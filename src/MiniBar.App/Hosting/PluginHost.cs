using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows.Threading;
using MiniBar.App.Infrastructure;
using MiniBar.App.Interop;
using MiniBar.App.Services;
using MiniBar.Sdk;

namespace MiniBar.App.Hosting;

/// <summary>
/// 插件宿主：负责发现、加载、卸载、启用、禁用、删除与目录监视。
///
/// ════════════════════════════════════════════════════════════════════
/// 给新手的“插件从被发现到被卸载”完整流程（建议按顺序读一遍）
/// ════════════════════════════════════════════════════════════════════
/// 1. 扫描目录（RescanAsync）：枚举内置目录与用户目录下的 *.dll，对每个 DLL 单独丢进一个
///    “临时 ALC”（AssemblyLoadContext，.NET 里“加载 DLL”=把它登记进某个 ALC）。这个临时上下文
///    只做一件事：读出元数据（哪个类型实现了 IMinibarPlugin、带不带 [PluginManifest]、能力列表、
///    图标、名称），然后把上下文整个 Unload 掉。因为只读元数据、不实例化、不留引用，所以判断
///    “是不是插件”完全不会污染内存，也不需要真正加载插件代码。结果用纯字符串 PluginCandidate 保存。
/// 2. 合并（Merge）：把探测结果登记成一个 PluginDescriptor（宿主侧模型）。同一插件 ID 出现多份时，
///    先发现的那个保持正常，后来的只标成 IsDuplicate（重复文件，可在界面里删），绝不反向标记已经
///    正常的那个——早期版本写反过，导致同一 DLL 装两份时全部不加载。
/// 3. 加载（LoadInstance）：在 UI 线程为插件新建一个“可回收”的 ALC（isCollectible: true）。注意默认
///    ALC 永远不能卸载，所以每个插件必须单独建 ALC 才能做到“删掉插件真的释放内存”。插件 DLL 用
///    流式加载（LoadFromStream）而非 LoadFromAssemblyPath——这样宿主不占文件句柄，卸载后能立刻删除
///    DLL（副作用：插件里 Assembly.Location 为空）。加载时 MiniBar.Sdk 契约程序集必须共享（插件 ALC
///    遇到它 return null 交回默认上下文），否则类型标识不一致，instance is IMinibarPlugin 会恒为 false。
///    找到入口类型 → Activator.CreateInstance → instance.Initialize(facade)。
/// 4. 运行：宿主通过 facade（PluginContext）回调插件能力；点击事件由宿主先调插件 OnClick，插件若
///    在 OnClick 里调用 OpenPanel/TogglePanel 等会把 ClickHandled 置 true，宿主就不再套默认行为，
///    否则只要插件实现了 IPanelContentPlugin，宿主自动 toggle 面板。
/// 5. 卸载（UnloadInstance）：调用插件 Dispose、把上下文 Unload()。但 Unload() 只是“标记可回收”，
///    插件里只要有活对象（静态字段、没停的计时器、事件订阅）就回收不了。所以这里主动 GC.Collect +
///    WaitForPendingFinalizers 催两轮，再 TrimMemory（EmptyWorkingSet 把内存页还给系统，实测 109MB→12MB）。
/// 6. 文件消失（僵尸路径）：卸载实例、把描述符移出列表，但【保留】持久化状态（Enabled/Pinned/Order），
///    这样文件放回来能恢复原来的固定位置。
///
/// 关键概念速查：
///   · AssemblyLoadContext（ALC）：决定①从哪找依赖②同名程序集算不算同一类型③以后能不能卸载。
///   · 契约共享：MiniBar.Sdk 在两个 ALC 间必须共享，否则类型身份不一致。
///   · 流式加载：LoadFromStream 不锁文件，卸载后可立即删 DLL；代价是 Assembly.Location 为空。
///   · collectible ALC：只有引用彻底归零才会真回收，所以卸载要配合 GC。
///   · 僵尸路径清单：文件删了但要留状态。
///
/// 线程模型（很重要）：
///   · 文件枚举与程序集探测放在后台线程（只读元数据，不碰 UI）；
///   · 插件实例的创建、Initialize、以及所有能力回调都在 UI 线程；
///   · ObservableCollection 的增删只在 UI 线程执行（WPF 界面对象有线程亲和性，跨线程改会抛异常）。
///
/// 内存模型：
///   · 每个插件一个可回收 AssemblyLoadContext；
///   · 插件程序集以字节流方式加载，不锁文件，卸载后可立即删除；
///   · 卸载后强制走一轮 GC，确保 collectible ALC 真的被回收；
///   · 禁用的插件完全不加载 —— 不占内存、不占句柄。
/// </summary>
public sealed class PluginHost : IDisposable
{
    private readonly SettingsService _settings;
    private readonly PluginStateStore _store;
    private readonly Dispatcher _dispatcher;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly HashSet<string> _dirtyVisuals = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _visualCoalesceTimer;

    private bool _disposed;
    private bool _initialized;

    public PluginHost(SettingsService settings)
    {
        _settings = settings;
        _store = new PluginStateStore(settings);
        _dispatcher = Dispatcher.CurrentDispatcher;

        // 同一帧内可能有很多插件请求刷新图标，合并成一次
        _visualCoalesceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(60),
        };
        _visualCoalesceTimer.Tick += (_, _) => FlushDirtyVisuals();
    }

    public ObservableCollection<PluginDescriptor> Plugins { get; } = new();

    /// <summary>固定项，按 Order 排序。任务栏显示区就绑它。</summary>
    public IEnumerable<PluginDescriptor> PinnedPlugins => Plugins
        .Where(p => p.IsPinned && p.IsEnabled && !p.IsDuplicate)
        .OrderBy(p => p.Order);

    public IEnumerable<PluginDescriptor> OverflowPlugins => Plugins
        .Where(p => !p.IsPinned && p.IsEnabled && !p.IsDuplicate)
        .OrderBy(p => p.Order);

    public event EventHandler<PluginDescriptor>? PluginLoaded;

    public event EventHandler<PluginDescriptor>? PluginUnloaded;

    /// <summary>集合内容或固定顺序变化（任务栏需要重排 UI）。</summary>
    public event EventHandler? LayoutChanged;

    /// <summary>插件目录出现/消失 DLL。</summary>
    public event EventHandler<string>? PluginFileMissing;

    public string UserPluginDirectory => AppPaths.UserPluginDirectory;

    public string BuiltInPluginDirectory => AppPaths.BuiltInPluginDirectory;

    // ---------------------------------------------------------------- 启动

    public async Task StartAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        AppPaths.EnsureCreated();

        await RescanAsync().ConfigureAwait(true);

        if (_settings.Settings.WatchPluginFolders)
        {
            StartWatchers();
        }
    }

    private void StartWatchers()
    {
        AddWatcher(AppPaths.BuiltInPluginDirectory);
        AddWatcher(AppPaths.UserPluginDirectory);
    }

    /// <summary>
    /// 给单个目录挂一个 FileSystemWatcher：只看 *.dll，关注文件名/最后写入/大小变化，监听创建·删除·重命名·修改四类事件。
    /// 目录不存在时跳过；建监视器失败（权限等）只记日志不抛，保证宿主不被个别目录拖垮。
    /// </summary>
    private void AddWatcher(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            var watcher = new FileSystemWatcher(directory, "*.dll")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };

            watcher.Created += OnPluginFolderChanged;
            watcher.Deleted += OnPluginFolderChanged;
            watcher.Renamed += OnPluginFolderChanged;
            watcher.Changed += OnPluginFolderChanged;
            _watchers.Add(watcher);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"监视插件目录失败：{directory}", ex);
        }
    }

    private DateTime _lastWatchEvent = DateTime.MinValue;

    private void OnPluginFolderChanged(object sender, FileSystemEventArgs e)
    {
        // 简单防抖：文件复制过程中会连续触发多次
        var now = DateTime.UtcNow;
        if ((now - _lastWatchEvent).TotalMilliseconds < 400)
        {
            return;
        }

        _lastWatchEvent = now;
        AppLog.Info($"插件目录变化：{e.ChangeType} {Path.GetFileName(e.FullPath)}");

        _dispatcher.BeginInvoke(new Action(() =>
        {
            _ = RescanAsync();
        }), DispatcherPriority.Background);
    }

    // ---------------------------------------------------------------- 扫描

    /// <summary>
    /// 重新扫描两个插件目录（内置 + 用户）：加入新插件、剔除文件已消失的插件。
    /// 这是“热插拔”的核心入口，被首次启动（StartAsync）、目录监视回调、拖入 DLL 等共用。
    ///
    /// 分步骤：
    ///   1. 枚举两个目录的候选 DLL（排除 MiniBar.Sdk.dll、MiniBar.dll、*.resources.dll）；
    ///   2. 用 HashSet 找出“磁盘上有但集合里还没有”的新文件；
    ///   3. 在后台线程（Task.Run）里逐个 Probe —— 只读元数据、用临时 ALC 探测后立刻 Unload，
    ///      不污染内存、不锁文件。读磁盘很慢，所以放后台，避免卡 UI；
    ///   4. 回到 UI 线程，按“同一 ID 修改时间新的优先”合并（Merge）；
    ///   5. 文件已消失的插件：卸载实例、移出列表、保留持久化状态（僵尸路径清单）；
    ///   6. 对“启用且未加载且非重复”的补做加载，并触发 LayoutChanged 让任务栏重排。
    ///
    /// 为什么放回 UI 线程再做合并/加载：WPF 的 ObservableCollection 与界面对象有线程亲和性，
    /// 跨线程增删会直接抛异常；而探测是纯只读元数据，放后台最划算。
    /// </summary>
    public async Task RescanAsync()
    {
        var directories = new[] { AppPaths.BuiltInPluginDirectory, AppPaths.UserPluginDirectory };
        var files = new List<string>();

        foreach (var directory in directories)
        {
            foreach (var file in PluginScanner.EnumerateCandidateFiles(directory))
            {
                files.Add(file);
            }
        }

        var knownPaths = new HashSet<string>(Plugins.Select(p => p.FilePath), StringComparer.OrdinalIgnoreCase);
        var newFiles = files.Where(f => !knownPaths.Contains(f)).ToArray();

        // 后台探测（只读元数据 + 卸载探测上下文）
        var probed = await Task.Run(() =>
        {
            var list = new List<PluginCandidate>();
            foreach (var file in newFiles)
            {
                var candidate = PluginScanner.Probe(file, out _);
                if (candidate is not null)
                {
                    list.Add(candidate);
                }
            }

            return list;
        }).ConfigureAwait(true);

        if (_disposed)
        {
            return;
        }

        var changed = false;

        // 同一个插件 ID 存在多份时，让修改时间更新的那份先合并（先合并者胜出）
        foreach (var candidate in OrderByNewestPerId(probed))
        {
            changed |= Merge(candidate);
        }

        // 文件被删除的插件：卸载并移出列表（保留状态，文件放回来时自动恢复固定位置）
        var present = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        var removed = Plugins.Where(p => !present.Contains(p.FilePath)).ToArray();
        foreach (var descriptor in removed)
        {
            AppLog.Info($"插件文件已消失，卸载：{descriptor.Name}（{descriptor.FilePath}）");
            UnloadInstance(descriptor);
            Plugins.Remove(descriptor);
            PluginFileMissing?.Invoke(this, descriptor.Id);
            changed = true;
        }

        if (changed)
        {
            NormalizeOrder();
            foreach (var descriptor in Plugins)
            {
                if (descriptor.IsEnabled && !descriptor.IsLoaded && !descriptor.IsDuplicate)
                {
                    LoadInstance(descriptor);
                }
            }

            LayoutChanged?.Invoke(this, EventArgs.Empty);
            AppLog.Info($"插件清单刷新完成：共 {Plugins.Count} 个（固定 {PinnedPlugins.Count()} 个，已加载 {Plugins.Count(p => p.IsLoaded)} 个）");
        }

        _store.Save();
    }

    /// <summary>
    /// 合并一个探测到的插件：把它变成宿主侧模型 PluginDescriptor 并登记进集合。
    ///
    /// 分步骤：
    ///   1. 找一个“同 ID 且非重复”的已存在插件；
    ///   2. 若存在且两份文件【内容相同】（文件名+长度+修改时间一致，认为是同一 DLL 的副本，
    ///      比如 Debug/Release 两个输出目录各一份）→ 静默忽略，不重复登记；
    ///   3. 若存在但内容不同 → 新建一个 IsDuplicate=true 的副本登记：**保留先发现的那个**，只把新来的
    ///      标成“重复文件”，后者不加载、可在界面里删除。绝不去动已经正常加载的那个；
    ///   4. 若不存在 → 用 PluginStateStore.GetOrCreate 取/建持久化状态（Enabled/Pinned/Order），建成
    ///      正常插件登记进 ObservableCollection。
    ///
    /// 为什么“先发现者胜出、绝不反向标记”：早期版本把后来者当成正常、把已加载的标成重复，结果同一 DLL
    /// 装两份时本该正常的那个也被误判，导致全部插件都不加载。这个顺序（内置 → 用户）是稳定且正确的。
    /// </summary>
    private bool Merge(PluginCandidate candidate)
    {
        var existing = Plugins.FirstOrDefault(p =>
            string.Equals(p.Id, candidate.Id, StringComparison.OrdinalIgnoreCase) && !p.IsDuplicate);

        if (existing is not null)
        {
            // 同一个 DLL 的副本（比如 Debug/Release 两个输出目录各一份）：内容一致就静默忽略
            if (IsSameContent(existing.FilePath, candidate.FilePath))
            {
                AppLog.Info($"忽略内容相同的重复副本：{candidate.Id}（{candidate.FilePath}）");
                return false;
            }

            var duplicate = new PluginDescriptor(candidate)
            {
                IsEnabled = false,
                IsDuplicate = true,
                Error = $"插件 ID 与已加载的 {existing.FilePath} 冲突，本文件未被加载",
            };

            Plugins.Add(duplicate);
            AppLog.Warn($"插件 ID 冲突：{candidate.Id} 同时存在于 {existing.FilePath} 与 {candidate.FilePath}；" +
                        "保留先发现的，另一个标为重复。删掉多余的那份即可消除此提示。");
            return true;
        }

        var descriptor = new PluginDescriptor(candidate);
        var state = _store.GetOrCreate(candidate);
        descriptor.IsEnabled = state.Enabled;
        descriptor.IsPinned = state.Pinned;
        descriptor.Order = state.Order;

        Plugins.Add(descriptor);
        AppLog.Info($"发现插件：{candidate.Name}（{candidate.Id}，能力：{string.Join("/", candidate.Capabilities)}）");

        return true;
    }

    /// <summary>同一插件 ID 的多份文件里，按修改时间新的优先排序；各 ID 之间维持原有顺序。</summary>
    private static IEnumerable<PluginCandidate> OrderByNewestPerId(IEnumerable<PluginCandidate> candidates)
    {
        foreach (var group in candidates.GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var candidate in group.OrderByDescending(c => GetLastWriteTimeUtc(c.FilePath)))
            {
                yield return candidate;
            }
        }
    }

    private static DateTime GetLastWriteTimeUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>文件名 + 长度 + 修改时间都一致，就认为两份文件是同一个 DLL 的副本。</summary>
    private static bool IsSameContent(string left, string right)
    {
        try
        {
            if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!File.Exists(left) || !File.Exists(right) ||
                !string.Equals(Path.GetFileName(left), Path.GetFileName(right), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var a = new FileInfo(left);
            var b = new FileInfo(right);
            return a.Length == b.Length && a.LastWriteTimeUtc == b.LastWriteTimeUtc;
        }
        catch
        {
            return false;
        }
    }

    private void NormalizeOrder()
    {
        // 重复项不参与排序，也别去动共享的持久化状态
        var ordered = Plugins.Where(p => !p.IsDuplicate).OrderBy(p => p.Order).ToArray();
        for (var i = 0; i < ordered.Length; i++)
        {
            if (ordered[i].Order != i)
            {
                ordered[i].Order = i;
                if (_store.TryGet(ordered[i].Id, out var state))
                {
                    state.Order = i;
                }
            }
        }

        var tail = ordered.Length;
        foreach (var duplicate in Plugins.Where(p => p.IsDuplicate))
        {
            duplicate.Order = tail++;
        }
    }

    // ---------------------------------------------------------------- 加载 / 卸载

    /// <summary>
    /// 加载插件实例并调用 Initialize（必须在 UI 线程执行）。
    ///
    /// 分步骤：
    ///   1. 防御：已加载或重复文件直接返回；失败路径会把已建起来的上下文拆干净（见 catch）；
    ///   2. 新建可回收 ALC：new PluginLoadContext(descriptor.FilePath) —— 注意默认 ALC 永远不能卸载，
    ///      所以每个插件必须单独建 isCollectible: true 的 ALC，将来才能真的释放内存；
    ///   3. 流式加载主程序集：LoadMainAssembly() 内部用 LoadFromStream 把 DLL 字节读进内存。好处是宿主
    ///      不占文件句柄，卸载后能立刻删除 DLL；代价是插件里 Assembly.Location 为空（别用它当物理路径，
    ///      要用 descriptor.FilePath / PluginContext.PluginDirectory）；
    ///   4. 定位入口类型：优先用清单里记录的类型全名，找不到再退化为“第一个实现 IMinibarPlugin 的公开非抽象类”；
    ///   5. 契约共享：插件 ALC 遇到 MiniBar.Sdk 会 return null 交回默认上下文。这点必须保证——否则插件 ALC
    ///      与宿主各有一份 SDK 类型，instance is IMinibarPlugin 会恒为 false，后面 New 出来的实例套不进去；
    ///   6. Activator.CreateInstance + instance.Initialize(facade)：facade 是 PluginContext，插件此后只能
    ///      通过它触达宿主；Initialize 里插件通常会建计时器/订阅事件，这些都必须在 UI 线程，否则挂；
    ///   7. 登记引用（LoadContext/Assembly/Instance/Facade）、触发 PluginLoaded 事件、通知状态变化。
    ///
    /// 为什么 Initialize 必须在 UI 线程：WPF 界面对象有线程亲和性，插件常在那里创建控件或订阅 Dispatcher，
    /// 跨线程会抛 InvalidOperationException。本方法由 RescanAsync/SetEnabled 等在 UI 线程调用。
    /// </summary>
    public bool LoadInstance(PluginDescriptor descriptor)
    {
        if (descriptor.IsLoaded || descriptor.IsDuplicate)
        {
            return descriptor.IsLoaded;
        }

        PluginLoadContext? context = null;
        try
        {
            context = new PluginLoadContext(descriptor.FilePath);
            var assembly = context.LoadMainAssembly();

            var type = assembly.GetType(descriptor.Candidate.PluginTypeName, throwOnError: false)
                       ?? assembly.GetTypes().FirstOrDefault(t =>
                           !t.IsAbstract && typeof(IMinibarPlugin).IsAssignableFrom(t));

            if (type is null)
            {
                throw new InvalidOperationException("找不到插件入口类型（可能缺少 MiniBar.Sdk 引用）");
            }

            if (Activator.CreateInstance(type) is not IMinibarPlugin instance)
            {
                throw new InvalidOperationException($"{type.FullName} 无法实例化");
            }

            var facade = new PluginContext(this, descriptor);

            descriptor.LoadContext = context;
            descriptor.PluginAssembly = assembly;
            descriptor.Facade = facade;
            descriptor.Instance = instance;

            instance.Initialize(facade);

            descriptor.IsLoaded = true;
            descriptor.Error = null;
            RegisterHotkeys(descriptor);

            AppLog.Info($"插件已加载：{descriptor.Name}（{descriptor.Id}）");
            PluginLoaded?.Invoke(this, descriptor);
            descriptor.NotifyStateChanged();
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error($"插件加载失败：{descriptor.Name}（{descriptor.FilePath}）", ex);
            descriptor.Error = $"{ex.GetType().Name}: {ex.Message}";
            descriptor.IsLoaded = false;

            // 初始化失败也要把已经建起来的上下文拆干净
            try
            {
                descriptor.Instance?.Dispose();
            }
            catch
            {
                // ignore
            }

            descriptor.Instance = null;
            descriptor.Facade?.Dispose();
            descriptor.Facade = null;
            descriptor.PluginAssembly = null;
            descriptor.LoadContext = null;
            context?.Unload();
            descriptor.RefreshVisuals();
            return false;
        }
    }

    /// <summary>卸载插件（可选同时删除 DLL 文件）。</summary>
    /// <summary>
    /// 公开卸载入口：先关掉该插件的浮层面板（避免面板引用着已卸载的内容），再 UnloadInstance；
    /// deleteFile=true 时一并删除 DLL。最后刷新图标、通知状态变化、触发 LayoutChanged 让任务栏重排。
    /// 返回是否成功卸载（与删除结果做 & 合并）。
    /// </summary>
    public bool Unload(PluginDescriptor descriptor, bool deleteFile = false)
    {
        AppServices.Shell?.ClosePanel(descriptor.Id);

        var removed = UnloadInstance(descriptor);

        if (deleteFile)
        {
            removed &= DeletePluginFiles(descriptor);
        }

        descriptor.RefreshVisuals();
        descriptor.NotifyStateChanged();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    /// <summary>
    /// 真正执行卸载的小核心（被 Unload/SetEnabled(false)/Reload/Dispose 共用）。
    ///
    /// 分步骤：
    ///   1. 已无实例且无上下文 → 直接置 IsLoaded=false 返回（幂等）；否则先取消快捷键注册；
    ///   2. 调用 instance.Dispose()：插件应当在这里停掉计时器、断开事件订阅、释放非托管资源。这一步最关键——
    ///      只要插件里还残留任何“活引用”（静态字段、没停的 DispatcherTimer、仍被宿主事件持有的委托），
    ///      collectible ALC 就永远回收不了；
    ///   3. 把 Instance/Facade/Assembly 引用全部置 null，断开宿主侧到插件对象的引用链；
    ///   4. context.Unload()：只是“标记该 ALC 可回收”，并不会立刻回收内存；
    ///   5. 主动 GC.Collect() + WaitForPendingFinalizers() 催两轮：把“禁用/删除”之后本应释放的内存立刻降下来，
    ///      而不是等下一次不确定何时到来的 GC（否则任务管理器读数迟迟不降，用户以为没卸载干净）；
    ///   6. AppServices.TrimMemory()：EmptyWorkingSet 把刚空出来的内存页还给系统，任务管理器读数立刻掉下来
    ///      （实测空壳 + 4 插件约 109MB → 12MB）。
    ///
    /// 为什么必须手动催 GC：collectible ALC 的回收条件非常苛刻——所有从它加载的类型/实例都必须不可达。
    /// 我们没法保证前面那些 Dispose/Timer 一定做干净，所以主动催两轮是最稳妥的下限保障。
    /// </summary>
    private bool UnloadInstance(PluginDescriptor descriptor)
    {
        if (descriptor.Instance is null && descriptor.LoadContext is null)
        {
            descriptor.IsLoaded = false;
            return true;
        }

        UnregisterHotkeys(descriptor);

        try
        {
            descriptor.Instance?.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"插件 Dispose 抛异常：{descriptor.Id}", ex);
        }

        descriptor.Instance = null;
        descriptor.Facade?.Dispose();
        descriptor.Facade = null;
        descriptor.PluginAssembly = null;

        var context = descriptor.LoadContext;
        descriptor.LoadContext = null;

        try
        {
            context?.Unload();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"卸载插件上下文失败：{descriptor.Id}", ex);
        }

        descriptor.IsLoaded = false;

        // collectible ALC 只有在完全没有引用时才会真正释放。这里主动催两次，
        // 让“禁用 / 删除”之后内存立刻降下来，而不是等下一次 GC。
        for (var i = 0; i < 2; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        // 再把刚空出来的内存页还给系统：任务管理器里的内存读数会立刻掉下来
        AppServices.TrimMemory();

        AppLog.Info($"插件已卸载：{descriptor.Name}（{descriptor.Id}）");
        PluginUnloaded?.Invoke(this, descriptor);
        return true;
    }

    /// <summary>
    /// 删除插件文件（DLL + 同名 .pdb）。只删两个插件目录内的文件，绝不碰用户自己的目录。
    ///
    /// 分步骤：
    ///   1. 安全检查 IsInsidePluginDirectory：只放行用户目录与内置目录内的文件。因为拖进来的 DLL 会先被
    ///      复制到用户插件目录再加载，所以宿主从不会持有用户原始文件的删除权，避免误删用户数据；
    ///   2. 删除 DLL 与同名 .pdb，做最多 12 次重试：卸载后文件句柄可能有极短的释放延迟（尤其防病毒扫描），
    ///      直接删会偶发 IOException，重试 + 短休眠能扛过这个窗口；
    ///   3. TryRemovePluginFolder：若插件放在用户目录的子目录且已空，把整个目录一起清掉（目录根/程序目录下的不动）；
    ///   4. 持久化状态处理：被删的是“重复文件”时【绝不】动状态——那份状态属于真正加载着的那一个，否则会把
    ///      正常插件的 Enabled/Pinned/Order 一起删掉；非重复的正常插件才 TryDropState（下次放回当全新插件）。
    ///
    /// 为什么重试删除：流式加载虽然不锁句柄，但 Windows + 杀软仍可能在毫秒级内短暂占用，单次删除容易失败。
    /// </summary>
    private bool DeletePluginFiles(PluginDescriptor descriptor)
    {
        var path = descriptor.FilePath;
        if (!IsInsidePluginDirectory(path))
        {
            AppLog.Warn($"出于安全考虑，拒绝删除插件目录之外的文件：{path}");
            descriptor.Error = "文件不在 MiniBar 插件目录内，仅卸载未删除";
            return false;
        }

        var ok = true;

        foreach (var target in new[] { path, Path.ChangeExtension(path, ".pdb") })
        {
            if (!File.Exists(target))
            {
                continue;
            }

            // 卸载后文件句柄可能还有极短的释放延迟，做几次重试
            for (var attempt = 0; attempt < 12; attempt++)
            {
                try
                {
                    File.Delete(target);
                    break;
                }
                catch (IOException) when (attempt < 11)
                {
                    System.Threading.Thread.Sleep(80);
                }
                catch (UnauthorizedAccessException) when (attempt < 11)
                {
                    System.Threading.Thread.Sleep(80);
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"删除失败：{target}", ex);
                    ok = false;
                    break;
                }
            }
        }

        // 如果插件放在用户目录的一个独立子目录里（且只剩插件产物），把整个目录一起清掉
        TryRemovePluginFolder(path);

        // 状态记录一并移除（下次放回该 DLL 会当作新插件，默认固定）
        // 但被删的是"重复文件"时不能动：那份状态属于真正加载着的那一个
        if (!descriptor.IsDuplicate)
        {
            TryDropState(descriptor.Id);
        }

        return ok;
    }

    /// <summary>
    /// 安全判定：path 是否落在用户插件目录或内置插件目录之内（用完整路径 + 目录分隔符比对，防 ../ 越界）。
    /// 删除操作的前置闸门——只在“圈定范围内”才允许动文件。
    /// </summary>
    private static bool IsInsidePluginDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        foreach (var root in new[] { AppPaths.UserPluginDirectory, AppPaths.BuiltInPluginDirectory })
        {
            var rootFull = Path.GetFullPath(root);
            if (full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetDirectoryName(full), rootFull, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void TryRemovePluginFolder(string pluginDllPath)
    {
        try
        {
            var folder = Path.GetDirectoryName(pluginDllPath);
            if (folder is null)
            {
                return;
            }

            var userRoot = Path.GetFullPath(AppPaths.UserPluginDirectory);
            var full = Path.GetFullPath(folder);
            if (!full.StartsWith(userRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return; // 目录根或程序目录下的插件不动目录
            }

            if (Directory.EnumerateFileSystemEntries(full).Any())
            {
                return; // 还有别的东西，保守起见不删
            }

            Directory.Delete(full);
            AppLog.Info($"已删除空插件目录：{full}");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"删除插件目录失败：{pluginDllPath}", ex);
        }
    }

    private void TryDropState(string pluginId)
    {
        if (_store.TryGet(pluginId, out var state))
        {
            state.Enabled = false;
            _store.Save();
        }

        _settings.DropPluginSettings(pluginId);
    }

    // ---------------------------------------------------------------- 用户操作

    /// <summary>
    /// 按路径加载插件（拖入 DLL / 命令行参数 / IShellService.LoadPlugin 都走这里）。
    /// 若文件在插件目录之外，会先复制一份到用户插件目录，避免宿主持有外部文件句柄或误删用户文件。
    /// </summary>
    public string? LoadPluginFile(string dllPath, out string? message)
    {
        message = null;

        if (!File.Exists(dllPath))
        {
            message = "文件不存在";
            return null;
        }

        if (!string.Equals(Path.GetExtension(dllPath), ".dll", StringComparison.OrdinalIgnoreCase))
        {
            message = "只支持 .dll 插件";
            return null;
        }

        var targetPath = dllPath;
        if (!IsInsidePluginDirectory(dllPath))
        {
            targetPath = CopyIntoUserPluginDirectory(dllPath);
            AppLog.Info($"插件已复制到用户插件目录：{targetPath}");
        }

        // 已经加载过同一个文件？
        var existing = Plugins.FirstOrDefault(p =>
            string.Equals(p.FilePath, targetPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (!existing.IsEnabled)
            {
                SetEnabled(existing, true);
                message = $"已启用已存在的插件：{existing.Name}";
                return existing.Id;
            }

            if (!existing.IsLoaded)
            {
                LoadInstance(existing);
                message = $"已重新加载插件：{existing.Name}";
                return existing.Id;
            }

            message = $"插件已在运行：{existing.Name}";
            return existing.Id;
        }

        var candidate = PluginScanner.Probe(targetPath, out var probeError);
        if (candidate is null)
        {
            message = string.IsNullOrEmpty(probeError)
                ? "该 DLL 不是 MiniBar 插件（未找到实现 IMinibarPlugin 且带 [PluginManifest] 的类型）"
                : $"无法作为插件加载：{probeError}";
            return null;
        }

        // 同一个插件 ID 已经存在？比较修改时间，更新的那份胜出 —— 于是"拖入新版本 DLL"就是一次替换
        var known = Plugins.FirstOrDefault(p =>
            string.Equals(p.Id, candidate.Id, StringComparison.OrdinalIgnoreCase) && !p.IsDuplicate);

        if (known is not null)
        {
            if (IsSameContent(known.FilePath, candidate.FilePath))
            {
                message = $"内容相同，插件已在运行：{known.DisplayName}";
                return known.Id;
            }

            if (File.GetLastWriteTimeUtc(candidate.FilePath) <= File.GetLastWriteTimeUtc(known.FilePath))
            {
                message = $"插件 ID「{candidate.Id}」已由更新的 {Path.GetFileName(known.FilePath)} 提供，本次文件未加载";
                return null;
            }

            AppLog.Info($"插件 {candidate.Id} 检测到更新的文件，替换旧版本：{known.FilePath} -> {candidate.FilePath}");

            // 先彻底卸载旧实例，再让下面的 Merge 接上新的 —— 固定顺序/启用状态都按 ID 保留
            Unload(known, deleteFile: false);
            Plugins.Remove(known);
        }

        Merge(candidate);
        NormalizeOrder();

        var descriptor = Plugins.First(p => string.Equals(p.Id, candidate.Id, StringComparison.OrdinalIgnoreCase));
        descriptor.IsEnabled = true;
        if (_store.TryGet(descriptor.Id, out var state))
        {
            state.Enabled = true;
            _store.Save();
        }

        LoadInstance(descriptor);
        LayoutChanged?.Invoke(this, EventArgs.Empty);

        message = descriptor.HasError ? $"加载失败：{descriptor.Error}" : $"已加载插件：{descriptor.Name}";
        return descriptor.Id;
    }

    /// <summary>
    /// 把“插件目录之外”的 DLL 复制到用户插件目录。原因：宿主加载后不希望持有用户原始文件的句柄，也避免误删用户数据；
    /// 同名且内容一致时直接复用；文件名冲突时自动改名（name (2).dll …）。复制后再走正常扫描/加载流程。
    /// </summary>
    private static string CopyIntoUserPluginDirectory(string sourcePath)
    {
        Directory.CreateDirectory(AppPaths.UserPluginDirectory);
        var fileName = Path.GetFileName(sourcePath);
        var target = Path.Combine(AppPaths.UserPluginDirectory, fileName);

        if (File.Exists(target))
        {
            var sourceInfo = new FileInfo(sourcePath);
            var targetInfo = new FileInfo(target);
            if (sourceInfo.Length == targetInfo.Length && sourceInfo.LastWriteTimeUtc == targetInfo.LastWriteTimeUtc)
            {
                return target; // 内容看起来一致，直接用
            }

            var name = Path.GetFileNameWithoutExtension(fileName);
            for (var i = 2; i < 1000; i++)
            {
                var candidate = Path.Combine(AppPaths.UserPluginDirectory, $"{name} ({i}).dll");
                if (!File.Exists(candidate))
                {
                    target = candidate;
                    break;
                }
            }
        }

        File.Copy(sourcePath, target, overwrite: true);
        return target;
    }

    public void SetEnabled(PluginDescriptor descriptor, bool enabled)
    {
        if (descriptor.IsEnabled == enabled)
        {
            return;
        }

        descriptor.IsEnabled = enabled;

        if (_store.TryGet(descriptor.Id, out var state))
        {
            state.Enabled = enabled;
            _store.Save();
        }

        if (enabled)
        {
            LoadInstance(descriptor);
        }
        else
        {
            UnloadInstance(descriptor);
        }

        descriptor.RefreshVisuals();
        descriptor.NotifyStateChanged();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetPinned(PluginDescriptor descriptor, bool pinned)
    {
        if (descriptor.IsPinned == pinned)
        {
            return;
        }

        descriptor.IsPinned = pinned;

        if (_store.TryGet(descriptor.Id, out var state))
        {
            state.Pinned = pinned;
            state.Order = pinned ? int.MaxValue : state.Order; // 新固定的排在最后
            _store.Save();
        }

        NormalizeOrder();
        _store.Save();
        descriptor.NotifyStateChanged();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>按给定顺序重排固定项（拖拽结束 / 固定顺序变化时调用）。</summary>
    /// <summary>
    /// 拖拽重排后调用：按用户给的顺序把“固定项”的 Order 重写为 0..n-1，未固定的排在后面并顺延 Order，
    /// 全部写盘并触发 LayoutChanged。保证插件管理器/任务栏的显示顺序与磁盘状态一致。
    /// </summary>
    public void ApplyPinnedOrder(IReadOnlyList<PluginDescriptor> orderedPinned)
    {
        for (var i = 0; i < orderedPinned.Count; i++)
        {
            if (_store.TryGet(orderedPinned[i].Id, out var state))
            {
                state.Order = i;
                state.Pinned = true;
            }

            orderedPinned[i].Order = i;
        }

        var next = orderedPinned.Count;
        foreach (var descriptor in Plugins.Where(p => !p.IsPinned).OrderBy(p => p.Order))
        {
            descriptor.Order = next++;
            if (_store.TryGet(descriptor.Id, out var state))
            {
                state.Order = descriptor.Order;
            }
        }

        _store.Save();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Reload(PluginDescriptor descriptor)
    {
        UnloadInstance(descriptor);
        LoadInstance(descriptor);
        descriptor.RefreshVisuals();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------------------------------------------------------------- 图标刷新

    internal void InvalidateBarItem(PluginDescriptor descriptor)
    {
        lock (_dirtyVisuals)
        {
            _dirtyVisuals.Add(descriptor.Id);
        }

        if (!_visualCoalesceTimer.IsEnabled)
        {
            _visualCoalesceTimer.Start();
        }
    }

    private void FlushDirtyVisuals()
    {
        _visualCoalesceTimer.Stop();

        string[] ids;
        lock (_dirtyVisuals)
        {
            ids = _dirtyVisuals.ToArray();
            _dirtyVisuals.Clear();
        }

        foreach (var id in ids)
        {
            var descriptor = Plugins.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            descriptor?.RefreshVisuals();
        }
    }

    // ---------------------------------------------------------------- 快捷键

    /// <summary>
    /// 注册插件声明的全局快捷键。逐条调用 HotkeyManager.Register；若被系统/其它程序占用注册失败，
    /// 通过插件的 OnHotkeyRegistrationFailed 回调告知插件（而不是静默失败）。无快捷键能力或宿主未就绪则跳过。
    /// </summary>
    private void RegisterHotkeys(PluginDescriptor descriptor)
    {
        if (descriptor.Hotkeys is not { } hotkeys || AppServices.Hotkeys is null)
        {
            return;
        }

        try
        {
            foreach (var hotkey in hotkeys.GetHotkeys())
            {
                var ok = AppServices.Hotkeys.Register(descriptor.Id, hotkey.Id, hotkey.Modifiers, hotkey.Key, hotkey.DisplayName);
                if (!ok)
                {
                    hotkeys.OnHotkeyRegistrationFailed(hotkey.Id, "快捷键注册失败（可能已被占用）");
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"注册插件快捷键失败：{descriptor.Id}", ex);
        }
    }

    private void UnregisterHotkeys(PluginDescriptor descriptor)
    {
        // HotkeyManager 按插件为单位整体注销再重建，代价很低
        AppServices.Hotkeys?.UnregisterPlugin(descriptor.Id);
    }

    /// <summary>
    /// 宿主主题切换后广播给所有插件：逐个通过各插件的 facade 触发 ThemeChanged 事件，
    /// 让插件界面（使用 DynamicResource 绑定语义色）与宿主一起换肤。被 ShellService.OnThemeChanged 调用。
    /// </summary>
    internal void RaiseThemeChanged()
    {
        foreach (var descriptor in Plugins)
        {
            descriptor.Facade?.RaiseThemeChanged();
        }
    }

    // ---------------------------------------------------------------- 查询

    public IReadOnlyList<PluginInfo> GetPluginInfos() => Plugins.Select(ToInfo).ToArray();

    private static PluginInfo ToInfo(PluginDescriptor descriptor) => new(
        descriptor.Id,
        descriptor.DisplayName,
        descriptor.Version,
        descriptor.FilePath,
        descriptor.IsEnabled,
        descriptor.IsLoaded,
        descriptor.IsPinned,
        descriptor.Capabilities,
        descriptor.Error);

    public PluginDescriptor? Find(string pluginId) =>
        Plugins.FirstOrDefault(p => string.Equals(p.Id, pluginId, StringComparison.OrdinalIgnoreCase));

    /// <summary>按固定顺序 + 未固定顺序返回所有启用的插件，用于菜单聚合。</summary>
    public IEnumerable<PluginDescriptor> GetEnabledPlugins() =>
        Plugins.Where(p => p.IsEnabled && !p.IsDuplicate).OrderBy(p => p.Order);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _visualCoalesceTimer.Stop();

        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        _watchers.Clear();

        foreach (var descriptor in Plugins.ToArray())
        {
            UnloadInstance(descriptor);
        }

        _store.Save();
        _settings.SaveNow();
    }
}
