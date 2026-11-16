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
    private readonly object gate = new();

    private IReadOnlyList<VnavmeshEntry> entries = [];
    private DateTime lastRefreshUtc = DateTime.MinValue;
    private int busy;
    private string statusText = "就绪。";
    private bool statusIsError;
    private CancellationTokenSource? cancellation;

    public VnavmeshSwitcher(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
    }

    public IReadOnlyList<VnavmeshEntry> Entries => Volatile.Read(ref this.entries);

    public bool IsBusy => Volatile.Read(ref this.busy) != 0;

    public string StatusText => Volatile.Read(ref this.statusText);

    public bool StatusIsError => Volatile.Read(ref this.statusIsError);

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
        var list = this.Entries;

        if (list.Count == 0)
        {
            this.Fail("没有检测到任何版本的 vnavmesh 插件。");
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

    /// <summary>把 installedPlugins 下的所有 vnavmesh 版本登记为开发插件。</summary>
    public void RequestImport()
    {
        if (Interlocked.CompareExchange(ref this.busy, 1, 0) != 0)
        {
            this.Fail("上一次操作还在进行中，请稍候。");
            return;
        }

        this.cancellation?.Dispose();
        this.cancellation = new CancellationTokenSource();
        var token = this.cancellation.Token;

        this.SetStatus("正在扫描并导入 vnavmesh 版本 …");
        _ = Task.Run(() => this.RunImportAsync(token));
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
            // 阶段 0：先让所有条目离开「加载中 / 卸载中」这种过渡状态。
            if (!await this.WaitUntilAsync(
                    () => this.SnapshotSafe().All(x => !x.IsBusy),
                    StableTimeout,
                    token).ConfigureAwait(false))
            {
                this.Fail("等待插件状态稳定超时：仍有 vnavmesh 处于加载 / 卸载过程中，已中止。");
                return;
            }

            var current = this.SnapshotSafe();
            if (current.Count == 0)
            {
                this.Fail("没有检测到任何版本的 vnavmesh 插件。");
                return;
            }

            // 同一个分支可能同时存在「已安装」条目和「开发插件」条目：优先用开发插件那份，
            // 避免启动清理流程把 installedPlugins 下的非最高版本目录删掉。
            var targetEntry = current.Where(x => x.Flavor == target)
                .OrderByDescending(x => x.IsDev)
                .ThenByDescending(x => x.Version)
                .FirstOrDefault();
            if (targetEntry is null)
            {
                this.Fail($"当前没有可用的{FlavorLabel(target)} vnavmesh 条目。");
                return;
            }

            // 阶段 1：目标版本已经是唯一在跑的版本，直接收尾。
            var loadedNow = current.Where(x => x.IsLoaded).ToList();
            if (loadedNow.Count == 1 && ReferenceEquals(loadedNow[0].Instance, targetEntry.Instance))
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
                        var list = this.SnapshotSafe();
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
            var closed = this.SnapshotSafe();
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
                              ?? closed.Where(x => x.Flavor == target)
                                  .OrderByDescending(x => x.IsDev)
                                  .ThenByDescending(x => x.Version)
                                  .FirstOrDefault();

            if (targetFinal is null)
            {
                this.Fail($"关闭后找不到{FlavorLabel(target)}条目，已中止加载。");
                return;
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

    private async Task RunImportAsync(CancellationToken token)
    {
        try
        {
            var versions = VnavmeshImporter.ScanInstalledVersions(this.log);
            if (versions.Count == 0)
            {
                this.Fail("未在已安装插件目录中找到 vnavmesh 版本目录（installedPlugins\\vnavmesh\\<版本>）。");
                return;
            }

            var pluginRoot = Path.GetDirectoryName(this.pluginInterface.AssemblyLocation.FullName);
            if (string.IsNullOrEmpty(pluginRoot))
            {
                this.Fail("无法定位本插件所在目录。");
                return;
            }

            var registered = new List<string>();
            foreach (var version in versions)
            {
                token.ThrowIfCancellationRequested();

                var dllPath = await VnavmeshImporter.ImportAsync(version, pluginRoot, this.log, token).ConfigureAwait(false);
                if (dllPath is null) continue;

                var nickname = $"{version.FlavorName} {version.Version}";
                if (DalamudInternal.RegisterDevPluginLocation(dllPath, nickname)) registered.Add(nickname);
            }

            if (registered.Count == 0)
            {
                this.Fail("没有新的版本需要导入（目标位置已存在，或已全部登记为开发插件）。");
                return;
            }

            DalamudInternal.QueueConfigSave();
            await DalamudInternal.ScanDevPluginsAsync().ConfigureAwait(false);

            // 新导入的版本一律默认关闭，只保留当前正在运行的那一版。
            var snapshot = this.SnapshotSafe();
            var running = snapshot.FirstOrDefault(x => x.IsLoaded);
            foreach (var entry in snapshot)
            {
                var enabled = running is not null && ReferenceEquals(entry.Instance, running.Instance);
                DalamudInternal.SetStartOnBoot(entry.Instance, enabled);
                await DalamudInternal.SetProfileWantedAsync(entry.WorkingPluginId, entry.InternalName, enabled).ConfigureAwait(false);
            }

            DalamudInternal.QueueConfigSave();
            this.SetStatus($"已导入 {registered.Count} 个版本：{string.Join("、", registered)}。");
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

    private async Task PersistAsync(VnavmeshEntry chosen, IReadOnlyList<VnavmeshEntry> snapshot)
    {
        try
        {
            foreach (var entry in snapshot)
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
