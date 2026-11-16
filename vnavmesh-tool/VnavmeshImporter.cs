using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace VnavmeshTool;

/// <summary>已安装插件目录里发现的一份 vnavmesh 版本。</summary>
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

/// <summary>
/// 把 installedPlugins 下的 vnavmesh 版本目录复制成开发插件，以便两版在同一进程内共存。
/// </summary>
internal static class VnavmeshImporter
{
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

    internal static async Task<string?> ImportAsync(
        InstalledVnavmeshVersion version,
        string pluginRoot,
        IPluginLog log,
        CancellationToken cancellationToken)
    {
        var targetDirectory = Path.Combine(pluginRoot, "Versions", $"{Tag(version.Flavor)}-{version.Version}");
        var targetDll = Path.Combine(targetDirectory, "vnavmesh.dll");

        if (!File.Exists(targetDll))
        {
            log.Info($"复制 vnavmesh {version.Version}（{version.FlavorName}）：{version.SourceDirectory} -> {targetDirectory}");
            await Task.Run(() => CopyDirectory(version.SourceDirectory, targetDirectory), cancellationToken).ConfigureAwait(false);
        }

        return File.Exists(targetDll) ? targetDll : null;
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

    private static string Tag(VnavmeshFlavor flavor) => flavor == VnavmeshFlavor.China ? "cn" : "global";
}
