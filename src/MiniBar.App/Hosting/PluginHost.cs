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
/// 线程模型（很重要）：
///   · 文件枚举与程序集探测放在后台线程（只读元数据，不碰 UI）；
///   · 插件实例的创建、Initialize、以及所有能力回调都在 UI 线程；
///   · ObservableCollection 的增删只在 UI 线程执行。
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
    /// 重新扫描两个插件目录：加入新插件、剔除文件已消失的插件。
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

        foreach (var candidate in probed)
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

    private bool Merge(PluginCandidate candidate)
    {
        var existing = Plugins.FirstOrDefault(p => string.Equals(p.Id, candidate.Id, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            if (!string.Equals(existing.FilePath, candidate.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                existing.IsDuplicate = true;
                existing.Error = $"插件 ID 与 {existing.FilePath} 重复，已忽略本文件";
                AppLog.Warn($"插件 ID 冲突：{candidate.Id} 同时出现在 {existing.FilePath} 和 {candidate.FilePath}");
            }

            return false;
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

    private void NormalizeOrder()
    {
        var ordered = Plugins.OrderBy(p => p.Order).ToArray();
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
    }

    // ---------------------------------------------------------------- 加载 / 卸载

    /// <summary>加载插件实例并调用 Initialize（必须在 UI 线程）。</summary>
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

    /// <summary>卸载插件（可选删除文件）。</summary>
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
    /// 删除插件文件。安全约束：只允许删除两个插件目录内的文件，
    /// 绝不会碰用户自己的目录（拖进来的 DLL 会先被复制到用户插件目录再加载）。
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
        TryDropState(descriptor.Id);

        return ok;
    }

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

        var known = Plugins.FirstOrDefault(p => string.Equals(p.Id, candidate.Id, StringComparison.OrdinalIgnoreCase));
        if (known is not null && !string.Equals(known.FilePath, candidate.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            message = $"插件 ID「{candidate.Id}」已由 {Path.GetFileName(known.FilePath)} 提供";
            return null;
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
