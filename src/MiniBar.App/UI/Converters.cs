using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MiniBar.Sdk;

namespace MiniBar.App.UI;

// ============================================================
// 文件级说明：WPF 值转换器集合（IValueConverter 实现）。
//
// 什么是 IValueConverter：WPF 绑定(Binding)把“数据源属性”显示到“界面属性”时，两边类型常常对不上，
// 例如后台是 bool（true/false），界面要的是 Visibility（Visible/Collapsed）。XAML 里不能写 if/三元，
// 所以需要一个“转换器”在绑定途中做类型/值变换。每个转换器实现两个方法：
//   - Convert：源 → 界面（显示时用）；
//   - ConvertBack：界面 → 源（界面改动写回时用；只读场景直接抛 NotSupportedException）。
//
// 怎么用：在 XAML 里把转换器声明成资源，然后在绑定上 Converter={StaticResource Xxx}。
// 这里全是“无状态”的，所以类标记 sealed（不可被继承，省心）且通常注册成单例资源。
// Visibility 两种值：Visible=显示，Collapsed=不显示且不占布局空间（区别于 Hidden，Hidden 仍占位）。
// ============================================================

/// <summary>bool → Visibility 的反向转换器：true 收起(Collapsed)、false 显示(Visible)。用于“满足条件时隐藏”的场景。</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Collapsed;
}

/// <summary>bool → Visibility 的正向转换器：true 显示(Visible)、false 收起(Collapsed)。最常用的一号转换器。</summary>
public sealed class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

/// <summary>非空 → 可见。</summary>
public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// 图标描述(PluginIcon，图片类) → 图片源(ImageSource)。
/// 关键点：
///   - 按显示尺寸解码：设 BitmapImage.DecodePixelWidth 为要求的尺寸，WPF 只会按这个宽度解码，
///     不会把整张高清原图读进内存，省内存也快（图标本来就只显示 16~32px）。
///   - 缓存：用一个字典按图标路径缓存解码结果，插件每秒刷新多次时不会反复解码卡界面。
///   - Freeze()：解码完冻结位图，之后可在任何线程安全使用（WPF 冻结的对象线程无关）。
///   - 只读转换：ConvertBack 不支持，直接抛 NotSupportedException（图标不需要从图片反写回描述）。
/// </summary>
public sealed class IconImageConverter : IValueConverter
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not PluginIcon icon)
        {
            return null;
        }

        if (icon.Kind is not (PluginIconKind.ImageFile or PluginIconKind.ImageUri))
        {
            return null;
        }

        var decodedSize = parameter is string s && int.TryParse(s, out var size) ? size : 32;

        lock (Cache)
        {
            if (Cache.TryGetValue(icon.Value, out var cached))
            {
                return cached;
            }
        }

        ImageSource? source = null;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.DecodePixelWidth = decodedSize;
            bitmap.UriSource = icon.Kind == PluginIconKind.ImageFile
                ? new Uri(icon.Value, UriKind.Absolute)
                : new Uri(icon.Value, UriKind.RelativeOrAbsolute);
            bitmap.EndInit();
            // Freeze：把位图变成“不可变且线程安全”，冻结后能在任意线程使用，也便于被多处共享/缓存。
            bitmap.Freeze();
            source = bitmap;
        }
        catch
        {
            // 图片坏了就当没有图标
        }

        lock (Cache)
        {
            if (Cache.Count > 64)
            {
                Cache.Clear();
            }

            Cache[icon.Value] = source;
        }

        return source;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
