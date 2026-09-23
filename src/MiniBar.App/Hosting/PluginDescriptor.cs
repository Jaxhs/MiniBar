using System.Reflection;
using MiniBar.App.Infrastructure;
using MiniBar.App.Services;
using MiniBar.Sdk;

namespace MiniBar.App.Hosting;

/// <summary>
/// 一个插件的宿主侧模型：既描述“磁盘上的插件”，也承载“当前是否加载 / 是否固定 / 是否出错”。
/// 界面直接绑定它（任务栏图标、插件管理器共用）。
/// </summary>
public sealed class PluginDescriptor : ObservableObject
{
    private bool _isEnabled = true;
    private bool _isLoaded;
    private bool _isPinned;
    private int _order;
    private string? _error;
    private bool _isPanelOpen;
    private bool _isMiniHost;
    private bool _isDuplicate;

    public PluginDescriptor(PluginCandidate candidate)
    {
        Candidate = candidate;
        _isPinned = candidate.DefaultPinned;
    }

    public PluginCandidate Candidate { get; private set; }

    public string Id => Candidate.Id;

    public string Name => Candidate.Name;

    public string? Description => Candidate.Description;

    public string? Author => Candidate.Author;

    public string? Version => Candidate.Version;

    public string FilePath => Candidate.FilePath;

    public string[] Capabilities => Candidate.Capabilities;

    // ---- 能力标记（由扫描阶段得出，零反射开销）----

    public bool HasTaskButton => Capabilities.Contains(PluginCapabilities.TaskButton);

    public bool HasSettings => Capabilities.Contains(PluginCapabilities.Settings);

    public bool HasBarWidget => Capabilities.Contains(PluginCapabilities.BarWidget);

    public bool HasPanel => Capabilities.Contains(PluginCapabilities.Panel);

    public bool HasCompact => Capabilities.Contains(PluginCapabilities.Compact);

    public bool HasContextMenu => Capabilities.Contains(PluginCapabilities.ContextMenu);

    public bool HasHotkey => Capabilities.Contains(PluginCapabilities.Hotkey);

    public bool HasDropHandler => Capabilities.Contains(PluginCapabilities.DropHandler);

    public bool IsOpenable => HasPanel;

    // ---- 运行状态 ----

    /// <summary>用户是否启用（持久化）。禁用会立即卸载，但保留文件与配置。</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    /// <summary>程序集是否已加载进内存并完成 Initialize。</summary>
    public bool IsLoaded
    {
        get => _isLoaded;
        internal set
        {
            if (SetProperty(ref _isLoaded, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    /// <summary>是否固定显示在任务栏上。</summary>
    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (SetProperty(ref _isPinned, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    /// <summary>固定项排序权重。</summary>
    public int Order
    {
        get => _order;
        internal set => SetProperty(ref _order, value);
    }

    /// <summary>面板当前是否展开。</summary>
    public bool IsPanelOpen
    {
        get => _isPanelOpen;
        internal set => SetProperty(ref _isPanelOpen, value);
    }

    /// <summary>是否正作为迷你模式的内容来源。</summary>
    public bool IsMiniHost
    {
        get => _isMiniHost;
        internal set => SetProperty(ref _isMiniHost, value);
    }

    /// <summary>与其它目录下的插件 ID 重复，已忽略。</summary>
    public bool IsDuplicate
    {
        get => _isDuplicate;
        internal set => SetProperty(ref _isDuplicate, value);
    }

    public string? Error
    {
        get => _error;
        internal set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);

    public string StatusText => IsDuplicate
        ? "重复文件"
        : !IsEnabled
            ? "已禁用"
            : HasError ? "出错"
            : IsLoaded ? "运行中"
            : "待加载";

    /// <summary>能力列表的可读文本。</summary>
    public string CapabilityText => Capabilities.Length == 0 ? "无能力声明" : string.Join(" · ", Capabilities);

    /// <summary>是否可以打开界面。</summary>
    public bool CanOpen => HasPanel;

    /// <summary>是否可以删除文件（只允许两个插件目录内的文件，绝不动用户自己的文件）。</summary>
    public bool CanDelete
    {
        get
        {
            try
            {
                var full = System.IO.Path.GetFullPath(FilePath);
                return full.StartsWith(System.IO.Path.GetFullPath(AppPaths.UserPluginDirectory), StringComparison.OrdinalIgnoreCase) ||
                       full.StartsWith(System.IO.Path.GetFullPath(AppPaths.BuiltInPluginDirectory), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }

    public string FileName => System.IO.Path.GetFileName(FilePath);

    public string DirectoryPath => System.IO.Path.GetDirectoryName(FilePath) ?? string.Empty;

    public string SourceText => FilePath.StartsWith(AppPaths.UserPluginDirectory, StringComparison.OrdinalIgnoreCase)
        ? "用户目录"
        : "程序目录";

    // ---- 插件实例（仅宿主内部访问）----

    internal PluginLoadContext? LoadContext { get; set; }

    internal Assembly? PluginAssembly { get; set; }

    internal IMinibarPlugin? Instance { get; set; }

    internal PluginContext? Facade { get; set; }

    internal ITaskButtonPlugin? TaskButton => Instance as ITaskButtonPlugin;

    internal ISettingsPlugin? Settings => Instance as ISettingsPlugin;

    internal IBarWidgetPlugin? BarWidget => Instance as IBarWidgetPlugin;

    internal IPanelContentPlugin? Panel => Instance as IPanelContentPlugin;

    internal ICompactContentPlugin? Compact => Instance as ICompactContentPlugin;

    internal IContextMenuPlugin? Menu => Instance as IContextMenuPlugin;

    internal IHotkeyPlugin? Hotkeys => Instance as IHotkeyPlugin;

    internal IDropHandlerPlugin? DropHandler => Instance as IDropHandlerPlugin;

    // ---- 供界面直接绑定的图标信息 ----

    /// <summary>图标取值优先级：插件运行时提供的 > 清单声明的 > 默认。</summary>
    public PluginIcon Icon
    {
        get
        {
            if (IsLoaded && TaskButton is { } taskButton)
            {
                try
                {
                    return taskButton.Icon ?? PluginIcon.Default;
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"读取插件图标失败：{Id}", ex);
                }
            }

            return PluginIcon.Parse(Candidate.IconSpec);
        }
    }

    public string DisplayName
    {
        get
        {
            if (IsLoaded && TaskButton is { } taskButton)
            {
                try
                {
                    var name = taskButton.DisplayName;
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        return name;
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"读取插件显示名失败：{Id}", ex);
                }
            }

            return Candidate.Name;
        }
    }

    public string? Tooltip
    {
        get
        {
            if (IsLoaded && TaskButton is { } taskButton)
            {
                try
                {
                    return taskButton.Tooltip;
                }
                catch
                {
                    return null;
                }
            }

            return Candidate.Description;
        }
    }

    public string? Badge
    {
        get
        {
            if (IsLoaded && TaskButton is { } taskButton)
            {
                try
                {
                    return taskButton.Badge;
                }
                catch
                {
                    return null;
                }
            }

            return null;
        }
    }

    public bool BadgeIsAccent
    {
        get
        {
            if (IsLoaded && TaskButton is { } taskButton)
            {
                try
                {
                    return taskButton.BadgeIsAccent;
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }
    }

    public bool HasBadge => !string.IsNullOrEmpty(Badge);

    /// <summary>悬停提示：插件名 + 插件自述（若有）。</summary>
    public string HoverText => string.IsNullOrWhiteSpace(Tooltip) ? DisplayName : $"{DisplayName}\n{Tooltip}";

    /// <summary>插件自己改了图标 / 徽标 / 提示后由宿主调用，刷新绑定。</summary>
    public void RefreshVisuals()
    {
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Tooltip));
        OnPropertyChanged(nameof(Badge));
        OnPropertyChanged(nameof(BadgeIsAccent));
        OnPropertyChanged(nameof(HasBadge));
        OnPropertyChanged(nameof(HoverText));
    }

    internal void NotifyStateChanged()
    {
        if (Instance is ITaskButtonPlugin taskButton)
        {
            try
            {
                taskButton.OnStateChanged(new BarItemState(IsPinned, IsPanelOpen, IsMiniHost, IsMiniHost));
            }
            catch (Exception ex)
            {
                AppLog.Warn($"插件 OnStateChanged 抛异常：{Id}", ex);
            }
        }
    }
}
