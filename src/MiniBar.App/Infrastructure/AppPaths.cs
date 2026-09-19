using System.IO;

namespace MiniBar.App.Infrastructure;

/// <summary>宿主所有落盘位置集中管理。</summary>
public static class AppPaths
{
    static AppPaths()
    {
        BaseDirectory = AppContext.BaseDirectory;

        // 程序目录下的 Plugins：随程序分发，通常只读
        BuiltInPluginDirectory = Path.Combine(BaseDirectory, "Plugins");

        ConfigDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MiniBar");

        // 用户插件目录：往这里丢 DLL 即可被自动发现，且始终可写
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
