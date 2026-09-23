using System.Windows;
using MiniBar.App.Infrastructure;
using MiniBar.Sdk;

namespace MiniBar.App.Services;

/// <summary>
/// 主题服务。宿主界面用 DynamicResource 绑定“颜色键”（如 BackgroundBrush/ForegroundBrush 这类资源名），
/// 插件则通过 <see cref="PluginTheme"/> 拿到同样的语义色。切换主题时，只要换掉底层的 ResourceDictionary，
/// 所有用 DynamicResource 绑定的地方会【自动重新求值】——于是宿主与所有插件界面一起变，无需逐个控件重设颜色。
///
/// 实现机制（新手版）：
///   · Light.xaml / Dark.xaml 各是一份 ResourceDictionary，里面只定义一堆“颜色键 → 画刷”的映射；
///   · Application.Resources.MergedDictionaries 是全局资源合并表，界面用 {DynamicResource 键} 引用其中的画刷；
///   · Apply() 时把对应主题字典 Insert(0, …) 到合并表第 0 位（最优先），并移除旧字典，实现“整锅换肤”。
///
/// 一个深坑（务必记住）：若自定义主题 + 又定义了全局隐式 TextBlock 样式，就必须自己给 ToolTip / ComboBox / TextBox /
/// CheckBox 等写控件模板。否则系统会沿用默认的浅色控件模板（浅底），配上你设的近白前景 → 深色主题下出现“白底白字”
/// （典型的就是“tooltip 一片空白”）。本项目的主题字典已处理这些模板，新增自定义主题时要照搬。
/// </summary>
public sealed class ThemeService
{
    private const string LightSource = "pack://application:,,,/UI/Themes/Light.xaml";
    private const string DarkSource = "pack://application:,,,/UI/Themes/Dark.xaml";

    private ResourceDictionary? _themeDictionary;

    public bool IsDark { get; private set; }

    public PluginTheme Current => IsDark ? PluginTheme.Dark : PluginTheme.Light;

    public event EventHandler? Changed;

    /// <summary>
    /// 应用主题。themeName 可为 Light/Dark/System：System 表示跟随系统（读注册表 AppsUseLightTheme 判断当前深浅）。
    /// 若目标深浅与当前一致且字典已加载则跳过（避免无谓重建、闪烁）。
    /// 分步骤：加载对应 Light/Dark 字典 → 从全局 MergedDictionaries 移除旧主题字典、Insert(0, 新字典)（第 0 位优先级最高，
    /// 覆盖任何同名键）→ 把每个已打开窗口的标题栏设为深色/浅色 → 触发 Changed 事件（宿主据此广播给插件换肤）。
    /// </summary>
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
