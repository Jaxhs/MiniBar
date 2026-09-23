using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using MiniBar.App.Infrastructure;

namespace MiniBar.App.Hosting;

/// <summary>
/// 每个插件一个「可回收的」程序集加载上下文（<see cref="AssemblyLoadContext"/>，简称 ALC）。
///
/// <para><b>先讲清楚 ALC 是什么（新手必读）</b></para>
/// <para>
/// .NET 里"加载一个 DLL"不是一个动作，而是一次"登记"：把程序集放进某个
/// <see cref="AssemblyLoadContext"/> 里。ALC 决定了三件事：
/// ① 从哪里找依赖；② 同名程序集算不算同一个类型；③ 这个程序集以后能不能被丢掉。
/// </para>
/// <para>
/// 默认情况下所有程序集都进"默认上下文"，而默认上下文<b>永远不能被卸载</b>。
/// 所以要想"删掉插件后真的释放内存"，就必须给每个插件单独建一个
/// <c>isCollectible: true</c> 的 ALC —— 这就是本类存在的唯一理由。
/// </para>
///
/// <para><b>这套实现里有两条铁律，破坏了热插拔就会失灵</b></para>
/// <list type="number">
///   <item>
///     <b>契约程序集必须共享。</b>
///     插件编译时引用的 <c>MiniBar.Sdk.dll</c> 是"宿主的那一份"。如果插件自己的 ALC
///     又去加载一份同名程序集，那么插件里的 <c>IMinibarPlugin</c> 与宿主里的
///     <c>IMinibarPlugin</c> 会成为**两个不同的类型**，宿主执行
///     <c>instance is IMinibarPlugin</c> 会得到 false，插件会被判定为"不是我的插件"。
///     做法：凡是共享名（SDK / BCL / WPF）一律 <c>return null</c>，
///     表示"我不负责，交给默认上下文去找"。
///   </item>
///   <item>
///     <b>用字节流加载，不要用路径加载。</b>
///     <c>LoadFromAssemblyPath</c> 会让本进程长期占用该 DLL 的文件句柄，
///     于是"卸载插件后删掉文件"就会失败（Windows 不允许删除被占用的文件）。
///     这里统一用 <see cref="LoadFromBytes"/>，读完即关。
///   </item>
/// </list>
///
/// <para><b>卸载为什么还要配合 GC？</b></para>
/// <para>
/// <see cref="AssemblyLoadContext.Unload"/> 只是"标记可以回收"。
/// 只要插件里还有任何对象活着（一个静态字段、一个没停的定时器、一个事件订阅），
/// 运行时就无法真正卸掉它。所以宿主在 Unload 之后会主动
/// <c>GC.Collect() + WaitForPendingFinalizers()</c> 催两轮 —— 见 PluginHost。
/// </para>
/// </summary>
public sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _pluginDirectory;

    /// <param name="pluginPath">插件 DLL 的完整路径。</param>
    public PluginLoadContext(string pluginPath)
        : base($"MiniBar.Plugin::{Path.GetFileNameWithoutExtension(pluginPath)}", isCollectible: true)
    {
        PluginPath = pluginPath;

        // 插件自己的目录：既用来兜底找依赖，也用来定位插件随包携带的资源文件
        _pluginDirectory = Path.GetDirectoryName(pluginPath) ?? AppContext.BaseDirectory;

        // AssemblyDependencyResolver 会读插件旁边的 *.deps.json，按里面的记录解析依赖；
        // 没有 deps.json 时下面的 Load 里有"同目录同名 DLL"的兜底。
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    /// <summary>正在托管的插件 DLL 路径。</summary>
    public string PluginPath { get; }

    /// <summary>契约程序集与其他插件共享的程序集名前缀。</summary>
    private static readonly string[] SharedPrefixes = { "MiniBar.Sdk", "MiniBar.Shell" };

    /// <summary>
    /// 判断某个程序集是否应该"共享"（也就是交回默认上下文解析）。
    ///
    /// <para>
    /// 规则很简单：SDK、整个 .NET 基础类库（System.*）、微软的程序集（Microsoft.*）、
    /// 以及 WPF 的三个核心程序集，都必须是全进程唯一的。其它（插件自己带的第三方库）
    /// 才由插件目录里的副本提供。
    /// </para>
    /// </summary>
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

    /// <summary>
    /// 运行时问我们"这个依赖去哪儿找"。返回 null = "我不知道，请默认上下文处理"。
    /// 这是 .NET 里最常用的程序集重定向钩子。
    /// </summary>
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

    /// <summary>非托管 DLL（例如插件调用的 C/C++ 库）的查找。</summary>
    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return string.IsNullOrEmpty(path) ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }

    /// <summary>
    /// 流式加载：把文件读成字节后交给运行时，<b>不占用文件句柄</b>。
    ///
    /// <para>
    /// <c>FileShare.ReadWrite | FileShare.Delete</c> 是双保险：即使别的东西正在写它，
    /// 我们也能读；而且允许别人删除它。
    /// </para>
    /// </summary>
    public Assembly LoadFromBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return LoadFromStream(stream);
    }

    /// <summary>加载插件主程序集（同样走流式，避免文件被锁）。</summary>
    public Assembly LoadMainAssembly() => LoadFromBytes(PluginPath);
}
