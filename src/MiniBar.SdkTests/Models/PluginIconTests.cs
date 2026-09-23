/// <summary>
/// 本文件是插件图标描述串（"emoji:🕒" / "glyph:E823" …）的解析单元测试。
///
/// <para>这套语法是插件的公开契约：解析错了，任务栏图标就会变成“豆腐块”（□）。
/// 所以用测试把这些规则钉死——任何改动都不能让既有语法悄悄失效。</para>
/// </summary>
using MiniBar.Sdk;

namespace MiniBar.Sdk.Tests;

/// <summary>
/// 插件图标描述串的解析测试。
/// 这套语法是插件的公开契约（"emoji:🕒" / "glyph:E823" …），解析错了会让任务栏图标变成豆腐块，
/// 所以用测试钉住。
/// </summary>
[TestClass]
public sealed class PluginIconTests
{
    [TestMethod]
    public void Emoji_前缀_解析为表情图标()
    {
        var icon = PluginIcon.Parse("emoji:🕒");

        Assert.AreEqual(PluginIconKind.Emoji, icon.Kind);
        Assert.AreEqual("🕒", icon.Value);
    }

    [TestMethod]
    public void Glyph_前缀_支持十六进制码点()
    {
        // "glyph:E823" 是 Segoe MDL2 的标准写法，必须解析成真正的字符而不是字面量 "E823"
        var icon = PluginIcon.Parse("glyph:E823");

        Assert.AreEqual(PluginIconKind.Glyph, icon.Kind);
        Assert.AreEqual("\uE823", icon.Value);
        Assert.AreEqual(1, icon.Value.Length);
    }

    [TestMethod]
    public void Glyph_前缀_也接受直接给字符()
    {
        var icon = PluginIcon.Parse("glyph:★");

        Assert.AreEqual(PluginIconKind.Glyph, icon.Kind);
        Assert.AreEqual("★", icon.Value);
    }

    [TestMethod]
    public void 文件与地址前缀_解析为图片()
    {
        Assert.AreEqual(PluginIconKind.ImageFile, PluginIcon.Parse(@"file:C:\icons\a.png").Kind);
        Assert.AreEqual(PluginIconKind.ImageUri, PluginIcon.Parse("uri:pack://application:,,,/res/a.png").Kind);
    }

    [TestMethod]
    public void 无前缀的非ASCII文本_视为表情()
    {
        var icon = PluginIcon.Parse("💡");

        Assert.AreEqual(PluginIconKind.Emoji, icon.Kind);
    }

    [TestMethod]
    public void 空值_回退到默认图标()
    {
        Assert.AreEqual(PluginIcon.Default.Value, PluginIcon.Parse(null).Value);
        Assert.AreEqual(PluginIcon.Default.Value, PluginIcon.Parse("   ").Value);
    }

    [TestMethod]
    public void 字符串可以隐式转换为图标()
    {
        // 插件里最常见的写法：public PluginIcon Icon => "emoji:📈";
        PluginIcon icon = "emoji:📈";

        Assert.AreEqual(PluginIconKind.Emoji, icon.Kind);
        Assert.AreEqual("📈", icon.Value);
    }

    [TestMethod]
    public void 相同类型与值_判定相等()
    {
        Assert.AreEqual(PluginIcon.Parse("glyph:E823"), PluginIcon.Parse("glyph:E823"));
        Assert.AreNotEqual(PluginIcon.Parse("glyph:E823"), PluginIcon.Parse("glyph:E824"));
        Assert.AreEqual(PluginIcon.Parse("emoji:🕒").GetHashCode(), PluginIcon.Parse("emoji:🕒").GetHashCode());
    }
}
