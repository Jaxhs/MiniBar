/// <summary>
/// 本文件定义“拖放处理”能力，以及拖放上下�? <see cref="PluginDropContext"/> 与拖放目标枚�? <see cref="PluginDropTarget"/>�?
///
/// <para>用户把文�?/文件夹（�? DLL）拖到任务栏时，宿主按顺序问插件“你能处理吗”，
/// 第一个把上下文标记为已处理（<see cref="PluginDropContext.Handled"/> = true）的插件胜出�?
/// 没人处理则走宿主内置兜底（DLL→加载插件，其它→复制到用户插件目录）�?</para>
///
/// <para><b>IReadOnlyList&lt;string&gt; 是什么：</b>一组“只能读”的拖入路径（文�?/文件夹绝对路径）�?
/// 你可以遍历、判断扩展名，但不要试图修改这个集合本身�?</para>
/// </summary>
using System.IO;
using System.Windows;

namespace MiniBar.Sdk;

public enum PluginDropTarget
{
    /// <summary>拖到任务栏空白处�?</summary>
    Bar,

    /// <summary>拖到某个任务栏图标上�?<see cref="PluginDropContext.TargetPluginId"/> 为该图标对应插件）�?</summary>
    BarItem,

    /// <summary>拖到迷你窗口上�?</summary>
    MiniWindow,

    /// <summary>拖到插件管理器窗口上�?</summary>
    PluginManager,
}

public sealed class PluginDropContext
{
    /// <summary>由宿主创建�?</summary>
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

    /// <summary>�ϵ����ĸ�λ�ã��հ״� / ͼ�� / ���㴰�� / ��������������� <see cref="PluginDropTarget"/>��</summary>
    public PluginDropTarget Target { get; }

    public string? TargetPluginId { get; }

    /// <summary>拖入的文�?/文件夹绝对路径�?</summary>
    public IReadOnlyList<string> Paths { get; }

    /// <summary>屏幕坐标（像素），可用于在落点附近弹窗�?</summary>
    public Point ScreenPosition { get; }

    /// <summary>拖入内容里是否至少包含一个文件夹�?</summary>
    public bool HasFolders => Paths.Any(Directory.Exists);

    /// <summary>拖入内容里是否至少包含一个文件�?</summary>
    public bool HasFiles => Paths.Any(File.Exists);

    /// <summary>所有拖入项的去重扩展名（小写、含点，例如 ".png"），便于按类型过滤�?</summary>
    public string[] Extensions => Paths
        .Select(p => Path.GetExtension(p))
        .Where(e => !string.IsNullOrEmpty(e))
        .Select(e => e.ToLowerInvariant())
        .Distinct()
        .ToArray();

    public bool IsForSelf => TargetPluginId is null || TargetPluginId == Plugin.PluginId;

    /// <summary>被拖�? DLL 的路径（仅当拖入单个 .dll 且宿主把它判定为待加载插件时非空）�?</summary>
    public string? DllPath { get; set; }

    /// <summary>�����Ƿ����ж�������Ǵ����ز�������� .dll����</summary>
    /// <summary>�����Ƿ����ж�������Ǵ����ز�������� .dll����</summary>
    public bool IsPluginDll { get; set; }

    /// <summary>插件�? <see cref="IDropHandlerPlugin.OnDrop"/> 中置�? true 表示“我处理了，不要继续传递”�?</summary>
    public bool Handled { get; set; }
}

/// <summary>
/// 能力：处理拖入的文件/文件夹�?
/// 宿主固定询问顺序为：被拖到图标上时先问该图标对应的插件，再问其它插件（按固定顺序），
/// 第一个把 <see cref="PluginDropContext"/> 标记为已处理�?<see cref="Handled"/> = true）的插件胜出�?
/// 若无人处理，宿主走内置兜底（DLL �? 加载插件，其�? �? 提示路径并复制到用户插件目录）�?
/// </summary>
public interface IDropHandlerPlugin
{
    /// <summary>能否处理。请保持快速、无副作用�?</summary>
    bool CanHandle(PluginDropContext context);

    /// <summary>执行处理。可以先做，再设�? <see cref="PluginDropContext.Handled"/>�?</summary>
    void OnDrop(PluginDropContext context);
}
