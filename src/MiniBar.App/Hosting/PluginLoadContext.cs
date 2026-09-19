using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using MiniBar.App.Infrastructure;

namespace MiniBar.App.Hosting;

/// <summary>
/// 每个插件一个可回收（collectible）的 AssemblyLoadContext，这是“运行时卸载”的前提。
///
/// 两个关键设计：
/// 1) 契约程序集（MiniBar.Sdk）永远交给默认上下文解析。否则插件里编译期引用的
///    IMinibarPlugin 与宿主里的会是两个不同的 Type，<c>is IMinibarPlugin</c> 恒为 false。
///    做法：命中共享名时 <c>return null</c>，让运行时回退到默认上下文。
/// 2) 一律用字节流/流式加载托管程序集（LoadFromStream），而不是 LoadFromAssemblyPath。
///    这样插件 DLL 不会被本进程占用句柄 —— 卸载后可以立刻删除文件（真正的“热删除”）。
/// </summary>
public sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _pluginDirectory;

    public PluginLoadContext(string pluginPath)
        : base($"MiniBar.Plugin::{Path.GetFileNameWithoutExtension(pluginPath)}", isCollectible: true)
    {
        PluginPath = pluginPath;
        _pluginDirectory = Path.GetDirectoryName(pluginPath) ?? AppContext.BaseDirectory;
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    public string PluginPath { get; }

    /// <summary>契约程序集与其他插件共享的程序集名前缀。</summary>
    private static readonly string[] SharedPrefixes = { "MiniBar.Sdk", "MiniBar.Shell" };

    public static bool IsSharedAssembly(AssemblyName name)
    {
        var simpleName = name.Name;
        if (string.IsNullOrEmpty(simpleName))
        {
            return false;
        }

        foreach (var prefix in SharedPrefixes)
        {
            if (simpleName.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                simpleName.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // WPF / BCL 一律走默认上下文
        return simpleName.StartsWith("System.", StringComparison.OrdinalIgnoreCase) ||
               simpleName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ||
               simpleName.Equals("mscorlib", StringComparison.OrdinalIgnoreCase) ||
               simpleName.Equals("netstandard", StringComparison.OrdinalIgnoreCase) ||
               simpleName.Equals("WindowsBase", StringComparison.OrdinalIgnoreCase) ||
               simpleName.Equals("PresentationCore", StringComparison.OrdinalIgnoreCase) ||
               simpleName.Equals("PresentationFramework", StringComparison.OrdinalIgnoreCase);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (IsSharedAssembly(assemblyName))
        {
            return null; // 交回默认上下文
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        if (string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(assemblyName.Name))
        {
            // 没有 deps.json 时的回退：插件目录下的同名 DLL
            var candidate = Path.Combine(_pluginDirectory, assemblyName.Name + ".dll");
            if (File.Exists(candidate))
            {
                path = candidate;
            }
        }

        return string.IsNullOrEmpty(path) ? null : LoadFromBytes(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return string.IsNullOrEmpty(path) ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }

    /// <summary>流式加载：不占用文件句柄，卸载后可立即删除。</summary>
    public Assembly LoadFromBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return LoadFromStream(stream);
    }

    /// <summary>加载插件主程序集（同样走流式，避免文件被锁）。</summary>
    public Assembly LoadMainAssembly() => LoadFromBytes(PluginPath);
}
