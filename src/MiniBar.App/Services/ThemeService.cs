using System.Windows;
using MiniBar.App.Infrastructure;
using MiniBar.Sdk;

namespace MiniBar.App.Services;

/// <summary>
/// 主题服务。宿主界面用 DynamicResource 绑定颜色键，插件通过 <see cref="PluginTheme"/> 拿到同样的语义色，
/// 因此切换主题时宿主与所有插件界面会一起变。
/// </summary>
public sealed class ThemeService
{
    private const string LightSource = "pack://application:,,,/UI/Themes/Light.xaml";
    private const string DarkSource = "pack://application:,,,/UI/Themes/Dark.xaml";

    private ResourceDictionary? _themeDictionary;

    public bool IsDark { get; private set; }

    public PluginTheme Current => IsDark ? PluginTheme.Dark : PluginTheme.Light;

    public event EventHandler? Changed;

    public void Apply(string themeName)
    {
        var dark = themeName.Equals("Dark", StringComparison.OrdinalIgnoreCase) ||
                   (themeName.Equals("System", StringComparison.OrdinalIgnoreCase) && SystemTheme.IsDarkModeEnabled());

        if (_themeDictionary is not null && dark == IsDark)
        {
            return;
        }

        IsDark = dark;

        try
        {
            var dictionary = new ResourceDictionary
            {
                Source = new Uri(dark ? DarkSource : LightSource, UriKind.Absolute),
            };

            var merged = Application.Current.Resources.MergedDictionaries;
            if (_themeDictionary is not null)
            {
                merged.Remove(_themeDictionary);
            }

            merged.Insert(0, dictionary);
            _themeDictionary = dictionary;
        }
        catch (Exception ex)
        {
            AppLog.Warn("主题资源加载失败", ex);
        }

        foreach (Window window in Application.Current.Windows)
        {
            Interop.WindowDressingService.SetDarkTitleBar(window, dark);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>读取系统深浅色偏好（注册表，失败时按浅色处理）。</summary>
public static class SystemTheme
{
    public static bool IsDarkModeEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int i && i == 0;
        }
        catch
        {
            return false;
        }
    }
}
