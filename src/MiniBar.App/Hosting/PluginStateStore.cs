using System.Collections.Generic;
using MiniBar.App.Infrastructure;

namespace MiniBar.App.Hosting;

public sealed class PluginState
{
    /// <summary>用户是否启用。禁用 = 不加载、不占内存，但文件与配置保留。</summary>
    public bool Enabled { get; set; } = true;

    public bool Pinned { get; set; }

    public int Order { get; set; }

    /// <summary>最后一次已知路径，用于诊断“插件从哪来”。</summary>
    public string? FilePath { get; set; }
}

/// <summary>
/// 插件状态持久化（%APPDATA%\MiniBar\plugins.json）。
/// 只存“用户意图”（Enabled/Pinned/Order/FilePath），不存运行状态 —— 所以复制一份配置文件就能还原整套插件布局。
///
/// 新手须知：
///   · 它在内存里用 Dictionary&lt;string, PluginState&gt;（以插件 ID 为键）保存，所有读改写都先动内存，Save() 时才整体序列化
///     成 JSON 写盘（JsonSerializer）。用 Dictionary 是为了 O(1) 按 ID 查状态；
///   · GetOrCreate 是关键：扫描到新插件时，先在磁盘文件里找旧状态“合并”过来（保留用户之前设置的固定/启用），
///     找不到才用清单默认值新建。这就是“文件删了放回来，固定位置还在”的来源；
///   · 它不负责“重复文件”的状态归属——那是 PluginHost 的纪律：删除重复文件绝不能动真正加载着的那一个的状态。
/// </summary>
public sealed class PluginStateStore
{
    private readonly SettingsService _settings;
    private Dictionary<string, PluginState> _states;

    public PluginStateStore(SettingsService settings)
    {
        _settings = settings;
        _states = settings.ReadJson<Dictionary<string, PluginState>>(AppPaths.PluginStateFile)
                  ?? new Dictionary<string, PluginState>(StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyDictionary<string, PluginState> All => _states;

    public bool TryGet(string pluginId, out PluginState state) => _states.TryGetValue(pluginId, out state!);

    /// <summary>取状态；首次见到该插件时按清单里的默认值创建。</summary>
    public PluginState GetOrCreate(PluginCandidate candidate)
    {
        if (_states.TryGetValue(candidate.Id, out var existing))
        {
            existing.FilePath = candidate.FilePath;
            return existing;
        }

        var isNew = !_states.ContainsKey(candidate.Id);
        var state = new PluginState
        {
            Enabled = true,
            Pinned = candidate.DefaultPinned,
            Order = candidate.Order != 0 ? candidate.Order : NextOrder(),
            FilePath = candidate.FilePath,
        };

        _states[candidate.Id] = state;

        if (isNew)
        {
            NormalizeOrder();
        }

        Save();
        return state;
    }

    /// <summary>把内存里的状态字典整体用 JsonSerializer 序列化成 plugins.json 写盘（整体覆盖，简单且不容易写花）。</summary>
    public void Save() => _settings.WriteJson(AppPaths.PluginStateFile, _states);

    /// <summary>把 Order 规整为 0..n-1 的连续序号。</summary>
    public void NormalizeOrder()
    {
        var ordered = _states.OrderBy(kv => kv.Value.Order).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].Value.Order = i;
        }
    }

    private int NextOrder() => _states.Count == 0 ? 0 : _states.Values.Max(s => s.Order) + 1;
}
