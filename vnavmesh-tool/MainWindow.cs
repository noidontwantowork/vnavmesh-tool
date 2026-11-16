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

        if (ImGui.CollapsingHeader("环境检查 / 导入版本"))
        {
            ImGui.TextWrapped(
                "同一个 InternalName 在 Dalamud 里只能存在一个「已安装」条目，所以两个版本要以开发插件（dev plugin）的形式共存。"
                + "点下面的按钮会把 installedPlugins\\vnavmesh 下发现的全部版本复制到本插件目录的 Versions 子目录，并登记为开发插件加载位置；"
                + "已经存在的版本不会重复复制。");

            ImGui.Spacing();

            if (busy) ImGui.BeginDisabled();
            if (ImGui.Button("扫描并导入 vnavmesh 版本", new Vector2(ImGui.GetContentRegionAvail().X, 0f)))
            {
                this.switcher.RequestImport();
            }

            if (busy) ImGui.EndDisabled();

            ImGui.Spacing();
            ImGui.TextWrapped("提示：导入后请再执行一次切换，让插件重新扫描并接管两个版本的开关状态。");
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
            return;
        }

        foreach (var entry in entries)
        {
            var color = IsErrorState(entry.StateName) ? ErrorColor
                : entry.IsLoaded ? LoadedColor
                : entry.IsBusy ? PendingColor
                : IdleColor;

            ImGui.PushStyleColor(ImGuiCol.Text, color);
            ImGui.TextUnformatted(
                $"{entry.FlavorName}  {entry.Version}  ({DescribeState(entry.StateName)})"
                + (entry.IsDev ? "  [dev]" : string.Empty));
            ImGui.PopStyleColor();
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
    }
}
