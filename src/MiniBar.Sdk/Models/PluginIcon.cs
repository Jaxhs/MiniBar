/// <summary>
/// 本文件定义与宿主解耦的图标描述 <see cref="PluginIcon"/> 与图标类型枚举 <see cref="PluginIconKind"/>。
///
/// <para>插件不用返回真图片对象，而是返回一个“描述串怎么解析成图”的结构，
/// 配合字符串隐式转换，最常见写法是 <c>public PluginIcon Icon =&gt; "emoji:🕒";</c>。
/// 这样图标既不依赖宿主资源、又能表达 emoji / Segoe 字形 / 本地图片 / 网络或包内资源多种来源。</para>
///
/// <para><b>pack:// 是什么：</b>WPF 特有的“打包 URI”方案，用来从已编译的程序集里取资源（如图标、图片），
/// 写法形如 <c>pack://application:,,,/YourAssembly;component/res/a.png</c>。它和普通 http(s) 网址一样是“地址”，
/// 由 <see cref="PluginIconKind.ImageUri"/> 处理；程序集内嵌资源请用它，而不是直接写文件路径。</para>
/// </summary>
using System.Globalization;
using System.Linq;

namespace MiniBar.Sdk;

public enum PluginIconKind
{
    /// <summary>Segoe MDL2 Assets / Segoe Fluent Icons 字形，值形如 "glyph:E823"。</summary>
    Glyph,

    /// <summary>Emoji 或任意短文本，值形如 "emoji:🕒"。</summary>
    Emoji,

    /// <summary>本地图片文件，值形如 "file:C:\icons\a.png"。</summary>
    ImageFile,

    /// <summary>pack:// 或 http(s) 图片地址，值形如 "uri:pack://application:,,,/res/a.png"。</summary>
    ImageUri,
}

/// <summary>
/// 与宿主解耦的图标描述。字符串隐式转换让插件可以写成：<c>public PluginIcon Icon => "emoji:🕒";</c>
/// </summary>
public sealed class PluginIcon : IEquatable<PluginIcon>
{
    /// <summary>默认图标（一个 Segoe 字形），<see cref="Parse"/> 遇到空串时回退到这里。</summary>
    public static readonly PluginIcon Default = new(PluginIconKind.Glyph, "\uE8B7");

    /// <summary>用“类型 + 取值”构造一个图标。插件一般用上面的静态工厂，而不是直接调这个。</summary>
    public PluginIcon(PluginIconKind kind, string value)
    {
        Kind = kind;
        Value = value;
    }

    /// <summary>图标来源类型（字形 / emoji / 本地文件 / URI），见 <see cref="PluginIconKind"/>。</summary>
    public PluginIconKind Kind { get; }

    /// <summary>按 <see cref="Kind"/> 解释的“取值”：字形码点或字符、emoji 文本、文件路径或图片地址。</summary>
    public string Value { get; }

    /// <summary>构造一个 Segoe 字形图标（如 "\uE823"）。</summary>
    public static PluginIcon Glyph(string glyph) => new(PluginIconKind.Glyph, glyph);

    /// <summary>构造一个 emoji / 短文本图标。</summary>
    public static PluginIcon Emoji(string emoji) => new(PluginIconKind.Emoji, emoji);

    /// <summary>构造一个本地图片文件图标（参数为文件绝对或相对路径）。</summary>
    public static PluginIcon FromFile(string path) => new(PluginIconKind.ImageFile, path);

    /// <summary>构造一个 pack:// 或 http(s) 图片地址图标。</summary>
    public static PluginIcon FromUri(string uri) => new(PluginIconKind.ImageUri, uri);

    /// <summary>
    /// "glyph:" 后面允许两种写法：
    ///   · 4~6 位十六进制码点，如 <c>glyph:E823</c>（Segoe Fluent Icons / MDL2 的标准写法）；
    ///   · 直接给字符，如 <c>glyph:★</c>。
    /// </summary>
    private static PluginIcon GlyphFromSpec(string value)
    {
        var trimmed = value.Trim();

        if (trimmed.Length is >= 4 and <= 6 && trimmed.All(Uri.IsHexDigit) &&
            int.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code) &&
            code > 0 && code <= 0x10FFFF)
        {
            return Glyph(char.ConvertFromUtf32(code));
        }

        return Glyph(trimmed);
    }

    /// <summary>
    /// 解析图标描述串。无前缀时：单个字符或含非 ASCII 视为 emoji，其余视为字形/短文本。
    /// </summary>
    public static PluginIcon Parse(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return Default;
        }

        var text = spec.Trim();
        var colon = text.IndexOf(':');
        if (colon > 0)
        {
            var prefix = text[..colon].ToLowerInvariant();
            var rest = text[(colon + 1)..];
            switch (prefix)
            {
                case "glyph":
                case "seg":
                case "mdl2":
                    return GlyphFromSpec(rest);
                case "emoji":
                case "text":
                    return Emoji(rest);
                case "file":
                case "path":
                    return FromFile(rest);
                case "uri":
                case "pack":
                    return FromUri(rest);
            }
        }

        if (text.Length <= 3 || text.Any(c => c > 0x7F))
        {
            return Emoji(text);
        }

        // 形如 "\uE823" 的转义写法
        if (text.StartsWith("\\u", StringComparison.Ordinal) &&
            int.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
        {
            return Glyph(char.ConvertFromUtf32(code));
        }

        return Glyph(text);
    }

    /// <summary>让字符串隐式变成 <see cref="PluginIcon"/>，所以插件里写 <c>"emoji:🕒"</c> 就能直接当图标用。</summary>
    public static implicit operator PluginIcon(string? spec) => Parse(spec);

    /// <summary>与另一个图标比较：类型和取值都相同才算相等（用于去重与命中测试）。</summary>
    public bool Equals(PluginIcon? other) =>
        other is not null && Kind == other.Kind && string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <summary>重写 <see cref="object.Equals"/>，转调上面的强类型版本。</summary>
    public override bool Equals(object? obj) => Equals(obj as PluginIcon);

    /// <summary>按类型与取值计算哈希（保证相等的两个图标哈希也相等）。</summary>
    public override int GetHashCode() => HashCode.Combine(Kind, Value);

    /// <summary>还原成 "Kind:Value" 形式（调试 / 显示用）。</summary>
    public override string ToString() => $"{Kind}:{Value}";
}
