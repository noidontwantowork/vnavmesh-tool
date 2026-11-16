using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace VnavmeshTool;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandToggle = "/vnavmeshswitch";
    private const string CommandGlobal = "/vnavmeshglobal";
    private const string CommandChina = "/vnavmeshchina";
    private const string CommandWindow = "/vnavmeshtool";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly WindowSystem windowSystem = new("vnavmesh-tool");
    private readonly VnavmeshSwitcher switcher;
    private readonly MainWindow mainWindow;

    public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commandManager, IPluginLog log)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;

        this.switcher = new VnavmeshSwitcher(pluginInterface, log);
        this.mainWindow = new MainWindow(this.switcher);

        this.windowSystem.AddWindow(this.mainWindow);

        pluginInterface.UiBuilder.Draw += this.windowSystem.Draw;
        pluginInterface.UiBuilder.OpenConfigUi += this.ToggleMainWindow;

        commandManager.AddHandler(CommandToggle, new CommandInfo((_, _) => this.switcher.RequestToggle())
        {
            HelpMessage = "检测当前运行的 vnavmesh，关闭它并开启另一个版本。",
        });

        commandManager.AddHandler(CommandGlobal, new CommandInfo((_, _) => this.switcher.RequestEnable(VnavmeshFlavor.Global))
        {
            HelpMessage = "关闭中国服版 vnavmesh 后开启国际服版 vnavmesh。",
        });

        commandManager.AddHandler(CommandChina, new CommandInfo((_, _) => this.switcher.RequestEnable(VnavmeshFlavor.China))
        {
            HelpMessage = "关闭国际服版 vnavmesh 后开启中国服版 vnavmesh。",
        });

        commandManager.AddHandler(CommandWindow, new CommandInfo((_, _) => this.mainWindow.Toggle())
        {
            HelpMessage = "打开 vnavmesh-tool 窗口。",
        });
    }

    public void Dispose()
    {
        this.pluginInterface.UiBuilder.Draw -= this.windowSystem.Draw;
        this.pluginInterface.UiBuilder.OpenConfigUi -= this.ToggleMainWindow;

        this.commandManager.RemoveHandler(CommandToggle);
        this.commandManager.RemoveHandler(CommandGlobal);
        this.commandManager.RemoveHandler(CommandChina);
        this.commandManager.RemoveHandler(CommandWindow);

        this.windowSystem.RemoveAllWindows();
        this.switcher.Dispose();
    }

    private void ToggleMainWindow() => this.mainWindow.Toggle();
}
