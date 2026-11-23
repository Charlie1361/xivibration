using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using XIVibration.Windows;

namespace XIVibration;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/xivib";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;
    [PluginService] internal static IGameConfig GameConfig { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IChatGui Chat { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    public Configuration Config { get; }
    public VibrationEngine Engine { get; }
    public ActionWatcher Watcher { get; }
    public ActionCatalog Catalog { get; } = new();

    private readonly WindowSystem windows = new("XIVibration");
    private readonly MainWindow mainWindow;

    public Plugin()
    {
        this.Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        this.Engine = new VibrationEngine(this.Config);
        this.Watcher = new ActionWatcher();
        this.Watcher.LocalActionUsed += this.OnLocalActionUsed;

        this.mainWindow = new MainWindow(this);
        this.windows.AddWindow(this.mainWindow);

        CommandManager.AddHandler(Command, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "Open XIVibration. Also: /xivib on | off | toggle | test | stop",
        });

        PluginInterface.UiBuilder.Draw += this.windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += this.mainWindow.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi += this.mainWindow.Toggle;
        Framework.Update += this.OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(IFramework framework) => this.Engine.Update();

    private void OnLocalActionUsed(uint actionId)
    {
        if (!this.Config.Enabled)
            return;
        if (this.Config.OnlyInCombat && !Condition[ConditionFlag.InCombat])
            return;

        ActionRule? rule = null;
        if (this.Config.Rules.TryGetValue(actionId, out var r) && r.Enabled)
            rule = r;
        else if (this.Config.UseFallback && !this.Config.Rules.ContainsKey(actionId))
            rule = this.Config.Fallback;

        if (rule != null)
            this.Engine.Play(rule.Pulses, rule.Strength * this.Config.MasterStrength);
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "on":
                this.SetEnabled(true);
                break;
            case "off":
                this.SetEnabled(false);
                break;
            case "toggle":
                this.SetEnabled(!this.Config.Enabled);
                break;
            case "test":
                this.Engine.Play(Presets.Copy(Presets.All[1]), this.Config.MasterStrength, ignoreGates: true);
                break;
            case "stop":
                this.Engine.StopAll();
                break;
            default:
                this.mainWindow.Toggle();
                break;
        }
    }

    private void SetEnabled(bool value)
    {
        this.Config.Enabled = value;
        this.Config.Save();
        Chat.Print($"[XIVibration] {(value ? "Enabled" : "Disabled")}");
    }

    public void Dispose()
    {
        Framework.Update -= this.OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= this.windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= this.mainWindow.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi -= this.mainWindow.Toggle;
        CommandManager.RemoveHandler(Command);

        this.Watcher.LocalActionUsed -= this.OnLocalActionUsed;
        this.Watcher.Dispose();
        this.Engine.Dispose();
        this.windows.RemoveAllWindows();
        this.mainWindow.Dispose();
    }
}
