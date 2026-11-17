using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace VnavmeshTool;

/// <summary>vnavmesh 的发行分支。</summary>
internal enum VnavmeshFlavor
{
    /// <summary>国际服版（awgil/ffxiv_navmesh）。</summary>
    Global,

    /// <summary>中国服版（AtmoOmen/ffxiv_navmesh-cn）。</summary>
    China,
}

/// <summary>某一时刻某个 vnavmesh 插件条目的只读快照。</summary>
internal sealed class VnavmeshEntry
{
    public required object Instance { get; init; }

    public required string Name { get; init; }

    public required string InternalName { get; init; }

    public required string Author { get; init; }

    public required Version Version { get; init; }

    public required string StateName { get; init; }

    public required string DllPath { get; init; }

    public required Guid WorkingPluginId { get; init; }

    public required bool IsDev { get; init; }

    public required VnavmeshFlavor Flavor { get; init; }

    /// <summary>dll 文件已经不在磁盘上（多见于登记了旧目录、之后目录被改名或删除）。</summary>
    public required bool DllMissing { get; init; }

    public bool IsLoaded => string.Equals(this.StateName, "Loaded", StringComparison.Ordinal);

    public bool IsBusy => this.StateName is "Loading" or "Unloading";

    /// <summary>真正没有占用程序集的状态：已卸载，或加载失败（同样没有活着的实例）。</summary>
    public bool IsFullyClosed => this.StateName is "Unloaded" or "LoadError" or "DependencyResolutionFailed";

    /// <summary>路径有效（可以真正被加载）的条目；失效条目只剩「已加载时仍需卸载」这一种用途。</summary>
    public bool IsUsable => !this.DllMissing;

    public string FlavorName => this.Flavor == VnavmeshFlavor.China ? "中国服版" : "国际服版";
}

/// <summary>
/// 通过反射访问 Dalamud 的内部插件管理器。所有方法都应在后台线程调用。
/// </summary>
internal static class DalamudInternal
{
    internal const string VnavmeshInternalName = "vnavmesh";

    private static readonly Assembly DalamudAssembly = typeof(IDalamudPluginInterface).Assembly;

    private static Type? pluginManagerType;
    private static Type? localPluginType;
    private static Type? localDevPluginType;
    private static Type? profileManagerType;
    private static Type? configurationType;
    private static Type? devLocationType;
    private static Type? disposalModeType;

    private static readonly Dictionary<string, (long Length, DateTime WriteTime, Version Version)> DiskVersionCache = [];

    internal static Type PluginManagerType
    {
        get
        {
            pluginManagerType ??= FindType("Dalamud.Plugin.Internal.PluginManager");
            return pluginManagerType!;
        }
    }

    internal static Type LocalPluginType
    {
        get
        {
            localPluginType ??= FindType("Dalamud.Plugin.Internal.Types.LocalPlugin");
            return localPluginType!;
        }
    }

    internal static Type LocalDevPluginType
    {
        get
        {
            localDevPluginType ??= FindType("Dalamud.Plugin.Internal.Types.LocalDevPlugin");
            return localDevPluginType!;
        }
    }

    internal static Type ProfileManagerType
    {
        get
        {
            profileManagerType ??= FindType("Dalamud.Plugin.Internal.Profiles.ProfileManager");
            return profileManagerType!;
        }
    }

    internal static Type ConfigurationType
    {
        get
        {
            configurationType ??= FindType("Dalamud.Configuration.Internal.DalamudConfiguration");
            return configurationType!;
        }
    }

    internal static Type DevLocationType
    {
        get
        {
            devLocationType ??= FindType("Dalamud.Configuration.DevPluginLocationSettings");
            return devLocationType!;
        }
    }

    internal static Type DisposalModeType
    {
        get
        {
            disposalModeType ??= FindType("Dalamud.Plugin.Internal.Types.PluginLoaderDisposalMode");
            return disposalModeType!;
        }
    }

    /// <summary>按 Author / Name 判定条目属于哪个发行分支。</summary>
    internal static VnavmeshFlavor DetectFlavor(string name, string author)
    {
        if (author.Contains("AtmoOmen", StringComparison.OrdinalIgnoreCase)) return VnavmeshFlavor.China;
        if (name.Contains("CN", StringComparison.OrdinalIgnoreCase)) return VnavmeshFlavor.China;
        if (name.Contains("国服", StringComparison.Ordinal)) return VnavmeshFlavor.China;
        return VnavmeshFlavor.Global;
    }

    internal static object? ReadProp(object target, string name)
    {
        var type = target.GetType();
        var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (prop is not null) return prop.GetValue(target);

        foreach (var iface in type.GetInterfaces())
        {
            prop = iface.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (prop is not null) return prop.GetValue(target);
        }

        return null;
    }

    internal static object GetService(Type serviceType)
    {
        var open = FindType("Dalamud.Service`1");
        var closed = open.MakeGenericType(serviceType);
        var get = closed.GetMethod("Get", BindingFlags.Public | BindingFlags.Static)
                  ?? throw new InvalidOperationException($"Service<{serviceType.Name}>.Get 未找到");

        return get.Invoke(null, null)
               ?? throw new InvalidOperationException($"Service<{serviceType.Name}>.Get() 返回了 null");
    }

    /// <summary>枚举当前所有 vnavmesh 插件条目（两版都返回）。</summary>
    internal static IReadOnlyList<VnavmeshEntry> Snapshot()
    {
        var manager = GetService(PluginManagerType);
        var prop = PluginManagerType.GetProperty("InstalledPlugins", BindingFlags.Public | BindingFlags.Instance)
                   ?? throw new InvalidOperationException("PluginManager.InstalledPlugins 未找到");

        if (prop.GetValue(manager) is not IEnumerable installed) return [];

        var result = new List<VnavmeshEntry>();
        foreach (var item in installed)
        {
            if (item is null) continue;

            if (ReadProp(item, "InternalName") is not string internalName) continue;
            if (!string.Equals(internalName, VnavmeshInternalName, StringComparison.OrdinalIgnoreCase)) continue;

            var manifest = ReadProp(item, "Manifest");
            var name = ReadProp(item, "Name") as string
                       ?? (manifest is null ? string.Empty : ReadProp(manifest, "Name") as string ?? string.Empty);
            var author = manifest is null ? string.Empty : ReadProp(manifest, "Author") as string ?? string.Empty;
            var version = ReadProp(item, "EffectiveVersion") as Version ?? new Version(0, 0);
            var state = ReadProp(item, "State")?.ToString() ?? "Unknown";
            var dllFile = ReadProp(item, "DllFile") as FileInfo;
            var dllPath = dllFile?.FullName ?? string.Empty;
            var workingId = ReadProp(item, "EffectiveWorkingPluginId") is Guid guid ? guid : Guid.Empty;
            var isDev = ReadProp(item, "IsDev") is bool dev && dev;

            result.Add(new VnavmeshEntry
            {
                Instance = item,
                Name = name,
                InternalName = internalName,
                Author = author,
                Version = ResolveVersion(dllFile?.FullName, version),
                StateName = state,
                DllPath = dllPath,
                WorkingPluginId = workingId,
                IsDev = isDev,
                Flavor = DetectFlavor(name, author),
                DllMissing = dllPath.Length == 0 || !File.Exists(dllPath),
            });
        }

        return result;
    }

    /// <summary>优先读磁盘上的 manifest 版本，避免插件更新后界面一直显示缓存的旧版本号。</summary>
    internal static Version ResolveVersion(string? dllPath, Version fallback)
    {
        if (string.IsNullOrEmpty(dllPath)) return fallback;

        var directory = Path.GetDirectoryName(dllPath);
        if (string.IsNullOrEmpty(directory)) return fallback;

        var manifestPath = Path.Combine(directory, "vnavmesh.json");

        try
        {
            var info = new FileInfo(manifestPath);
            if (!info.Exists) return fallback;

            lock (DiskVersionCache)
            {
                if (DiskVersionCache.TryGetValue(manifestPath, out var cached)
                    && cached.Length == info.Length
                    && cached.WriteTime == info.LastWriteTimeUtc)
                {
                    return cached.Version;
                }
            }

            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = document.RootElement;
            var text = root.TryGetProperty("AssemblyVersion", out var assemblyVersion) ? assemblyVersion.GetString()
                : root.TryGetProperty("Version", out var versionProperty) ? versionProperty.GetString()
                : null;

            if (!Version.TryParse(text, out var version)) version = fallback;

            lock (DiskVersionCache)
            {
                DiskVersionCache[manifestPath] = (info.Length, info.LastWriteTimeUtc, version);
            }

            return version;
        }
        catch
        {
            return fallback;
        }
    }

    internal static async Task LoadAsync(object plugin, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var method = LocalPluginType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                         .FirstOrDefault(m => m.Name == "LoadAsync" && m.GetParameters().Length == 3)
                     ?? throw new InvalidOperationException("LocalPlugin.LoadAsync 签名不匹配");

        var args = new object?[] { PluginLoadReason.Installer, false, cancellationToken };
        var task = Invoke<Task>(method, plugin, args, "LoadAsync");
        await task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task UnloadAsync(object plugin, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var method = LocalPluginType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                         .FirstOrDefault(m => m.Name == "UnloadAsync" && m.GetParameters().Length == 1)
                     ?? throw new InvalidOperationException("LocalPlugin.UnloadAsync 签名不匹配");

        var mode = Enum.Parse(DisposalModeType, "WaitBeforeDispose");
        var args = new object?[] { mode };
        var task = Invoke<Task>(method, plugin, args, "UnloadAsync");
        await task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
    }

    internal static void SetStartOnBoot(object plugin, bool value)
    {
        if (!LocalDevPluginType.IsInstanceOfType(plugin)) return;

        var prop = LocalDevPluginType.GetProperty("StartOnBoot", BindingFlags.Public | BindingFlags.Instance);
        if (prop is null || !prop.CanWrite) return;

        prop.SetValue(plugin, value);
    }

    internal static async Task SetProfileWantedAsync(Guid workingPluginId, string internalName, bool wanted)
    {
        if (workingPluginId == Guid.Empty) return;

        var manager = GetService(ProfileManagerType);
        var defaultProfile = ProfileManagerType.GetProperty("DefaultProfile", BindingFlags.Public | BindingFlags.Instance)?.GetValue(manager)
                             ?? throw new InvalidOperationException("ProfileManager.DefaultProfile 未找到");

        var method = defaultProfile.GetType().GetMethod(
            "AddOrUpdateAsync",
            BindingFlags.Public | BindingFlags.Instance,
            null,
            [typeof(Guid), typeof(string), typeof(bool), typeof(bool)],
            null);

        if (method is null) throw new InvalidOperationException("Profile.AddOrUpdateAsync 未找到");

        var task = Invoke<Task>(method, defaultProfile, [workingPluginId, internalName, wanted, false], "AddOrUpdateAsync");
        await task.ConfigureAwait(false);
    }

    internal static void QueueConfigSave()
    {
        var config = GetService(ConfigurationType);
        ConfigurationType.GetMethod("QueueSave", BindingFlags.Public | BindingFlags.Instance)?.Invoke(config, null);
    }

    /// <summary>把一份 dll 路径登记为开发插件加载位置；已登记但被停用的条目会被重新启用。</summary>
    internal static bool RegisterDevPluginLocation(string dllPath, string nickname)
    {
        var config = GetService(ConfigurationType);
        if (ConfigurationType.GetProperty("DevPluginLoadLocations", BindingFlags.Public | BindingFlags.Instance)?.GetValue(config) is not IList list)
            throw new InvalidOperationException("DalamudConfiguration.DevPluginLoadLocations 未找到");

        foreach (var existing in list)
        {
            if (existing is null) continue;
            if (ReadProp(existing, "Path") is not string path || !string.Equals(path, dllPath, StringComparison.OrdinalIgnoreCase))
                continue;

            var enabledProp = DevLocationType.GetProperty("IsEnabled");
            var nicknameProp = DevLocationType.GetProperty("Nickname");
            var changed = false;

            if (enabledProp is not null && enabledProp.GetValue(existing) is false)
            {
                enabledProp.SetValue(existing, true);
                changed = true;
            }

            if (nicknameProp is not null && nicknameProp.GetValue(existing) is null or "")
            {
                nicknameProp.SetValue(existing, nickname);
                changed = true;
            }

            return changed;
        }

        var settings = Activator.CreateInstance(DevLocationType, nonPublic: true)
                       ?? throw new InvalidOperationException("无法创建 DevPluginLocationSettings");

        DevLocationType.GetProperty("Path")!.SetValue(settings, dllPath);
        DevLocationType.GetProperty("IsEnabled")!.SetValue(settings, true);
        DevLocationType.GetProperty("Nickname")?.SetValue(settings, nickname);
        list.Add(settings);
        return true;
    }

    /// <summary>移除指向已不存在文件的开发插件加载位置（只处理本插件 Versions 目录下的条目）。</summary>
    internal static int RemoveStaleDevLocations(string versionsRoot)
    {
        var config = GetService(ConfigurationType);
        if (ConfigurationType.GetProperty("DevPluginLoadLocations", BindingFlags.Public | BindingFlags.Instance)?.GetValue(config) is not IList list)
            return 0;

        var removed = 0;
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (list[i] is not { } item) continue;
            if (ReadProp(item, "Path") is not string path) continue;
            if (!path.StartsWith(versionsRoot, StringComparison.OrdinalIgnoreCase)) continue;
            if (File.Exists(path)) continue;

            list.RemoveAt(i);
            removed++;
        }

        return removed;
    }

    internal static async Task ScanDevPluginsAsync()
    {
        var manager = GetService(PluginManagerType);
        var method = PluginManagerType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                         .FirstOrDefault(m => m.Name == "ScanDevPluginsAsync")
                     ?? throw new InvalidOperationException("PluginManager.ScanDevPluginsAsync 未找到");

        var args = method.GetParameters()
            .Select(p => p.HasDefaultValue
                ? p.DefaultValue
                : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)
            .ToArray();

        if (method.Invoke(manager, args) is Task task) await task.ConfigureAwait(false);
    }

    private static T Invoke<T>(MethodInfo method, object target, object?[] args, string label)
        where T : class
    {
        try
        {
            return method.Invoke(target, args) as T
                   ?? throw new InvalidOperationException($"{label} 返回了非预期结果");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static Type FindType(string fullName) =>
        DalamudAssembly.GetType(fullName, throwOnError: false)
        ?? throw new InvalidOperationException($"未能定位 Dalamud 内部类型：{fullName}");
}
