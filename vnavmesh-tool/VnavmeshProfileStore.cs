using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace VnavmeshTool;

/// <summary>
/// 两版 vnavmesh 的配置目录隔离。
/// <para>
/// 两版（1.2.3.15 与 0.9.3.0）都只通过 <c>IDalamudPluginInterface.ConfigDirectory</c> /
/// <c>GetPluginConfigDirectory()</c> 取配置根目录，而该路径恒为
/// <c>pluginConfigs\{InternalName}</c>，并且 Dalamud 每次调用都重新拼一次字符串
/// （<c>PluginConfigurations.GetConfigDirectory</c> 无缓存）。所以只要在「全部卸载完成之后、
/// 加载目标版本之前」把活动目录与目标槽位做一次 NTFS rename 交换，两版就各自持有一套完整配置，
/// 不需要复制任何字节，也不需要注入或 patch 任何程序集。
/// </para>
/// </summary>
internal static class VnavmeshProfileStore
{
    internal const string StoreFolderName = "vnavmesh-tool-store";
    internal const string ActiveFolderName = "vnavmesh";
    internal const string ActiveConfigFileName = "vnavmesh.json";

    private const string PendingFileName = ".pending";
    private const string OwnerFileName = ".active";
    private const string WorkerProcessName = "vnavmesh.Worker";
    private const string WorkerFolderName = "worker";
    private const string MigrateStep = "migrate";

    private static readonly TimeSpan WorkerExitTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan MoveRetryTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(250);

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    /// <summary>插件加载时的自动修复与切换流程可能同时进来，所有目录操作串行化。</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>一次未完成的换位事务。</summary>
    private sealed class PendingMove
    {
        public string From { get; set; } = string.Empty;

        public string To { get; set; } = string.Empty;

        public string Step { get; set; } = string.Empty;
    }

    internal static string SlotFolderName(VnavmeshFlavor flavor) =>
        flavor == VnavmeshFlavor.China ? "CN" : "Global";

    /// <summary>由本插件自己的配置目录推出 <c>pluginConfigs</c> 根目录。</summary>
    internal static string? PluginConfigsRoot(string ownConfigDirectory)
    {
        if (string.IsNullOrWhiteSpace(ownConfigDirectory)) return null;

        var trimmed = Path.TrimEndingDirectorySeparator(ownConfigDirectory);
        return Path.GetDirectoryName(trimmed);
    }

    internal static string StoreRoot(string pluginConfigsRoot) => Path.Combine(pluginConfigsRoot, StoreFolderName);

    internal static string SlotRoot(string pluginConfigsRoot, VnavmeshFlavor flavor) =>
        Path.Combine(StoreRoot(pluginConfigsRoot), SlotFolderName(flavor));

    internal static string SlotDirectory(string pluginConfigsRoot, VnavmeshFlavor flavor) =>
        Path.Combine(SlotRoot(pluginConfigsRoot, flavor), ActiveFolderName);

    internal static string SlotConfigFile(string pluginConfigsRoot, VnavmeshFlavor flavor) =>
        Path.Combine(SlotRoot(pluginConfigsRoot, flavor), ActiveConfigFileName);

    internal static string ActiveDirectory(string pluginConfigsRoot) =>
        Path.Combine(pluginConfigsRoot, ActiveFolderName);

    internal static string ActiveConfigFile(string pluginConfigsRoot) =>
        Path.Combine(pluginConfigsRoot, ActiveConfigFileName);

    /// <summary>
    /// 是否已经完成过首次归属：写过归属标记，或任一槽位里已经存在配置。
    /// 只建过空槽位目录不算，<see cref="EnsureLayout"/> 每次都会建它们。
    /// </summary>
    internal static bool IsMigrated(string pluginConfigsRoot) =>
        File.Exists(Path.Combine(StoreRoot(pluginConfigsRoot), OwnerFileName))
        || Directory.Exists(SlotDirectory(pluginConfigsRoot, VnavmeshFlavor.Global))
        || Directory.Exists(SlotDirectory(pluginConfigsRoot, VnavmeshFlavor.China))
        || File.Exists(SlotConfigFile(pluginConfigsRoot, VnavmeshFlavor.Global))
        || File.Exists(SlotConfigFile(pluginConfigsRoot, VnavmeshFlavor.China));

    /// <summary>读取活动配置位的归属版本。</summary>
    internal static VnavmeshFlavor? ReadActiveOwner(string pluginConfigsRoot)
    {
        try
        {
            var marker = Path.Combine(StoreRoot(pluginConfigsRoot), OwnerFileName);
            if (!File.Exists(marker)) return null;

            var text = File.ReadAllText(marker).Trim();
            return Enum.TryParse<VnavmeshFlavor>(text, true, out var flavor) ? flavor : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>保证 store 骨架存在。</summary>
    internal static void EnsureLayout(string pluginConfigsRoot)
    {
        foreach (var flavor in new[] { VnavmeshFlavor.Global, VnavmeshFlavor.China })
        {
            Directory.CreateDirectory(SlotRoot(pluginConfigsRoot, flavor));
        }
    }

    /// <summary>
    /// 首次归属：把活动位现有的那套共享配置**智能拆分**到两个槽位。
    /// <list type="bullet">
    /// <item><c>worker</c> 子目录只可能由中国服版产生（国际服版源码里没有 worker 概念），归 CN 槽位；</item>
    /// <item>其余内容（<c>meshcache</c>、<c>customization-editor</c> 等）归 Global 槽位；</item>
    /// <item><c>vnavmesh.json</c> 很小，两版各留一份副本，谁也不会丢设置；</item>
    /// <item>活动位不存在或为空（从没装过 vnavmesh）时只登记归属，两个槽位保持空目录。</item>
    /// </list>
    /// 判断用的是「有没有实际条目」，所以「装过国际服版」「装过中国服版」「两版都装过」「都没装过」
    /// 四种前置情况都能落在正确分支上。整个过程用 <c>.pending</c> 标记，中途崩溃可重入续做。
    /// </summary>
    internal static async Task MigrateIfNeededAsync(string pluginConfigsRoot, IPluginLog log, CancellationToken token)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await MigrateCoreAsync(pluginConfigsRoot, log, token).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task MigrateCoreAsync(string pluginConfigsRoot, IPluginLog log, CancellationToken token)
    {
        EnsureLayout(pluginConfigsRoot);

        // 完成判据只看归属标记：槽位里出现目录可能只是上次拆到一半，不能当作已完成。
        if (File.Exists(Path.Combine(StoreRoot(pluginConfigsRoot), OwnerFileName))) return;

        WritePending(pluginConfigsRoot, VnavmeshFlavor.Global, VnavmeshFlavor.Global, MigrateStep);

        var activeDir = ActiveDirectory(pluginConfigsRoot);
        var globalDir = SlotDirectory(pluginConfigsRoot, VnavmeshFlavor.Global);
        var chinaDir = SlotDirectory(pluginConfigsRoot, VnavmeshFlavor.China);

        var movedToChina = 0;
        var movedToGlobal = 0;

        if (Directory.Exists(activeDir))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(activeDir).ToList())
            {
                token.ThrowIfCancellationRequested();

                var name = Path.GetFileName(entry);
                var isWorker = string.Equals(name, WorkerFolderName, StringComparison.OrdinalIgnoreCase);
                var destination = Path.Combine(isWorker ? chinaDir : globalDir, name);

                if (Directory.Exists(entry))
                {
                    await MoveDirectoryAsync(entry, destination, token).ConfigureAwait(false);
                }
                else
                {
                    MoveFile(entry, destination);
                }

                if (isWorker) movedToChina++;
                else movedToGlobal++;
            }
        }

        // 活动目录留下一个空壳：换位流程会按需删掉或直接利用。
        Directory.CreateDirectory(activeDir);

        // 配置项是几百字节的设置文件，两版各留一份副本，避免任何一方丢掉自己的设置。
        var activeJson = ActiveConfigFile(pluginConfigsRoot);
        if (File.Exists(activeJson))
        {
            foreach (var flavor in new[] { VnavmeshFlavor.Global, VnavmeshFlavor.China })
            {
                var slotJson = SlotConfigFile(pluginConfigsRoot, flavor);
                if (File.Exists(slotJson)) continue;

                Directory.CreateDirectory(Path.GetDirectoryName(slotJson)!);
                File.Copy(activeJson, slotJson);
            }

            // 活动位自己的 json 先移除，换位时由标准流程把目标槽位的那份搬回来。
            File.Delete(activeJson);
        }

        WriteActiveOwner(pluginConfigsRoot, VnavmeshFlavor.Global);
        DeletePending(pluginConfigsRoot);

        log.Info(
            $"[vnavmesh-tool] 首次归属完成：{movedToGlobal} 项归 Global，{movedToChina} 项（worker）归 CN。");
    }

    /// <summary>
    /// 崩溃 / 异常退出后的自愈：<c>.pending</c> 存在说明上次死在两次 rename 之间。
    /// 优先补完目标槽位，补不了就回滚，最后保证活动目录一定存在。
    /// </summary>
    internal static async Task RecoverIfNeededAsync(string pluginConfigsRoot, IPluginLog log, CancellationToken token)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await RecoverCoreAsync(pluginConfigsRoot, log, token).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task RecoverCoreAsync(string pluginConfigsRoot, IPluginLog log, CancellationToken token)
    {
        EnsureLayout(pluginConfigsRoot);

        var pending = ReadPending(pluginConfigsRoot);
        var activeDir = ActiveDirectory(pluginConfigsRoot);

        if (pending is null)
        {
            if (!Directory.Exists(activeDir)) Directory.CreateDirectory(activeDir);
            return;
        }

        if (string.Equals(pending.Step, MigrateStep, StringComparison.Ordinal))
        {
            // 首次归属被打断。MigrateCoreAsync 是幂等的：把活动位里剩下的条目继续按规则拆开就行。
            log.Warning("[vnavmesh-tool] 检测到未完成的首次归属，继续拆分现有配置。");
            DeletePending(pluginConfigsRoot);
            await MigrateCoreAsync(pluginConfigsRoot, log, token).ConfigureAwait(false);
            return;
        }

        log.Warning(
            $"[vnavmesh-tool] 检测到未完成的配置换位事务（{pending.From} → {pending.To}，step={pending.Step}），开始修复。");

        var to = ParseFlavor(pending.To);
        var from = ParseFlavor(pending.From);

        // settled = 修复后活动配置位归属哪个版本。目录和 json 必须跟着同一个归属走，
        // 否则会出现「目录是中国服版的缓存、json 是国际服版的设置」这种错配。
        VnavmeshFlavor? settled = null;

        if (!Directory.Exists(activeDir))
        {
            // 活动位不存在说明 out 阶段已经把数据搬回槽位，事务越过了中点，只能往前补完 to。
            var toDir = to is null ? null : SlotDirectory(pluginConfigsRoot, to.Value);
            var fromDir = from is null ? null : SlotDirectory(pluginConfigsRoot, from.Value);

            if (toDir is not null && Directory.Exists(toDir))
            {
                await MoveDirectoryAsync(toDir, activeDir, token).ConfigureAwait(false);
                settled = to;
            }
            else if (fromDir is not null && Directory.Exists(fromDir))
            {
                await MoveDirectoryAsync(fromDir, activeDir, token).ConfigureAwait(false);
                settled = from;
                log.Warning($"[vnavmesh-tool] 配置换位未完成，已回滚到 {pending.From}。");
            }
            else
            {
                Directory.CreateDirectory(activeDir);
            }
        }
        else
        {
            // 活动位还在：out 阶段没搬完，仍然属于 from。
            settled = from;
        }

        var activeJson = ActiveConfigFile(pluginConfigsRoot);
        if (!File.Exists(activeJson) && settled is not null)
        {
            var settledJson = SlotConfigFile(pluginConfigsRoot, settled.Value);
            if (File.Exists(settledJson)) MoveFile(settledJson, activeJson);
        }

        if (settled is not null) WriteActiveOwner(pluginConfigsRoot, settled.Value);

        DeletePending(pluginConfigsRoot);
    }

    /// <summary>
    /// 把活动配置位与目标槽位交换。调用前必须已确认所有 vnavmesh 版本都真正卸载完成。
    /// </summary>
    internal static async Task SwapAsync(
        string pluginConfigsRoot,
        VnavmeshFlavor from,
        VnavmeshFlavor to,
        IPluginLog log,
        CancellationToken token)
    {
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await SwapCoreAsync(pluginConfigsRoot, from, to, log, token).ConfigureAwait(false);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task SwapCoreAsync(
        string pluginConfigsRoot,
        VnavmeshFlavor from,
        VnavmeshFlavor to,
        IPluginLog log,
        CancellationToken token)
    {
        EnsureLayout(pluginConfigsRoot);

        if (from == to)
        {
            // 同一个槽位：只需保证活动位里有数据（例如刚刚做完首次归属，或者用户手动删过目录）。
            var active = ActiveDirectory(pluginConfigsRoot);
            var slot = SlotDirectory(pluginConfigsRoot, to);
            if (!Directory.Exists(active) || !HasAnyEntry(active))
            {
                if (Directory.Exists(slot)) await MoveDirectoryAsync(slot, active, token).ConfigureAwait(false);
                else Directory.CreateDirectory(active);
            }

            var activeConfig = ActiveConfigFile(pluginConfigsRoot);
            var slotConfig = SlotConfigFile(pluginConfigsRoot, to);
            if (!File.Exists(activeConfig) && File.Exists(slotConfig)) MoveFile(slotConfig, activeConfig);

            WriteActiveOwner(pluginConfigsRoot, to);
            return;
        }

        await WaitWorkerExitAsync(log, token).ConfigureAwait(false);

        WritePending(pluginConfigsRoot, from, to, "begin");

        var activeDir = ActiveDirectory(pluginConfigsRoot);
        var fromDir = SlotDirectory(pluginConfigsRoot, from);
        var fromJson = SlotConfigFile(pluginConfigsRoot, from);

        // 1) 把当前活动配置搬回它自己的槽位。
        if (Directory.Exists(activeDir))
        {
            if (HasAnyEntry(activeDir))
            {
                await MoveDirectoryAsync(activeDir, fromDir, token).ConfigureAwait(false);
            }
            else
            {
                TryDeleteEmptyDirectory(activeDir);
            }
        }

        var activeJson = ActiveConfigFile(pluginConfigsRoot);
        if (File.Exists(activeJson)) MoveFile(activeJson, fromJson);

        WritePending(pluginConfigsRoot, from, to, "out-done");

        // 2) 把目标槽位搬到活动位。
        var toDir = SlotDirectory(pluginConfigsRoot, to);
        if (Directory.Exists(toDir))
        {
            await MoveDirectoryAsync(toDir, activeDir, token).ConfigureAwait(false);
        }
        else
        {
            Directory.CreateDirectory(activeDir);
            log.Info($"[vnavmesh-tool] {SlotFolderName(to)} 槽位还没有配置，将从空目录开始生成。");
        }

        var toJson = SlotConfigFile(pluginConfigsRoot, to);
        if (File.Exists(toJson)) MoveFile(toJson, activeJson);

        WriteActiveOwner(pluginConfigsRoot, to);
        DeletePending(pluginConfigsRoot);

        log.Info($"[vnavmesh-tool] 配置目录已从 {SlotFolderName(from)} 换位到 {SlotFolderName(to)}。");
    }

    /// <summary>把当前活动配置归属记为指定版本（首次切换用的登记动作）。</summary>
    internal static void WriteActiveOwner(string pluginConfigsRoot, VnavmeshFlavor flavor)
    {
        try
        {
            Directory.CreateDirectory(StoreRoot(pluginConfigsRoot));
            File.WriteAllText(Path.Combine(StoreRoot(pluginConfigsRoot), OwnerFileName), flavor.ToString());
        }
        catch
        {
            // 归属标记只是辅助信息，写失败不影响换位本身。
        }
    }

    private static VnavmeshFlavor? ParseFlavor(string? text) =>
        Enum.TryParse<VnavmeshFlavor>(text, true, out var flavor) ? flavor : null;

    private static bool HasAnyEntry(string directory)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(directory).Any();
        }
        catch
        {
            return false;
        }
    }

    private static async Task WaitWorkerExitAsync(IPluginLog log, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + WorkerExitTimeout;
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            if (!IsWorkerRunning()) return;
            await Task.Delay(RetryInterval, token).ConfigureAwait(false);
        }

        if (IsWorkerRunning())
        {
            log.Warning("[vnavmesh-tool] vnavmesh.Worker.exe 仍在运行，仍尝试换位配置目录。");
        }
    }

    private static bool IsWorkerRunning()
    {
        try
        {
            var processes = Process.GetProcessesByName(WorkerProcessName);
            try
            {
                return processes.Length > 0;
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
        }
        catch
        {
            return false;
        }
    }

    private static async Task MoveDirectoryAsync(string source, string target, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        if (Directory.Exists(target)) await DeleteDirectoryAsync(target, token).ConfigureAwait(false);

        var deadline = DateTime.UtcNow + MoveRetryTimeout;
        while (true)
        {
            token.ThrowIfCancellationRequested();

            try
            {
                Directory.Move(source, target);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(RetryInterval, token).ConfigureAwait(false);
            }
        }
    }

    private static async Task DeleteDirectoryAsync(string target, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + MoveRetryTimeout;
        while (true)
        {
            token.ThrowIfCancellationRequested();

            try
            {
                // 防御性：如果目标意外是目录联接 / 符号链接，只删链接本身，不递归跟进。
                var attributes = File.GetAttributes(target);
                if ((attributes & FileAttributes.ReparsePoint) != 0) Directory.Delete(target);
                else Directory.Delete(target, true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(RetryInterval, token).ConfigureAwait(false);
            }
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !HasAnyEntry(path)) Directory.Delete(path);
        }
        catch
        {
            // 空目录删不掉无关紧要，下一次操作会再试。
        }
    }

    private static void MoveFile(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(source, target, true);
    }

    private static void WritePending(string pluginConfigsRoot, VnavmeshFlavor from, VnavmeshFlavor to, string step)
    {
        try
        {
            var root = StoreRoot(pluginConfigsRoot);
            Directory.CreateDirectory(root);
            var payload = new PendingMove { From = from.ToString(), To = to.ToString(), Step = step };
            File.WriteAllText(Path.Combine(root, PendingFileName), JsonSerializer.Serialize(payload, SerializerOptions));
        }
        catch
        {
            // 事务日志写不进去时仍然继续，只是少了崩溃恢复的依据。
        }
    }

    private static PendingMove? ReadPending(string pluginConfigsRoot)
    {
        try
        {
            var path = Path.Combine(StoreRoot(pluginConfigsRoot), PendingFileName);
            if (!File.Exists(path)) return null;

            return JsonSerializer.Deserialize<PendingMove>(File.ReadAllText(path), SerializerOptions);
        }
        catch
        {
            return null;
        }
    }

    private static void DeletePending(string pluginConfigsRoot)
    {
        try
        {
            var path = Path.Combine(StoreRoot(pluginConfigsRoot), PendingFileName);
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 忽略。
        }
    }
}
