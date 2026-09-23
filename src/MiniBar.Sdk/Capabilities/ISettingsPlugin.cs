using System.Collections.Generic;
using System.IO;

namespace MiniBar.Sdk;

public enum PluginSettingKind
{
    /// <summary>纯说明文字（只读）。</summary>
    Info,

    Text,

    Bool,

    Number,

    /// <summary>下拉选择，配合 <see cref="PluginSettingItem.Choices"/>。</summary>
    Choice,

    /// <summary>路径选择（文件夹），宿主会给出"浏览…"按钮。</summary>
    Folder,

    /// <summary>路径选择（文件）。</summary>
    File,

    /// <summary>一个按钮，点击执行 <see cref="PluginSettingItem.Invoke"/>。</summary>
    Action,
}

/// <summary>
/// 一条设置项。宿主负责渲染与读写：读 <see cref="GetValue"/>、写 <see cref="SetValue"/>，
/// 插件在自己的回调里落盘并立即生效 —— 这样宿主不需要知道插件是怎么存配置的。
/// 绝大多数场景直接用下面的静态工厂，几行就能拼出一个设置页面。
/// </summary>
public sealed class PluginSettingItem
{
    public required string Label { get; init; }

    public PluginSettingKind Kind { get; init; } = PluginSettingKind.Text;

    public string? Description { get; init; }

    public Func<object?>? GetValue { get; init; }

    public Action<object?>? SetValue { get; init; }

    public double Minimum { get; init; } = double.NaN;

    public double Maximum { get; init; } = double.NaN;

    public double Step { get; init; } = 1;

    public IReadOnlyList<string>? Choices { get; init; }

    public string? Placeholder { get; init; }

    /// <summary>数值单位，显示在输入框后面（例如 "px" / "秒"）。</summary>
    public string? Suffix { get; init; }

    /// <summary>Action 类型的按钮文字。</summary>
    public string? ActionLabel { get; init; }

    public Action? Invoke { get; init; }

    // ---------------------------------------------------------------- 工厂
    //
    // 所有工厂都支持一个可选的 onChanged：宿主写完配置值后立刻回调，
    // 插件在这里"即时生效"（刷新界面、重开计时器等），不需要另写一套保存逻辑。

    /// <summary>开关。值写回插件配置（<see cref="IPluginContext.SetSetting{T}"/>）。</summary>
    public static PluginSettingItem Toggle(IPluginContext context, string key, string label, bool defaultValue = false,
        string? description = null, Action? onChanged = null) => new()
    {
        Label = label,
        Kind = PluginSettingKind.Bool,
        Description = description,
        GetValue = () => context.GetSetting(key, defaultValue),
        SetValue = value =>
        {
            context.SetSetting(key, value is bool b ? b : defaultValue);
            Applied(context, onChanged);
        },
    };

    public static PluginSettingItem Text(IPluginContext context, string key, string label, string defaultValue = "",
        string? description = null, string? placeholder = null, Action? onChanged = null) => new()
    {
        Label = label,
        Kind = PluginSettingKind.Text,
        Description = description,
        Placeholder = placeholder,
        GetValue = () => context.GetSetting(key, defaultValue),
        SetValue = value =>
        {
            context.SetSetting(key, value as string ?? defaultValue);
            Applied(context, onChanged);
        },
    };

    public static PluginSettingItem Number(IPluginContext context, string key, string label, double defaultValue,
        double minimum, double maximum, double step = 1, string? suffix = null, string? description = null,
        Action? onChanged = null) => new()
    {
        Label = label,
        Kind = PluginSettingKind.Number,
        Description = description,
        Suffix = suffix,
        Minimum = minimum,
        Maximum = maximum,
        Step = step,
        GetValue = () => context.GetSetting(key, defaultValue),
        SetValue = value =>
        {
            var clamped = value is double d ? Math.Clamp(d, minimum, maximum) : defaultValue;
            context.SetSetting(key, clamped);
            Applied(context, onChanged);
        },
    };

    public static PluginSettingItem Choice(IPluginContext context, string key, string label,
        IReadOnlyList<string> choices, string defaultValue = "", string? description = null,
        Action? onChanged = null) => new()
    {
        Label = label,
        Kind = PluginSettingKind.Choice,
        Description = description,
        Choices = choices,
        GetValue = () => context.GetSetting(key, defaultValue) ?? defaultValue,
        SetValue = value =>
        {
            context.SetSetting(key, value as string ?? defaultValue);
            Applied(context, onChanged);
        },
    };

    public static PluginSettingItem Folder(IPluginContext context, string key, string label,
        string defaultValue = "", string? description = null, Action? onChanged = null) => new()
    {
        Label = label,
        Kind = PluginSettingKind.Folder,
        Description = description,
        GetValue = () => context.GetSetting(key, defaultValue) ?? string.Empty,
        SetValue = value =>
        {
            context.SetSetting(key, value as string ?? string.Empty);
            Applied(context, onChanged);
        },
    };

    public static PluginSettingItem FilePicker(IPluginContext context, string key, string label,
        string defaultValue = "", string? description = null, Action? onChanged = null) => new()
    {
        Label = label,
        Kind = PluginSettingKind.File,
        Description = description,
        GetValue = () => context.GetSetting(key, defaultValue) ?? string.Empty,
        SetValue = value =>
        {
            context.SetSetting(key, value as string ?? string.Empty);
            Applied(context, onChanged);
        },
    };

    /// <summary>说明文字（不参与存储）。</summary>
    public static PluginSettingItem Note(string text, string? description = null) => new()
    {
        Label = text,
        Kind = PluginSettingKind.Info,
        Description = description,
    };

    public static PluginSettingItem Button(string label, Action invoke, string? description = null,
        string? buttonText = null) => new()
    {
        Label = label,
        Kind = PluginSettingKind.Action,
        Description = description,
        ActionLabel = buttonText ?? label,
        Invoke = invoke,
    };

    /// <summary>插件自己写 GetValue/SetValue 时用这个收尾：回调 + 让宿主刷新任务栏图标与提示。</summary>
    public static void Applied(IPluginContext context, Action? onChanged)
    {
        try
        {
            onChanged?.Invoke();
        }
        catch (Exception ex)
        {
            context.Logger.Warn("设置变更回调抛异常", ex);
        }

        context.InvalidateBarItem();
    }
}

/// <summary>设置页面里的一个分组（宿主渲染成一张卡片）。</summary>
public sealed class PluginSettingsSection
{
    public required string Title { get; init; }

    public string? Description { get; init; }

    public PluginIcon? Icon { get; init; }

    public IEnumerable<PluginSettingItem> Items { get; init; } = Array.Empty<PluginSettingItem>();
}

/// <summary>
/// 能力：让插件把自己的设置暴露到宿主的设置窗口里。
/// 宿主负责渲染、读写与即时生效；"宿主设置"本身也走同一套模型，
/// 所以插件看到的效果和宿主自己的设置完全一致。
/// </summary>
public interface ISettingsPlugin
{
    IEnumerable<PluginSettingsSection> GetSettingsSections();
}
