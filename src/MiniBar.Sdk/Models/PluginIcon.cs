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
    public static readonly PluginIcon Default = new(PluginIconKind.Glyph, "\uE8B7");

    public PluginIcon(PluginIconKind kind, string value)
    {
        Kind = kind;
        Value = value;
    }

    public PluginIconKind Kind { get; }

    public string Value { get; }

    public static PluginIcon Glyph(string glyph) => new(PluginIconKind.Glyph, glyph);

    public static PluginIcon Emoji(string emoji) => new(PluginIconKind.Emoji, emoji);

    public static PluginIcon FromFile(string path) => new(PluginIconKind.ImageFile, path);

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

    public static implicit operator PluginIcon(string? spec) => Parse(spec);

    public bool Equals(PluginIcon? other) =>
        other is not null && Kind == other.Kind && string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as PluginIcon);

    public override int GetHashCode() => HashCode.Combine(Kind, Value);

    public override string ToString() => $"{Kind}:{Value}";
}
