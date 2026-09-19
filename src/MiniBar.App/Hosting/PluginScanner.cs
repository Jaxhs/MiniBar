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
/// 插件探测。只读元数据、不实例化、不留引用，探测完立刻卸载探测上下文。
/// 这样即使某个 DLL 只是普通的类库（甚至是有问题的 DLL），也不会污染内存，更不会锁文件。
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
    /// 探测一个 DLL。返回 null 表示“不是 MiniBar 插件”（静默忽略，不打扰用户）。
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

        return list.ToArray();
    }
}
