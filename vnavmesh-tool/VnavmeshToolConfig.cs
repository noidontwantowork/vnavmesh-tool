using Dalamud.Configuration;

namespace VnavmeshTool;

/// <summary>本插件的持久化配置。</summary>
internal sealed class VnavmeshToolConfig : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>是否启用两版 vnavmesh 的配置目录隔离（切换时做目录换位）。</summary>
    public bool ProfileIsolationEnabled { get; set; } = true;

    /// <summary>最近一次换位后，活动配置位归属的版本。</summary>
    public VnavmeshFlavor? LastActiveFlavor { get; set; }
}
