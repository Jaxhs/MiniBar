using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using MiniBar.App.Infrastructure;
using MiniBar.Sdk;

namespace MiniBar.App.Hosting;

/// <summary>扫描结果（纯字符串，不持有任何插件类型，因此探测用的 ALC 可以被完整回收）。</summary>
public sealed class PluginCandidate
{
    public required string FilePath { get; init; }

    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public string? Author { get; init; }

    public string? Version { get; init; }

    public string? IconSpec { get; init; }

    public int Order { get; init; }

    public bool DefaultPinned { get; init; }

    /// <summary>实现 IMinibarPlugin 的具体类型全名。</summary>
    public required string PluginTypeName { get; init; }

    public string[] Capabilities { get; init; } = Array.Empty<string>();
}

/// <summary>
/// 插件探测：判断“某个 DLL 到底是不是 MiniBar 插件”，并提取它的清单信息。
///
/// 核心设计——“探测不污染内存”：
///   · 探测时把 DLL 加载进一个【临时 AssemblyLoadContext（ALC）】。ALC 是 .NET 里“加载 DLL”的容器，决定从哪找依赖、
///     同名程序集算不算同一类型、以后能不能卸载。这里用的临时 ALC 是 isCollectible: true 的，读完元数据立刻 Unload；
///   · 整个探测只做“读类型、读特性、列能力”，【绝不 new 插件实例】，因此不需要插件的依赖也能判断（见下）；
///   · 结果用 PluginCandidate 这种纯字符串对象保存（id/名称/能力/图标），不持有任何来自插件 ALC 的类型或实例引用，
///     于是临时 ALC 一卸载就彻底没有牵挂，能被 GC 回收；
///   · 副作用：探测过程不会锁文件（不像 LoadFromAssemblyPath 会一直占着句柄），一个 DLL 哪怕不是插件、甚至已损坏，
///     也不会把东西留在内存里——它只是“路过”被看了一眼。
///
/// AssemblyDependencyResolver：PluginLoadContext 内部用它根据 deps.json / 同目录 DLL 解析依赖。
/// 但探测阶段其实只 GetTypes() 读元数据，并不真的解析/加载依赖，所以即便依赖缺失，也只是部分类型读不出（被 catch 容错）。
/// </summary>
public static class PluginScanner
{
    private static readonly string[] ProbeExclusions = { "MiniBar.Sdk.dll", "MiniBar.dll" };

    /// <summary>枚举插件目录下的候选 DLL（不递归进子目录里的依赖，只取顶层 + 一层子目录）。</summary>
    public static IEnumerable<string> EnumerateCandidateFiles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(directory, "*.dll", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"枚举插件目录失败：{directory}", ex);
            yield break;
        }

        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (ProbeExclusions.Any(e => string.Equals(e, name, StringComparison.OrdinalIgnoreCase)) ||
                name.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return file;
        }
    }

    /// <summary>
    /// 探测一个 DLL：在临时 ALC 里加载它、读出元数据、判断是否 MiniBar 插件。
    /// 返回 null 表示“不是 MiniBar 插件”（静默忽略，不打扰用户）；非 null 即 PluginCandidate（纯字符串结果）。
    ///
    /// 为什么用 try/finally + context.Unload()：无论探测成功还是抛异常（DLL 损坏/不是程序集），临时 ALC 都会被卸载，
    /// 不留任何引用在内存里——这正是“探测不污染内存”的保证。注意传入的路径必须是绝对路径，AssemblyDependencyResolver 才能解析。
    /// </summary>
    public static PluginCandidate? Probe(string dllPath, out string? error)
    {
        error = null;
        PluginLoadContext? context = null;

        try
        {
            // 目录里没有 deps.json 时 AssemblyDependencyResolver 也能工作；
            // 但需要保证路径是绝对路径。
            var fullPath = Path.GetFullPath(dllPath);
            context = new PluginLoadContext(fullPath);
            var assembly = context.LoadMainAssembly();
            return ProbeAssembly(assembly, fullPath, out error);
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            AppLog.Debug($"探测失败（可能不是插件）：{dllPath} - {error}");
            return null;
        }
        finally
        {
            context?.Unload();
        }
    }

    /// <summary>
    /// 单独一个方法并禁止内联：保证 <c>Type</c> / <c>Attribute</c> 等引用在方法返回后立刻变成不可达，
    /// 探测用的 ALC 才有机会被回收。
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static PluginCandidate? ProbeAssembly(Assembly assembly, string fullPath, out string? error)
    {
        error = null;

        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
            error = "部分类型加载失败：" +
                    string.Join("; ", ex.LoaderExceptions.Where(e => e is not null).Take(3).Select(e => e!.Message));
        }

        var pluginInterface = typeof(IMinibarPlugin);

        foreach (var type in types)
        {
            if (type is null || type.IsAbstract || type.IsInterface || !type.IsClass)
            {
                continue;
            }

            if (!pluginInterface.IsAssignableFrom(type))
            {
                continue;
            }

            var manifest = type.GetCustomAttribute<PluginManifestAttribute>(inherit: false);
            if (manifest is null)
            {
                AppLog.Warn($"{Path.GetFileName(fullPath)} 中的 {type.FullName} 实现了 IMinibarPlugin " +
                            "但缺少 [PluginManifest]，已忽略。");
                continue;
            }

            if (type.GetConstructor(Type.EmptyTypes) is null)
            {
                error = $"{type.FullName} 缺少公共无参构造函数";
                continue;
            }

            return new PluginCandidate
            {
                FilePath = fullPath,
                Id = manifest.Id,
                Name = manifest.Name,
                Description = manifest.Description,
                Author = manifest.Author,
                Version = manifest.Version,
                IconSpec = manifest.Icon,
                Order = manifest.Order,
                DefaultPinned = manifest.DefaultPinned,
                PluginTypeName = type.FullName!,
                Capabilities = DescribeCapabilities(type),
            };
        }

        return null;
    }

    /// <summary>用类型名做能力判断，避免为了列能力而实例化插件。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string[] DescribeCapabilities(Type type)
    {
        var list = new List<string>(6);

        if (typeof(ITaskButtonPlugin).IsAssignableFrom(type))
        {
            list.Add(PluginCapabilities.TaskButton);
        }

        if (typeof(IBarWidgetPlugin).IsAssignableFrom(type))
        {
            list.Add(PluginCapabilities.BarWidget);
        }

        if (typeof(IPanelContentPlugin).IsAssignableFrom(type))
        {
            list.Add(PluginCapabilities.Panel);
        }

        if (typeof(ICompactContentPlugin).IsAssignableFrom(type))
        {
            list.Add(PluginCapabilities.Compact);
        }

        if (typeof(IContextMenuPlugin).IsAssignableFrom(type))
        {
            list.Add(PluginCapabilities.ContextMenu);
        }

        if (typeof(IHotkeyPlugin).IsAssignableFrom(type))
        {
            list.Add(PluginCapabilities.Hotkey);
        }

        if (typeof(IDropHandlerPlugin).IsAssignableFrom(type))
        {
            list.Add(PluginCapabilities.DropHandler);
        }

        if (typeof(ISettingsPlugin).IsAssignableFrom(type))
        {
            list.Add(PluginCapabilities.Settings);
        }

        return list.ToArray();
    }
}
