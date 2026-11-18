using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace VnavmeshTool;

/// <summary>
/// 串行地关闭 / 开启 vnavmesh 各版本。核心约束：只有在确认所有版本的 vnavmesh
/// 都已经真正卸载完成（而不是仅仅发出了卸载请求）之后，才允许加载目标版本。
/// </summary>
internal sealed class VnavmeshSwitcher : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan StableTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan UnloadTimeout = TimeSpan.FromSeconds(150);
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(150);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(250);

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private readonly VnavmeshToolConfig config;
    private readonly object gate = new();

    private IReadOnlyList<VnavmeshEntry> entries = [];
    private DateTime lastRefreshUtc = DateTime.MinValue;
    private DateTime lastProfileProbeUtc = DateTime.MinValue;
    private int busy;
    private string statusText = "就绪。";
    private string profileSummary = "配置共存：检测中 …";
    private bool statusIsError;
    private CancellationTokenSource? cancellation;

    public VnavmeshSwitcher(IDalamudPluginInterface pluginInterface, IPluginLog log, VnavmeshToolConfig config)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
        this.config = config;
    }

    public IReadOnlyList<VnavmeshEntry> Entries => Volatile.Read(ref this.entries);

    public bool IsBusy => Volatile.Read(ref this.busy) != 0;

    public string StatusText => Volatile.Read(ref this.statusText);

    public bool StatusIsError => Volatile.Read(ref this.statusIsError);

    /// <summary>配置目录隔离（换位）是否启用。</summary>
    public bool ProfileIsolationEnabled
    {
        get => this.config.ProfileIsolationEnabled;
        set => this.config.ProfileIsolationEnabled = value;
    }

    /// <summary>活动配置位的当前归属，用于窗口展示。</summary>
    public string ProfileSummary => Volatile.Read(ref this.profileSummary);

    public void SetProfileIsolation(bool enabled)
    {
        if (this.config.ProfileIsolationEnabled == enabled) return;

        this.config.ProfileIsolationEnabled = enabled;
        this.SaveConfig();
        this.ProbeProfile(force: true);
    }

    public static string FlavorLabel(VnavmeshFlavor flavor) => flavor == VnavmeshFlavor.China ? "中国服版" : "国际服版";

    public void RefreshIfDue()
    {
        if (DateTime.UtcNow - this.lastRefreshUtc < RefreshInterval) return;
        this.Refresh();
    }

    public void Refresh()
    {
        this.lastRefreshUtc = DateTime.UtcNow;

        try
        {
            lock (this.gate)
            {
                this.entries = DalamudInternal.Snapshot();
            }

            this.ProbeProfile();
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "读取 vnavmesh 插件列表失败，下一轮重试。");
        }
    }

    /// <summary>检测当前状态并切换到另一个版本。</summary>
    public void RequestToggle()
    {
        this.Refresh();
        var list = this.Entries.Where(x => x.IsUsable).ToList();

        if (list.Count == 0)
        {
            this.Fail("没有检测到任何可用的 vnavmesh 插件条目。请先运行本插件目录下的 RegisterDevPlugins.bat 登记开发插件路径。");
            return;
        }

        var loaded = list.FirstOrDefault(x => x.IsLoaded);
        VnavmeshFlavor target;
        if (loaded is not null)
        {
            target = loaded.Flavor == VnavmeshFlavor.China ? VnavmeshFlavor.Global : VnavmeshFlavor.China;
        }
        else
        {
            target = list.Any(x => x.Flavor == VnavmeshFlavor.Global) ? VnavmeshFlavor.Global : VnavmeshFlavor.China;
        }

        if (list.All(x => x.Flavor != target))
        {
            this.Fail($"当前没有可用的{FlavorLabel(target)} vnavmesh 条目。");
            return;
        }

        this.RequestEnable(target);
    }

    /// <summary>只保留指定版本运行。</summary>
    public void RequestEnable(VnavmeshFlavor target)
    {
        if (Interlocked.CompareExchange(ref this.busy, 1, 0) != 0)
        {
            this.Fail("上一次操作还在进行中，请稍候。");
            return;
        }

        this.cancellation?.Dispose();
        this.cancellation = new CancellationTokenSource();
        var token = this.cancellation.Token;

        this.SetStatus($"正在切换到{FlavorLabel(target)} …");
        _ = Task.Run(() => this.RunSwitchAsync(target, token));
    }

    /// <summary>③ 把 installedPlugins 里发现的新版本同步进本插件的 Versions 槽位。</summary>
    public void RequestSyncFromInstalled()
    {
        if (Interlocked.CompareExchange(ref this.busy, 1, 0) != 0)
        {
            this.Fail("上一次操作还在进行中，请稍候。");
            return;
        }

        this.cancellation?.Dispose();
        this.cancellation = new CancellationTokenSource();
        var token = this.cancellation.Token;

        this.SetStatus("正在扫描并导入新版本 vnavmesh …");
        _ = Task.Run(() => this.RunSyncFromInstalledAsync(token));
    }

    /// <summary>④ 直接从上游插件库下载并更新两个槽位。</summary>
    public void RequestOnlineUpdate()
    {
        if (Interlocked.CompareExchange(ref this.busy, 1, 0) != 0)
        {
            this.Fail("上一次操作还在进行中，请稍候。");
            return;
        }

        this.cancellation?.Dispose();
        this.cancellation = new CancellationTokenSource();
        var token = this.cancellation.Token;

        this.SetStatus("正在从上游插件库检查 vnavmesh 更新 …");
        _ = Task.Run(() => this.RunOnlineUpdateAsync(token));
    }

    public void Dispose()
    {
        this.cancellation?.Cancel();
        this.cancellation?.Dispose();
        this.cancellation = null;
    }

    private async Task RunSwitchAsync(VnavmeshFlavor target, CancellationToken token)
    {
        try
        {
            var pluginRoot = this.ResolvePluginRoot();
            if (pluginRoot is null) return;

            var configsRoot = this.config.ProfileIsolationEnabled ? this.ResolvePluginConfigsRoot() : null;

            // 阶段 -1：自愈开发插件登记。旧版本留下的「Versions\<flavor>-<版本号>」条目会指向已删目录，
            // 这些条目只能报 did not exist，必须先清掉并把 Global / CN 两个槽位补登记。
            await this.RepairDevLocationsAsync(pluginRoot, token).ConfigureAwait(false);

            // 阶段 0：先让所有条目离开「加载中 / 卸载中」这种过渡状态。
            if (!await this.WaitUntilAsync(
                    () => Relevant(this.SnapshotSafe()).All(x => !x.IsBusy),
                    StableTimeout,
                    token).ConfigureAwait(false))
            {
                this.Fail("等待插件状态稳定超时：仍有 vnavmesh 处于加载 / 卸载过程中，已中止。");
                return;
            }

            var current = Relevant(this.SnapshotSafe());
            if (current.Count == 0)
            {
                this.Fail("没有检测到任何可用的 vnavmesh 插件条目。请先运行本插件目录下的 RegisterDevPlugins.bat。");
                return;
            }

            // 同一个分支可能同时存在「已安装」条目和「开发插件」条目：优先用开发插件那份，
            // 避免启动清理流程把 installedPlugins 下的非最高版本目录删掉。
            var targetEntry = current.Where(x => x.Flavor == target && x.IsUsable)
                .OrderByDescending(x => x.IsDev)
                .ThenByDescending(x => x.Version)
                .FirstOrDefault();
            if (targetEntry is null)
            {
                this.Fail($"当前没有可用的{FlavorLabel(target)} vnavmesh 条目：Versions 槽位缺失或未登记为开发插件。");
                return;
            }

            // 阶段 1：目标版本已经是唯一在跑的版本，且配置位也对得上，直接收尾。
            var loadedNow = current.Where(x => x.IsLoaded).ToList();
            var previousFlavor = loadedNow.Count == 1 ? loadedNow[0].Flavor : (VnavmeshFlavor?)null;
            if (loadedNow.Count == 1
                && ReferenceEquals(loadedNow[0].Instance, targetEntry.Instance)
                && !this.IsProfileMismatched(configsRoot, target))
            {
                await this.PersistAsync(targetEntry, current).ConfigureAwait(false);
                this.SetStatus($"{FlavorLabel(target)} {targetEntry.Version} 已在运行，无需切换。");
                return;
            }

            // 阶段 2：关闭所有正在运行的 vnavmesh（含目标版本本身，保证干净起点）。
            foreach (var entry in loadedNow)
            {
                this.SetStatus($"正在关闭{entry.FlavorName} {entry.Version} …");
                try
                {
                    await DalamudInternal.UnloadAsync(entry.Instance, UnloadTimeout, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    this.Fail($"关闭{entry.FlavorName} {entry.Version} 失败：{ex.Message}");
                    return;
                }
            }

            // 阶段 3：严格等待——必须所有版本都真正处于已卸载状态。
            if (!await this.WaitUntilAsync(
                    () =>
                    {
                        var list = Relevant(this.SnapshotSafe());
                        return list.Count > 0
                               && list.All(x => x.IsFullyClosed)
                               && list.All(x => x.StateName != "UnloadError");
                    },
                    UnloadTimeout,
                    token).ConfigureAwait(false))
            {
                this.Fail("等待 vnavmesh 完全卸载超时或检测到 UnloadError，已中止加载（当前没有加载任何版本）。请检查 /xllog，必要时重启游戏。");
                return;
            }

            // 阶段 4：二次确认。任何活动状态都视为失败，绝不冒险进入加载。
            var closed = Relevant(this.SnapshotSafe());
            if (closed.Count == 0)
            {
                this.Fail("vnavmesh 条目在关闭后意外消失，已中止。");
                return;
            }

            if (closed.Any(x => x.IsLoaded || x.IsBusy))
            {
                this.Fail("仍有 vnavmesh 处于活动状态，已中止加载。");
                return;
            }

            if (closed.Any(x => x.StateName == "UnloadError"))
            {
                this.Fail("检测到 UnloadError，已中止加载。请重启游戏后再试。");
                return;
            }

            var targetFinal = closed.FirstOrDefault(x => ReferenceEquals(x.Instance, targetEntry.Instance))
                              ?? closed.Where(x => x.Flavor == target && x.IsUsable)
                                  .OrderByDescending(x => x.IsDev)
                                  .ThenByDescending(x => x.Version)
                                  .FirstOrDefault();

            if (targetFinal is null)
            {
                this.Fail($"关闭后找不到{FlavorLabel(target)}条目，已中止加载。");
                return;
            }

            // 阶段 4.5：交换两版的配置目录。这时所有 vnavmesh 都已真正卸载完成，
            // 活动目录没有任何句柄，rename 是纯元数据操作（O(1)），不复制数据。
            if (configsRoot is not null)
            {
                try
                {
                    await VnavmeshProfileStore
                        .RecoverIfNeededAsync(configsRoot, this.log, token)
                        .ConfigureAwait(false);

                    // 首次归属：把用户现有的那套共享配置智能拆分到 Global / CN 两个槽位。
                    // 没装过 vnavmesh 时这里只会登记归属，两个槽位保持空目录。
                    await VnavmeshProfileStore
                        .MigrateIfNeededAsync(configsRoot, this.log, token)
                        .ConfigureAwait(false);

                    var from = this.ResolveActiveFlavor(configsRoot, previousFlavor);
                    if (from != target)
                    {
                        this.SetStatus($"正在换位{FlavorLabel(from)}与{FlavorLabel(target)}的配置文件 …");
                    }

                    await VnavmeshProfileStore
                        .SwapAsync(configsRoot, from, target, this.log, token)
                        .ConfigureAwait(false);

                    this.config.LastActiveFlavor = target;
                    this.SaveConfig();
                }
                catch (Exception ex)
                {
                    this.Fail($"切换配置文件失败，已中止加载：{ex.Message}");
                    this.log.Error(ex, "vnavmesh 配置目录换位失败");
                    return;
                }
            }

            // 阶段 5：加载目标版本。
            this.SetStatus($"正在开启{FlavorLabel(target)} {targetFinal.Version} …");
            try
            {
                await DalamudInternal.LoadAsync(targetFinal.Instance, LoadTimeout, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                this.Fail($"开启{FlavorLabel(target)} {targetFinal.Version} 失败：{ex.Message}");
                return;
            }

            // 阶段 6：确认真的进入 Loaded。
            if (!await this.WaitUntilAsync(
                    () => this.SnapshotSafe().Any(x => ReferenceEquals(x.Instance, targetFinal.Instance) && x.IsLoaded),
                    LoadTimeout,
                    token).ConfigureAwait(false))
            {
                this.Fail($"{FlavorLabel(target)} 的加载请求已发出，但未能在限定时间内进入已加载状态，请查看 /xllog。");
                return;
            }

            // 阶段 7：把开关状态写进 profile 与开发插件设置，避免下次启动两版同时加载。
            await this.PersistAsync(targetFinal, this.SnapshotSafe()).ConfigureAwait(false);
            this.SetStatus($"{FlavorLabel(target)} {targetFinal.Version} 已启用。");
        }
        catch (OperationCanceledException)
        {
            this.SetStatus("操作已取消。", true);
        }
        catch (Exception ex)
        {
            this.Fail($"切换失败：{ex.Message}");
            this.log.Error(ex, "vnavmesh 切换失败");
        }
        finally
        {
            Interlocked.Exchange(ref this.busy, 0);
            this.Refresh();
        }
    }

    private async Task RunSyncFromInstalledAsync(CancellationToken token)
    {
        try
        {
            var pluginRoot = this.ResolvePluginRoot();
            if (pluginRoot is null) return;

            await this.RepairDevLocationsAsync(pluginRoot, token).ConfigureAwait(false);

            var summary = await VnavmeshImporter.SyncFromInstalledAsync(pluginRoot, this.log, token).ConfigureAwait(false);
            DalamudInternal.QueueConfigSave();
            this.SetStatus($"导入完成：{summary}");
        }
        catch (OperationCanceledException)
        {
            this.SetStatus("操作已取消。", true);
        }
        catch (Exception ex)
        {
            this.Fail($"导入失败：{ex.Message}");
            this.log.Error(ex, "导入 vnavmesh 版本失败");
        }
        finally
        {
            Interlocked.Exchange(ref this.busy, 0);
            this.Refresh();
        }
    }

    private async Task RunOnlineUpdateAsync(CancellationToken token)
    {
        try
        {
            var pluginRoot = this.ResolvePluginRoot();
            if (pluginRoot is null) return;

            await this.RepairDevLocationsAsync(pluginRoot, token).ConfigureAwait(false);

            var summary = await VnavmeshImporter.UpdateFromRepositoriesAsync(pluginRoot, this.log, token).ConfigureAwait(false);
            DalamudInternal.QueueConfigSave();
            this.SetStatus($"线上更新完成：{summary}");
        }
        catch (OperationCanceledException)
        {
            this.SetStatus("操作已取消。", true);
        }
        catch (Exception ex)
        {
            this.Fail($"线上更新失败：{ex.Message}");
            this.log.Error(ex, "线上更新 vnavmesh 失败");
        }
        finally
        {
            Interlocked.Exchange(ref this.busy, 0);
            this.Refresh();
        }
    }

    private string? ResolvePluginRoot()
    {
        var root = Path.GetDirectoryName(this.pluginInterface.AssemblyLocation.FullName);
        if (string.IsNullOrEmpty(root))
        {
            this.Fail("无法定位本插件所在目录。");
            return null;
        }

        return root;
    }

    /// <summary>取 <c>pluginConfigs</c> 根目录（本插件配置目录的父目录）。</summary>
    private string? ResolvePluginConfigsRoot()
    {
        try
        {
            var own = this.pluginInterface.GetPluginConfigDirectory();
            var root = VnavmeshProfileStore.PluginConfigsRoot(own);
            if (root is null) this.log.Warning("无法从插件配置目录推导 pluginConfigs 根目录，本次跳过配置共存。");

            return root;
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "读取插件配置目录失败，本次跳过配置共存。");
            return null;
        }
    }

    /// <summary>
    /// 判定活动配置位当前归属哪个版本。
    /// 首次切换时会把现有配置智能拆分（worker → CN，其余 → Global），之后以「上次换位写下的归属标记 → 换位前正在运行的版本 → 上次记录」为准。
    /// 归属标记优先：用户手动用 /xlplugins 换过版本时，活动目录里的配置并没有跟着换，只有标记能说明它是谁的。
    /// </summary>
    private VnavmeshFlavor ResolveActiveFlavor(string configsRoot, VnavmeshFlavor? previousFlavor)
    {
        if (!VnavmeshProfileStore.IsMigrated(configsRoot)) return VnavmeshFlavor.Global;

        return VnavmeshProfileStore.ReadActiveOwner(configsRoot)
               ?? previousFlavor
               ?? this.config.LastActiveFlavor
               ?? VnavmeshFlavor.Global;
    }

    /// <summary>
    /// 目标版本的配置位是否与当前活动配置位对不上。
    /// 对不上时即使目标版本已经在运行，也要走完整流程把配置换回来，否则它读到的还是另一版的缓存。
    /// </summary>
    private bool IsProfileMismatched(string? configsRoot, VnavmeshFlavor target)
    {
        if (configsRoot is null) return false;
        if (!VnavmeshProfileStore.IsMigrated(configsRoot)) return false;

        var owner = VnavmeshProfileStore.ReadActiveOwner(configsRoot) ?? this.config.LastActiveFlavor;
        return owner is not null && owner.Value != target;
    }

    private void SaveConfig()
    {
        try
        {
            this.pluginInterface.SavePluginConfig(this.config);
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "保存 vnavmesh-tool 配置失败");
        }
    }

    private void ProbeProfile(bool force = false)
    {
        if (!force && DateTime.UtcNow - this.lastProfileProbeUtc < TimeSpan.FromSeconds(1)) return;
        this.lastProfileProbeUtc = DateTime.UtcNow;

        try
        {
            Volatile.Write(ref this.profileSummary, this.DescribeProfile());
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "读取配置共存状态失败。");
        }
    }

    private string DescribeProfile()
    {
        if (!this.config.ProfileIsolationEnabled) return "配置共存：已关闭，两版共用同一套配置文件。";

        var root = this.ResolvePluginConfigsRoot();
        if (root is null) return "配置共存：无法定位 pluginConfigs 目录。";

        if (!VnavmeshProfileStore.IsMigrated(root)) return "配置共存：尚未归属，首次切换会智能拆分现有配置。";

        var owner = VnavmeshProfileStore.ReadActiveOwner(root) ?? this.config.LastActiveFlavor;
        var ownerText = owner is null ? "未知" : FlavorLabel(owner.Value);

        var loaded = this.Entries.FirstOrDefault(x => x.IsLoaded && x.IsUsable);
        if (owner is not null && loaded is not null && loaded.Flavor != owner.Value)
        {
            return $"配置共存：活动配置位 {ownerText}，当前加载 {FlavorLabel(loaded.Flavor)}（不匹配，点任意切换修复）。";
        }

        return $"配置共存：活动配置位 {ownerText}。";
    }

    private async Task PersistAsync(VnavmeshEntry chosen, IReadOnlyList<VnavmeshEntry> snapshot)
    {
        try
        {
            foreach (var entry in snapshot.Where(x => x.IsUsable))
            {
                var enabled = ReferenceEquals(entry.Instance, chosen.Instance);
                DalamudInternal.SetStartOnBoot(entry.Instance, enabled);
                await DalamudInternal.SetProfileWantedAsync(entry.WorkingPluginId, entry.InternalName, enabled).ConfigureAwait(false);
            }

            DalamudInternal.QueueConfigSave();
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "写入 vnavmesh 开关持久化状态失败");
        }
    }

    /// <summary>
    /// 参与冲突判定的条目：路径有效，或仍被加载 / 正在过渡 / 上一次卸载失败。
    /// 指向已删目录、且从未成功加载的旧条目会被排除——它们既占用不了程序集，也加载不了。
    /// </summary>
    private static List<VnavmeshEntry> Relevant(IReadOnlyList<VnavmeshEntry> snapshot) =>
        snapshot.Where(x => x.IsUsable || x.IsLoaded || x.IsBusy || string.Equals(x.StateName, "UnloadError", StringComparison.Ordinal)).ToList();

    /// <summary>
    /// 自愈开发插件登记：清掉指向已删目录的旧条目，把 Versions\Global 与 Versions\CN 补登记，
    /// 再让 Dalamud 重新扫描开发插件，使本次进程内立刻能识别到正确的两个版本。
    /// </summary>
    private async Task RepairDevLocationsAsync(string pluginRoot, CancellationToken token)
    {
        var versionsRoot = Path.Combine(pluginRoot, VnavmeshImporter.VersionsFolderName);
        var changed = DalamudInternal.RemoveStaleDevLocations(versionsRoot + Path.DirectorySeparatorChar) > 0;

        foreach (var flavor in new[] { VnavmeshFlavor.Global, VnavmeshFlavor.China })
        {
            var dll = Path.Combine(VnavmeshImporter.SlotDirectory(pluginRoot, flavor), "vnavmesh.dll");
            if (!File.Exists(dll)) continue;

            var nickname = flavor == VnavmeshFlavor.China ? "vnavmesh CN" : "vnavmesh Global";
            if (DalamudInternal.RegisterDevPluginLocation(dll, nickname)) changed = true;
        }

        if (!changed) return;

        DalamudInternal.QueueConfigSave();
        this.SetStatus("正在同步 vnavmesh 的开发插件路径 …");
        await DalamudInternal.ScanDevPluginsAsync().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }

    private async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            if (condition()) return true;
            await Task.Delay(PollInterval, token).ConfigureAwait(false);
        }

        return condition();
    }

    private IReadOnlyList<VnavmeshEntry> SnapshotSafe()
    {
        try
        {
            lock (this.gate)
            {
                this.entries = DalamudInternal.Snapshot();
                this.lastRefreshUtc = DateTime.UtcNow;
                return this.entries;
            }
        }
        catch (Exception ex)
        {
            this.log.Debug(ex, "读取 vnavmesh 列表失败，沿用上一次快照。");
            return this.Entries;
        }
    }

    private void SetStatus(string text, bool isError = false)
    {
        Volatile.Write(ref this.statusText, text);
        Volatile.Write(ref this.statusIsError, isError);
    }

    private void Fail(string message)
    {
        this.SetStatus(message, true);
        this.log.Warning(message);
    }
}
