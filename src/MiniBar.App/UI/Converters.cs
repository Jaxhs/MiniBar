using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MiniBar.Sdk;

namespace MiniBar.App.UI;

public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Collapsed;
}

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
/// 图标描述 → 图片源。按显示尺寸解码（DecodePixelWidth），避免把原图整张读进内存。
/// 用一个很小的字典缓存同一个图标的位图，插件重复刷新时不会反复解码。
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
