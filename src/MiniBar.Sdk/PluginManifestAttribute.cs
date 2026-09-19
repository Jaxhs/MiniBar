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
