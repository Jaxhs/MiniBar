using System.Collections.Generic;
using System.Text.Json;
using MiniBar.Sdk;

namespace MiniBar.App.Infrastructure;

/// <summary>
/// 配置读写。写盘做了 800ms 防抖合并，避免插件频繁改配置时造成磁盘抖动。
/// </summary>
public sealed class SettingsService
{
    private readonly object _gate = new();
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    private System.Timers.Timer? _flushTimer;

    public SettingsService()
    {
        Settings = Load<AppSettings>(AppPaths.SettingsFile) ?? new AppSettings();

        // 首次运行就把默认配置落盘，方便用户直接改文件
        if (!File.Exists(AppPaths.SettingsFile))
        {
            SaveNow();
        }
    }

    public AppSettings Settings { get; private set; }

    public event EventHandler? Changed;

    public void NotifyChanged()
    {
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleFlush();
    }

    public void ScheduleFlush()
    {
        lock (_gate)
        {
            _flushTimer ??= new System.Timers.Timer(800) { AutoReset = false };
            _flushTimer.Elapsed -= OnFlush;
            _flushTimer.Elapsed += OnFlush;
            _flushTimer.Stop();
            _flushTimer.Start();
        }
    }

    private void OnFlush(object? sender, System.Timers.ElapsedEventArgs e) => SaveNow();

    public void SaveNow()
    {
        lock (_gate)
        {
            _flushTimer?.Stop();
            TryWrite(AppPaths.SettingsFile, Settings);
        }
    }

    public void Reload()
    {
        Settings = Load<AppSettings>(AppPaths.SettingsFile) ?? new AppSettings();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ---- 插件私有配置 ----

    public T? GetPluginSetting<T>(string pluginId, string key, T? defaultValue = default)
    {
        if (!Settings.PluginSettings.TryGetValue(pluginId, out var bag) || !bag.TryGetValue(key, out var raw))
        {
            return defaultValue;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(raw);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取插件配置失败 {pluginId}/{key}", ex);
            return defaultValue;
        }
    }

    public void SetPluginSetting<T>(string pluginId, string key, T? value)
    {
        if (!Settings.PluginSettings.TryGetValue(pluginId, out var bag))
        {
            bag = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Settings.PluginSettings[pluginId] = bag;
        }

        try
        {
            bag[key] = value is null ? "null" : JsonSerializer.Serialize(value);
        }
        catch
        {
            bag[key] = "null";
        }

        ScheduleFlush();
    }

    public void DropPluginSettings(string pluginId)
    {
        Settings.PluginSettings.Remove(pluginId);
        ScheduleFlush();
    }

    private T? Load<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var text = File.ReadAllText(path);
            return string.IsNullOrWhiteSpace(text) ? null : JsonSerializer.Deserialize<T>(text);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取配置失败：{path}", ex);
            return null;
        }
    }

    private void TryWrite<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(value, _json));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"写入配置失败：{path}", ex);
        }
    }

    /// <summary>供插件状态存储复用。</summary>
    public void WriteJson<T>(string path, T value) => TryWrite(path, value);

    public T? ReadJson<T>(string path) where T : class => Load<T>(path);
}
