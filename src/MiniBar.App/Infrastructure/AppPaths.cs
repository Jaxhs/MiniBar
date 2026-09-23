using System.IO;

namespace MiniBar.App.Infrastructure;

/// <summary>
/// 宿主所有落盘位置集中管理（程序目录、配置目录、日志、插件目录……一处定义，全局复用）。
/// <para>
/// <b>安全底线：</b>插件相关的删除/卸载操作，<b>只允许发生在下面这两个插件目录之内</b>——
/// <see cref="BuiltInPluginDirectory"/>（随程序分发、通常只读）与 <see cref="UserPluginDirectory"/>
/// （用户自己丢 DLL 的地方，%LOCALAPPDATA%\MiniBar\Plugins）。任何"删文件"逻辑都必须先校验目标路径确实落在这两个目录之下，
/// 绝不允许拼接出目录穿越（如 <c>..\..\</c>）去删除系统文件。这是插件热插拔的硬安全约束。
/// </para>
/// </summary>
public static class AppPaths
{
    static AppPaths()
    {
        BaseDirectory = AppContext.BaseDirectory;

    // 程序目录下的 Plugins：随程序分发，通常只读（也是允许删除插件的目录之一）
    BuiltInPluginDirectory = Path.Combine(BaseDirectory, "Plugins");

    ConfigDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MiniBar");

    // 用户插件目录：往这里丢 DLL 即可被自动发现，且始终可写。
    // 路径是 %LOCALAPPDATA%\MiniBar\Plugins（LocalApplicationData 不会随用户漫游，适合放可再生的程序数据）。
    // 这是允许删除插件的另一个（也是主要的）目录。
    UserPluginDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MiniBar", "Plugins");

        DataRoot = Path.Combine(ConfigDirectory, "data");
        LogDirectory = Path.Combine(ConfigDirectory, "logs");

        SettingsFile = Path.Combine(ConfigDirectory, "settings.json");
        PluginStateFile = Path.Combine(ConfigDirectory, "plugins.json");
        LogFile = Path.Combine(LogDirectory, "minibar.log");
    }

    public static string BaseDirectory { get; }

    public static string BuiltInPluginDirectory { get; }

    public static string UserPluginDirectory { get; }

    public static string ConfigDirectory { get; }

    public static string DataRoot { get; }

    public static string LogDirectory { get; }

    public static string SettingsFile { get; }

    public static string PluginStateFile { get; }

    public static string LogFile { get; }

    public static string GetPluginDataDirectory(string pluginId)
    {
        var safe = string.Concat(pluginId.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var dir = Path.Combine(DataRoot, safe);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(BuiltInPluginDirectory);
        Directory.CreateDirectory(UserPluginDirectory);
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(LogDirectory);
    }
}
