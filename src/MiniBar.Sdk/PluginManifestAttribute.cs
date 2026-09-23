/// <summary>
/// 本文件定义插件“清单”特性 <see cref="PluginManifestAttribute"/>。
/// 它是宿主在<b>不实例化任何插件类型</b>的前提下，仅凭程序集元数据就能读到的“身份证”：
/// 包含唯一 Id、显示名、默认图标、排序权重等。
///
/// <para>宿主扫描插件时先读这个特性来判断“这是个合法插件吗、叫什么、要不要固定到任务栏”，
/// 只有通过后才用 AssemblyLoadContext 把真正的插件类型加载进来。
/// 所以 Id 必须稳定且全局唯一（建议反向域名风格），一旦发布就别改——它同时是启用状态与私有配置的键。</para>
/// </summary>
namespace MiniBar.Sdk;

/// <summary>
/// 插件清单。宿主在不实例化任何类型的前提下通过程序集元数据读取该特性，
/// 所以 <see cref="Id"/> 必须稳定且全局唯一（建议反向域名风格）。
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class PluginManifestAttribute : Attribute
{
    public PluginManifestAttribute(string id, string name)
    {
        Id = id;
        Name = name;
    }

    /// <summary>全局唯一标识，用于持久化固定顺序、启用状态与插件私有配置。</summary>
    public string Id { get; }

    /// <summary>显示名称（任务栏图标提示、插件管理列表）。</summary>
    public string Name { get; }

    /// <summary>一句话描述。</summary>
    public string? Description { get; set; }

    public string? Author { get; set; }

    public string? Version { get; set; }

    /// <summary>默认图标，语法见 <see cref="PluginIcon.Parse"/>，例如 "emoji:🕒" 或 "glyph:E823"。</summary>
    public string? Icon { get; set; }

    /// <summary>排序权重，越小越靠前（仅用于同一次安装的初始顺序）。</summary>
    public int Order { get; set; }

    /// <summary>首次发现时是否默认固定到任务栏。</summary>
    public bool DefaultPinned { get; set; } = true;
}
