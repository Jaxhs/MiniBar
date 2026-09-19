using System.IO;
using System.Windows;

namespace MiniBar.Sdk;

public enum PluginDropTarget
{
    /// <summary>拖到任务栏空白处。</summary>
    Bar,

    /// <summary>拖到某个任务栏图标上（<see cref="PluginDropContext.TargetPluginId"/> 为该图标对应插件）。</summary>
    BarItem,

    /// <summary>拖到迷你窗口上。</summary>
    MiniWindow,

    /// <summary>拖到插件管理器窗口上。</summary>
    PluginManager,
}

public sealed class PluginDropContext
{
    /// <summary>由宿主创建。</summary>
    public PluginDropContext(IPluginContext plugin, PluginDropTarget target, string? targetPluginId,
        IReadOnlyList<string> paths, Point screenPosition)
    {
        Plugin = plugin;
        Target = target;
        TargetPluginId = targetPluginId;
        Paths = paths;
        ScreenPosition = screenPosition;
    }

    public IPluginContext Plugin { get; }

    public PluginDropTarget Target { get; }

    public string? TargetPluginId { get; }

    /// <summary>拖入的文件/文件夹绝对路径。</summary>
    public IReadOnlyList<string> Paths { get; }

    /// <summary>屏幕坐标（像素），可用于在落点附近弹窗。</summary>
    public Point ScreenPosition { get; }

    public bool HasFolders => Paths.Any(Directory.Exists);

    public bool HasFiles => Paths.Any(File.Exists);

    public string[] Extensions => Paths
        .Select(p => Path.GetExtension(p))
        .Where(e => !string.IsNullOrEmpty(e))
        .Select(e => e.ToLowerInvariant())
        .Distinct()
        .ToArray();

    public bool IsForSelf => TargetPluginId is null || TargetPluginId == Plugin.PluginId;

    /// <summary>被拖拽 DLL 的路径（仅当拖入单个 .dll 且宿主把它判定为待加载插件时非空）。</summary>
    public string? DllPath { get; set; }

    public bool IsPluginDll { get; set; }

    /// <summary>插件在 <see cref="IDropHandlerPlugin.OnDrop"/> 中置为 true 表示“我处理了，不要继续传递”。</summary>
    public bool Handled { get; set; }
}

/// <summary>
/// 能力：处理拖入的文件/文件夹。
/// 宿主固定询问顺序为：被拖到图标上时先问该图标对应的插件，再问其它插件（按固定顺序），
/// 第一个把 <see cref="PluginDropContext"/> 标记为已处理（<see cref="Handled"/> = true）的插件胜出。
/// 若无人处理，宿主走内置兜底（DLL → 加载插件，其它 → 提示路径并复制到用户插件目录）。
/// </summary>
public interface IDropHandlerPlugin
{
    /// <summary>能否处理。请保持快速、无副作用。</summary>
    bool CanHandle(PluginDropContext context);

    /// <summary>执行处理。可以先做，再设置 <see cref="PluginDropContext.Handled"/>。</summary>
    void OnDrop(PluginDropContext context);
}
