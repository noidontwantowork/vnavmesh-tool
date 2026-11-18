using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace VnavmeshTool;

internal sealed class MainWindow : Window
{
    private static readonly Vector4 LoadedColor = new(0.40f, 0.95f, 0.45f, 1f);
    private static readonly Vector4 PendingColor = new(1.00f, 0.80f, 0.25f, 1f);
    private static readonly Vector4 IdleColor = new(0.62f, 0.62f, 0.62f, 1f);
    private static readonly Vector4 ErrorColor = new(0.95f, 0.38f, 0.38f, 1f);
    private static readonly Vector4 NormalColor = new(0.85f, 0.85f, 0.85f, 1f);

    private readonly VnavmeshSwitcher switcher;

    public MainWindow(VnavmeshSwitcher switcher)
        : base("vnavmesh-tool###VnavmeshToolMainWindow")
    {
        this.switcher = switcher;
        this.Size = new Vector2(520f, 430f);
        this.SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        this.switcher.RefreshIfDue();

        var entries = this.switcher.Entries;
        var busy = this.switcher.IsBusy;

        ImGui.TextWrapped("在同一游戏进程内切换国际服版 / 中国服版 vnavmesh。两版命名空间相同，任何时刻只会有一个处于加载状态。");
        ImGui.Spacing();

        this.DrawStateSection(entries);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (busy) ImGui.BeginDisabled();

        var available = ImGui.GetContentRegionAvail().X;

        if (ImGui.Button("切换国际服版和中国服版", new Vector2(available, 0f)))
        {
            this.switcher.RequestToggle();
        }

        var halfWidth = (available - 8f) * 0.5f;
        if (ImGui.Button("开启国际服版", new Vector2(halfWidth, 0f)))
        {
            this.switcher.RequestEnable(VnavmeshFlavor.Global);
        }

        ImGui.SameLine();
        if (ImGui.Button("开启中国服版", new Vector2(halfWidth, 0f)))
        {
            this.switcher.RequestEnable(VnavmeshFlavor.China);
        }

        if (busy) ImGui.EndDisabled();

        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Text, this.switcher.StatusIsError ? ErrorColor : NormalColor);
        ImGui.TextWrapped(this.switcher.StatusText);
        ImGui.PopStyleColor();

        ImGui.Spacing();
        ImGui.Separator();

        if (ImGui.CollapsingHeader("宏命令（可直接写进游戏宏）"))
        {
            ImGui.TextUnformatted("/vnavmeshswitch  切换国际服版 / 中国服版");
            ImGui.TextUnformatted("/vnavmeshglobal  关闭中国服版，开启国际服版");
            ImGui.TextUnformatted("/vnavmeshchina   关闭国际服版，开启中国服版");
            ImGui.TextUnformatted("/vnavmeshtool    打开本窗口");
        }

        ImGui.Separator();

        if (ImGui.CollapsingHeader("导入 / 更新 vnavmesh"))
        {
            ImGui.TextWrapped(
                "实际使用的双版本 vnavmesh 将以开发版插件（dev plugin）的形式共存。"
                + "点击“扫描并导入新版本 vnavmesh”会把当前从线上插件库安装的新版本 vnavmesh 复制到本插件的 Versions 子目录；"
                + "点击“直接线上更新 vnavmesh”则由本插件自动下载并更新 vnavmesh。");

            ImGui.Spacing();

            if (busy) ImGui.BeginDisabled();

            if (ImGui.Button("扫描并导入新版本 vnavmesh", new Vector2(available, 0f)))
            {
                this.switcher.RequestSyncFromInstalled();
            }

            if (ImGui.Button("直接线上更新 vnavmesh", new Vector2(available, 0f)))
            {
                this.switcher.RequestOnlineUpdate();
            }

            if (busy) ImGui.EndDisabled();

            ImGui.Spacing();
            ImGui.TextWrapped("导入或更新后建议执行任意一种切换，让插件重新接管 vnavmesh 的开关状态。");
        }

        ImGui.Separator();

        if (ImGui.CollapsingHeader("双版本配置共存（减少重建缓存频率）"))
        {
            ImGui.TextWrapped("开启本功能将额外占用一定硬盘空间，请确保你的 XIVLauncher 所在的硬盘分区空间充足。");

            ImGui.Spacing();

            var isolation = this.switcher.ProfileIsolationEnabled;
            if (ImGui.Checkbox("自动调整 vnavmesh 配置与缓存", ref isolation))
            {
                this.switcher.SetProfileIsolation(isolation);
            }

            ImGui.TextWrapped(
                "配置存储位置：pluginConfigs\\vnavmesh-tool-store\\Global 与 \\CN。"
                + "首次切换会智能识别现有配置文件；此后每个版本各自保留自己的 meshcache / worker。");
        }
    }

    private static string DescribeState(string state) => state switch
    {
        "Loaded" => "已加载",
        "Loading" => "加载中",
        "Unloaded" => "已关闭",
        "Unloading" => "关闭中",
        "LoadError" => "加载失败",
        "UnloadError" => "卸载失败",
        "DependencyResolutionFailed" => "依赖解析失败",
        _ => state,
    };

    private static bool IsErrorState(string state) =>
        state is "LoadError" or "UnloadError" or "DependencyResolutionFailed";

    private void DrawStateSection(IReadOnlyList<VnavmeshEntry> entries)
    {
        ImGui.TextUnformatted("当前状态");
        ImGui.Separator();

        if (entries.Count == 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, ErrorColor);
            ImGui.TextUnformatted("未检测到任何 vnavmesh 插件条目。");
            ImGui.PopStyleColor();
            ImGui.Spacing();
            ImGui.TextWrapped(
                "请确认本插件目录下存在 Versions\\Global 与 Versions\\CN；"
                + "点击任意一种切换会自动把这两个槽位登记为开发插件并重新扫描。");
            return;
        }

        foreach (var entry in entries)
        {
            var stateText = entry.DllMissing ? "路径失效，已忽略" : DescribeState(entry.StateName);
            var color = entry.DllMissing ? PendingColor
                : IsErrorState(entry.StateName) ? ErrorColor
                : entry.IsLoaded ? LoadedColor
                : entry.IsBusy ? PendingColor
                : IdleColor;

            ImGui.PushStyleColor(ImGuiCol.Text, color);
            ImGui.TextUnformatted(
                $"{entry.FlavorName}  {entry.Version}  ({stateText})"
                + (entry.IsDev ? "  [dev]" : string.Empty));
            ImGui.PopStyleColor();
        }

        if (entries.Any(x => x.DllMissing))
        {
            ImGui.TextWrapped("标有“路径失效”的条目来自已改名或删除的旧目录，切换时会自动从开发插件列表中清理。");
        }

        ImGui.Spacing();

        var loaded = entries.Where(x => x.IsLoaded).ToList();
        string summary;
        if (loaded.Count > 1)
        {
            summary = "警告：同时有多个 vnavmesh 处于已加载状态，命名空间冲突已经发生，请立刻关闭多余版本。";
        }
        else if (loaded.Count == 1)
        {
            summary = $"当前生效：{loaded[0].FlavorName} {loaded[0].Version}。";
        }
        else if (entries.Any(x => x.IsBusy))
        {
            summary = "有版本处于加载 / 卸载过程中，请稍候。";
        }
        else
        {
            summary = "当前没有版本处于加载状态。";
        }

        ImGui.TextWrapped(summary);

        ImGui.PushStyleColor(ImGuiCol.Text, IdleColor);
        ImGui.TextWrapped(this.switcher.ProfileSummary);
        ImGui.PopStyleColor();
    }
}
