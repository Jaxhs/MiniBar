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
    // JsonSerializerOptions 是 System.Text.Json 的"怎么序列化"配置。
    // WriteIndented = true：输出带缩进的漂亮 JSON，方便用户直接用文本编辑器改 settings.json。
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    // 防抖计时器：插件频繁改配置时，真正写盘被合并到"最后一次改动后 800ms"，避免磁盘抖动。
    // 用 System.Timers.Timer（纯后台、不碰 UI）；AutoReset=false 让它在到点后只触发一次（一次性防抖）。
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

    /// <summary>
    /// 800ms 防抖落盘。步骤：
    /// ① 懒创建（只建一次）一个 800ms、非自动重复的 <c>System.Timers.Timer</c>；
    /// ② 每次调用都先 <c>Stop</c> 再 <c>Start</c>，等于把"倒计时"重置——所以连续改配置时，
    ///    只有<b>最后一次改动之后安静 800ms</b> 才会真正触发 <see cref="OnFlush"/> 写盘；
    /// ③ 用 <c>lock (_gate)</c> 保护，避免多线程同时复位计时器造成竞争。
    /// 这样插件狂写配置也不会每条都落盘，把几十次写合并成一次，保护磁盘与性能。
    /// </summary>
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

    /// <summary>
    /// 读某插件的私有配置项。
    /// <para>
    /// <b>插件配置隔离：</b>所有插件的设置都收在同一个 <c>settings.json</c> 文件里，但按
    /// <c>插件ID → (键 → 值)</c> 两层字典（<c>AppSettings.PluginSettings</c>）隔离——插件 A 永远读不到插件 B 的键，
    /// 也不会互相覆盖。值以 JSON 文本存（任何类型都能 <c>JsonSerializer.Serialize</c> 成字符串），
    /// 读出时再 <c>Deserialize&lt;T&gt;</c> 还原；取不到或解析失败时回退 <paramref name="defaultValue"/>，绝不抛异常影响插件。
    /// </para>
    /// </summary>
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

    /// <summary>
    /// 写某插件的私有配置项（同样按 <c>插件ID</c> 隔离，见 <see cref="GetPluginSetting{T}"/>）。
    /// 把 <paramref name="value"/> 序列化成 JSON 文本存进对应插件的字典，并触发 800ms 防抖落盘。
    /// 值为 null 时存字面量 "null"，反序列化时也能正确识别。
    /// </summary>
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
