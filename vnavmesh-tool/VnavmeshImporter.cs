using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace VnavmeshTool;

/// <summary>installedPlugins 目录里发现的一份 vnavmesh 版本。</summary>
internal sealed class InstalledVnavmeshVersion
{
    public required VnavmeshFlavor Flavor { get; init; }

    public required Version Version { get; init; }

    public required string SourceDirectory { get; init; }

    public required string DllPath { get; init; }

    public required string Name { get; init; }

    public required string Author { get; init; }

    public string FlavorName => this.Flavor == VnavmeshFlavor.China ? "中国服版" : "国际服版";
}

/// <summary>插件库（pluginmaster）里的一个 vnavmesh 候选。</summary>
internal sealed class VnavmeshPackage
{
    public required VnavmeshFlavor Flavor { get; init; }

    public required Version Version { get; init; }

    public required string DownloadLink { get; init; }

    public required string Source { get; init; }
}

/// <summary>
/// 维护本插件 Versions 目录下的两个槽位：<c>Versions\Global</c> 与 <c>Versions\CN</c>。
/// 每个槽位始终只保留一个最新版本，文件夹名不带版本号。
/// </summary>
internal static class VnavmeshImporter
{
    private const string UserAgent = "vnavmesh-tool/1.0.1.1";

    internal const string VersionsFolderName = "Versions";
    internal const string GlobalFolderName = "Global";
    internal const string ChinaFolderName = "CN";

    internal const string GlobalRepositoryUrl = "https://puni.sh/api/repository/veyn";
    internal const string ChinaRepositoryUrl = "https://raw.githubusercontent.com/AtmoOmen/DalamudPlugins/main/pluginmaster.json";

    private static readonly TimeSpan UnloadTimeout = TimeSpan.FromSeconds(150);
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(150);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    private static readonly string[] FallbackProxies =
    [
        "http://127.0.0.1:10853",
        "http://127.0.0.1:10852",
        "http://127.0.0.1:7890",
    ];

    private static readonly HttpClient Http = CreateHttpClient();

    internal static string SlotDirectory(string pluginRoot, VnavmeshFlavor flavor) =>
        Path.Combine(pluginRoot, VersionsFolderName, flavor == VnavmeshFlavor.China ? ChinaFolderName : GlobalFolderName);

    internal static IReadOnlyList<InstalledVnavmeshVersion> ScanInstalledVersions(IPluginLog log)
    {
        var result = new List<InstalledVnavmeshVersion>();

        foreach (var root in EnumerateInstallRoots())
        {
            if (!Directory.Exists(root)) continue;

            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var dll = Path.Combine(directory, "vnavmesh.dll");
                var manifestPath = Path.Combine(directory, "vnavmesh.json");
                if (!File.Exists(dll) || !File.Exists(manifestPath)) continue;

                try
                {
                    var manifest = ReadManifest(manifestPath);
                    if (manifest is not { } info) continue;

                    result.Add(new InstalledVnavmeshVersion
                    {
                        Flavor = DalamudInternal.DetectFlavor(info.Name, info.Author),
                        Version = info.Version,
                        SourceDirectory = directory,
                        DllPath = dll,
                        Name = info.Name,
                        Author = info.Author,
                    });
                }
                catch (Exception ex)
                {
                    log.Debug(ex, $"读取 vnavmesh manifest 失败：{manifestPath}");
                }
            }
        }

        return result
            .GroupBy(x => (x.Flavor, x.Version))
            .Select(g => g.First())
            .OrderBy(x => x.Flavor)
            .ThenBy(x => x.Version)
            .ToList();
    }

    /// <summary>每个发行分支在 installedPlugins 里的最高版本。</summary>
    internal static Dictionary<VnavmeshFlavor, InstalledVnavmeshVersion> LatestInstalled(IPluginLog log)
    {
        var result = new Dictionary<VnavmeshFlavor, InstalledVnavmeshVersion>();
        foreach (var group in ScanInstalledVersions(log).GroupBy(x => x.Flavor))
        {
            result[group.Key] = group.OrderByDescending(x => x.Version).First();
        }

        return result;
    }

    /// <summary>读取本地槽位当前版本（直接读磁盘上的 manifest）。</summary>
    internal static Version? ReadSlotVersion(string pluginRoot, VnavmeshFlavor flavor)
    {
        var manifest = Path.Combine(SlotDirectory(pluginRoot, flavor), "vnavmesh.json");
        if (!File.Exists(manifest)) return null;

        try
        {
            return ReadManifest(manifest)?.Version;
        }
        catch
        {
            return null;
        }
    }

    internal static bool HasSlot(string pluginRoot, VnavmeshFlavor flavor) =>
        File.Exists(Path.Combine(SlotDirectory(pluginRoot, flavor), "vnavmesh.dll"));

    /// <summary>③ 用 installedPlugins 里的新版本覆盖本插件槽位。</summary>
    internal static async Task<string> SyncFromInstalledAsync(
        string pluginRoot,
        IPluginLog log,
        CancellationToken cancellationToken)
    {
        var latest = LatestInstalled(log);
        if (latest.Count == 0)
        {
            return "未在 installedPlugins\\vnavmesh 下找到任何 vnavmesh 版本目录。";
        }

        var messages = new List<string>();

        foreach (var (flavor, source) in latest.OrderBy(x => x.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var current = ReadSlotVersion(pluginRoot, flavor);
            if (current is not null && current >= source.Version)
            {
                messages.Add($"{source.FlavorName} {current} 已是最新");
                continue;
            }

            var staging = CreateStagingDirectory();
            try
            {
                await Task.Run(() => CopyDirectory(source.SourceDirectory, staging), cancellationToken).ConfigureAwait(false);
                await ReplaceSlotAsync(flavor, source.Version, staging, pluginRoot, log, cancellationToken).ConfigureAwait(false);
                messages.Add($"{source.FlavorName} {current?.ToString() ?? "（无）"} → {source.Version}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                log.Warning(ex, $"从 installedPlugins 导入 {source.FlavorName} 失败");
                messages.Add($"{source.FlavorName} 导入失败：{ex.Message}");
            }
            finally
            {
                TryDeleteDirectory(staging);
            }
        }

        return string.Join("；", messages);
    }

    /// <summary>④ 从两个上游插件库下载最新版并覆盖本插件槽位。</summary>
    internal static async Task<string> UpdateFromRepositoriesAsync(
        string pluginRoot,
        IPluginLog log,
        CancellationToken cancellationToken)
    {
        var messages = new List<string>();

        (VnavmeshFlavor Flavor, string Url)[] repositories =
        [
            (VnavmeshFlavor.Global, GlobalRepositoryUrl),
            (VnavmeshFlavor.China, ChinaRepositoryUrl),
        ];

        foreach (var (flavor, url) in repositories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var label = VnavmeshSwitcher.FlavorLabel(flavor);

            string json;
            try
            {
                json = await GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                log.Warning(ex, $"读取 {label} 插件库失败：{url}");
                messages.Add($"{label} 读取插件库失败：{ex.Message}");
                continue;
            }

            var package = ParseRepository(json, flavor, url, log);
            if (package is null)
            {
                messages.Add($"{label} 插件库里没有找到 InternalName 为 vnavmesh 的条目");
                continue;
            }

            var current = ReadSlotVersion(pluginRoot, flavor);
            if (current is not null && current >= package.Version)
            {
                messages.Add($"{label} {current} 已是最新（库内 {package.Version}）");
                continue;
            }

            var work = CreateStagingDirectory();
            try
            {
                var zipPath = Path.Combine(work, "vnavmesh.zip");
                await DownloadFileAsync(package.DownloadLink, zipPath, cancellationToken).ConfigureAwait(false);

                var extracted = Path.Combine(work, "extracted");
                await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, extracted, overwriteFiles: true), cancellationToken)
                    .ConfigureAwait(false);

                var packageRoot = FindPackageRoot(extracted)
                                  ?? throw new InvalidOperationException("下载的压缩包里没有 vnavmesh.dll 与 vnavmesh.json");

                await ReplaceSlotAsync(flavor, package.Version, packageRoot, pluginRoot, log, cancellationToken)
                    .ConfigureAwait(false);
                messages.Add($"{label} {current?.ToString() ?? "（无）"} → {package.Version}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                log.Warning(ex, $"线上更新 {label} 失败");
                messages.Add($"{label} 更新失败：{ex.Message}");
            }
            finally
            {
                // 压缩包与解压产物都在这里，更新完一并删掉。
                TryDeleteDirectory(work);
            }
        }

        return string.Join("；", messages);
    }

    /// <summary>把准备好的文件换进槽位；如果槽位里那一版正在运行，先卸载、换完再加载回来。</summary>
    private static async Task ReplaceSlotAsync(
        VnavmeshFlavor flavor,
        Version version,
        string sourceDirectory,
        string pluginRoot,
        IPluginLog log,
        CancellationToken cancellationToken)
    {
        var target = SlotDirectory(pluginRoot, flavor);
        var label = VnavmeshSwitcher.FlavorLabel(flavor);

        var slotEntries = SnapshotSlot(target);
        var wasLoaded = slotEntries.Any(x => x.IsLoaded);

        foreach (var entry in slotEntries.Where(x => x.IsLoaded || x.IsBusy))
        {
            log.Info($"更新{label}前先卸载正在运行的 vnavmesh {entry.Version}");
            await DalamudInternal.UnloadAsync(entry.Instance, UnloadTimeout, cancellationToken).ConfigureAwait(false);
        }

        if (slotEntries.Count > 0)
        {
            if (!await WaitUntilAsync(
                        () => SnapshotSlot(target).All(x => x.IsFullyClosed && x.StateName != "UnloadError"),
                        UnloadTimeout,
                        cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException($"{label} 未能完全卸载，文件可能仍被占用");
            }
        }

        log.Info($"写入 {label} {version}：{sourceDirectory} -> {target}");
        await Task.Run(
            () =>
            {
                ClearDirectory(target);
                CopyDirectory(sourceDirectory, target);
            },
            cancellationToken).ConfigureAwait(false);

        var dllPath = Path.Combine(target, "vnavmesh.dll");
        DalamudInternal.RegisterDevPluginLocation(dllPath, flavor == VnavmeshFlavor.China ? "vnavmesh CN" : "vnavmesh Global");
        DalamudInternal.QueueConfigSave();
        await DalamudInternal.ScanDevPluginsAsync().ConfigureAwait(false);

        if (!wasLoaded)
        {
            return;
        }

        var fresh = SnapshotSlot(target).FirstOrDefault(x => x.IsFullyClosed && !x.IsBusy);
        if (fresh is null)
        {
            log.Warning($"{label} 更新完成，但没有找到可以重新加载的条目，请手动开启一次。");
            return;
        }

        await DalamudInternal.LoadAsync(fresh.Instance, LoadTimeout, cancellationToken).ConfigureAwait(false);
    }

    private static List<VnavmeshEntry> SnapshotSlot(string slotDirectory)
    {
        var prefix = slotDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        return DalamudInternal.Snapshot()
            .Where(x => !string.IsNullOrEmpty(x.DllPath)
                        && x.DllPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static VnavmeshPackage? ParseRepository(string json, VnavmeshFlavor flavor, string source, IPluginLog log)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var array = root;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (!root.TryGetProperty("Plugins", out array)) return null;
            }

            if (array.ValueKind != JsonValueKind.Array) return null;

            VnavmeshPackage? best = null;
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                var internalName = GetString(item, "InternalName");
                if (!string.Equals(internalName, DalamudInternal.VnavmeshInternalName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var versionText = GetString(item, "AssemblyVersion") ?? GetString(item, "Version");
                if (!Version.TryParse(versionText, out var version)) continue;

                var link = GetString(item, "DownloadLinkInstall") ?? GetString(item, "DownloadLink");
                if (string.IsNullOrEmpty(link)) continue;

                if (best is null || version > best.Version)
                {
                    best = new VnavmeshPackage
                    {
                        Flavor = flavor,
                        Version = version,
                        DownloadLink = link,
                        Source = source,
                    };
                }
            }

            return best;
        }
        catch (Exception ex)
        {
            log.Warning(ex, $"解析插件库 JSON 失败：{source}");
            return null;
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? FindPackageRoot(string extractedRoot)
    {
        foreach (var dll in Directory.EnumerateFiles(extractedRoot, "vnavmesh.dll", SearchOption.AllDirectories))
        {
            var directory = Path.GetDirectoryName(dll);
            if (directory is null) continue;
            if (File.Exists(Path.Combine(directory, "vnavmesh.json"))) return directory;
        }

        return null;
    }

    private static IEnumerable<string> EnumerateInstallRoots()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(appData)) yield break;

        yield return Path.Combine(appData, "XIVLauncherCN", "installedPlugins", DalamudInternal.VnavmeshInternalName);
        yield return Path.Combine(appData, "XIVLauncher", "installedPlugins", DalamudInternal.VnavmeshInternalName);
    }

    private static (Version Version, string Name, string Author)? ReadManifest(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        if (root.TryGetProperty("InternalName", out var internalName) &&
            !string.Equals(internalName.GetString(), DalamudInternal.VnavmeshInternalName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var versionText = root.TryGetProperty("AssemblyVersion", out var assemblyVersion) ? assemblyVersion.GetString()
            : root.TryGetProperty("Version", out var versionProperty) ? versionProperty.GetString()
            : null;

        if (!Version.TryParse(versionText, out var version)) version = new Version(0, 0);

        var name = root.TryGetProperty("Name", out var nameProperty) ? nameProperty.GetString() ?? string.Empty : string.Empty;
        var author = root.TryGetProperty("Author", out var authorProperty) ? authorProperty.GetString() ?? string.Empty : string.Empty;

        return (version, name, author);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    private static async Task<string> GetStringAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            return await Http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            foreach (var proxy in FallbackProxies)
            {
                try
                {
                    using var handler = new HttpClientHandler { Proxy = new WebProxy(proxy), UseProxy = true };
                    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
                    return await client.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // 换下一个本地代理端口再试。
                }
            }

            throw;
        }
    }

    private static async Task DownloadFileAsync(string url, string targetPath, CancellationToken cancellationToken)
    {
        var bytes = await GetBytesAsync(url, cancellationToken).ConfigureAwait(false);
        await File.WriteAllBytesAsync(targetPath, bytes, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> GetBytesAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            return await Http.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            foreach (var proxy in FallbackProxies)
            {
                try
                {
                    using var handler = new HttpClientHandler { Proxy = new WebProxy(proxy), UseProxy = true };
                    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
                    return await client.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // 换下一个本地代理端口再试。
                }
            }

            throw;
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (condition()) return true;
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        return condition();
    }

    private static string CreateStagingDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "vnavmesh-tool", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void ClearDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
            return;
        }

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    File.Delete(file);
                }

                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    Directory.Delete(child, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 5)
            {
                Thread.Sleep(300);
            }
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // 临时目录删不掉不影响主流程，交给系统清理。
        }
    }
}
