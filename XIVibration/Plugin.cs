using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using XIVibration.Timeline;
using XIVibration.Windows;

namespace XIVibration;

public enum SkillMode
{
    /// <summary>The user's own pattern.</summary>
    Custom,

    /// <summary>Pattern made from the animation's hits.</summary>
    Auto,

    /// <summary>The animation already vibrates; left alone.</summary>
    BuiltIn,

    /// <summary>Switched off.</summary>
    Off,
}

/// <summary>How a pattern's start was timed.</summary>
public sealed record StartInfo(string Source, long GapMs, float Speed);

/// <summary>Everything decided for one skill: what plays, when, and why.</summary>
public sealed record SkillPlan(
    uint ActionId,
    SkillMode Mode,
    ActionTimelineInfo Timeline,
    AutoResult Auto,
    ActionRule? Rule,
    List<VibrationPulse> Pulses,
    float Scale);

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/xivib";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
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
    public AnimationTimelines Timelines { get; } = new();
    public MultiHitData MultiHit { get; }
    public SkillAnalysis Analysis { get; }
    public AnimationSync Sync { get; }

    /// <summary>How the last pattern's start was timed.</summary>
    public StartInfo? LastStart { get; private set; }

    /// <summary>How long to wait for the animation after the server message before falling back.</summary>
    private const long PendingWaitMs = 150;

    private (SkillPlan Plan, long ReceivedMs, HashSet<uint> TimelineIds)? pending;

    /// <summary>The last skill you used and what was played for it.</summary>
    public SkillPlan? LastPlan { get; private set; }

    public DateTime LastPlanTime { get; private set; }

    private readonly WindowSystem windows = new("XIVibration");
    private readonly MainWindow mainWindow;

    public Plugin()
    {
        this.Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        this.Config.Migrate();

        this.MultiHit = new MultiHitData(
            PluginInterface.ConfigFile.DirectoryName ?? PluginInterface.GetPluginConfigDirectory(),
            this.Timelines.TimelineOf);
        this.Engine = new VibrationEngine(this.Config);
        this.Watcher = new ActionWatcher();
        this.Watcher.LocalActionUsed += this.OnLocalActionUsed;
        this.Analysis = new SkillAnalysis(this);
        this.Sync = new AnimationSync();
        this.Sync.Played += this.OnAnimationPlayed;

        this.mainWindow = new MainWindow(this);
        this.windows.AddWindow(this.mainWindow);

        CommandManager.AddHandler(Command, new CommandInfo(this.OnCommand)
        {
            HelpMessage = "Open XIVibration. Also: /xivib on | off | toggle | test | stop | analyse",
        });

        PluginInterface.UiBuilder.Draw += this.windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi += this.mainWindow.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi += this.mainWindow.Toggle;
        Framework.Update += this.OnFrameworkUpdate;
        ClientState.TerritoryChanged += this.OnTerritoryChanged;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        this.Analysis.Update();

        // The animation never showed up: time the pattern from the server message.
        if (this.pending is { } p && Clock.NowMs - p.ReceivedMs > PendingWaitMs)
        {
            this.pending = null;
            this.Start(p.Plan, p.ReceivedMs, 1f, "server message (animation not seen)", 0);
        }

        this.Engine.Update();
    }

    // Mods can be switched while zoning; re-read animations on the next use.
    private void OnTerritoryChanged(uint territory) => this.Timelines.Clear();

    public HitAnalyzer.MultiHitTable? MultiHitFor(uint actionId)
        => this.Config.UseMultiHit && this.MultiHit.TryGet(actionId, out var t) ? t : null;

    private readonly Dictionary<(uint Action, uint Timeline), (int Key, AutoResult Result)> autoCache = new();

    /// <summary>The automatic pattern for a skill, from its current animation.</summary>
    public AutoResult BuildAuto(uint actionId, ActionTimelineInfo tl)
    {
        var s = this.Config.AutoStrength;
        var key = HashCode.Combine(
            this.Timelines.Generation,
            this.MultiHit.Version,
            this.Config.UseMultiHit,
            HashCode.Combine(s.Weaponskill, s.Spell, s.Ability, s.Support, s.LimitBreak),
            this.Config.Sound.Key);
        if (this.autoCache.TryGetValue((actionId, tl.TimelineId), out var c) && c.Key == key)
            return c.Result;

        var result = AutoPattern.Build(this.Catalog.Get(actionId), tl, this.MultiHitFor(actionId), this.Config);
        this.autoCache[(actionId, tl.TimelineId)] = (key, result);
        return result;
    }

    public SkillPlan PlanFor(uint actionId, uint playedTimeline = 0)
    {
        var tl = this.Timelines.ForAction(actionId);
        if (!tl.Found && playedTimeline != 0 && playedTimeline != tl.TimelineId)
        {
            var played = this.Timelines.ForTimeline(playedTimeline);
            if (played.Found)
                tl = played;
        }

        var auto = this.BuildAuto(actionId, tl);
        var keepBuiltIn = tl.HasBuiltInVibration && !this.Config.ReplaceModVibration;

        if (this.Config.Rules.TryGetValue(actionId, out var rule))
        {
            var firstHit = auto.Hits.Count > 0 ? (int)MathF.Round(auto.Hits[0].Frame * TmbParser.MsPerFrame) : 0;
            var offset = rule.Anchor == PulseAnchor.FirstHit ? firstHit : 0;
            var pulses = rule.Pulses.Select(p =>
            {
                var c = p.Clone();
                c.DelayMs += offset;
                return c;
            }).ToList();

            if (rule.Origin == RuleOrigin.User)
                return new SkillPlan(actionId, rule.Enabled ? SkillMode.Custom : SkillMode.Off, tl, auto, rule, pulses, rule.Strength);

            // A pattern made from the animation: follows the auto settings.
            var mode = !rule.Enabled || !this.Config.AutoEnabled ? SkillMode.Off
                : keepBuiltIn ? SkillMode.BuiltIn
                : SkillMode.Auto;
            return new SkillPlan(actionId, mode, tl, auto, rule, pulses, rule.Strength * auto.Scale);
        }

        // Not analysed yet: build it live.
        var liveMode = !this.Config.AutoEnabled || this.Config.AutoDisabled.Contains(actionId) ? SkillMode.Off
            : keepBuiltIn ? SkillMode.BuiltIn
            : SkillMode.Auto;
        return new SkillPlan(actionId, liveMode, tl, auto, null, auto.Pulses, auto.Scale);
    }

    private void OnLocalActionUsed(uint actionId, uint playedTimeline)
    {
        // My animation is running: the game's own vibration for it (built-in in a mod, limit breaks) may play.
        this.Engine.OpenLocalWindow(Clock.NowMs + (long)this.Timelines.ForAction(actionId).LengthMs + 500);

        if (!this.Config.Enabled)
            return;
        if (this.Config.OnlyInCombat && !Condition[ConditionFlag.InCombat])
            return;

        var plan = this.PlanFor(actionId, playedTimeline);
        this.LastPlan = plan;
        this.LastPlanTime = DateTime.Now;

        if (plan.Mode is not (SkillMode.Custom or SkillMode.Auto))
            return;

        // Time 0 is when the animation really starts on the character.
        var ids = new HashSet<uint> { plan.Timeline.TimelineId, playedTimeline, this.Timelines.TimelineOf(actionId) };
        ids.Remove(0);
        var now = Clock.NowMs;
        var started = this.Sync.FindRecent(ids, 1000);
        if (started is { } e)
        {
            this.pending = null;
            this.Start(plan, e.TimeMs, e.Speed, "animation start", now - e.TimeMs);
        }
        else if (!this.Sync.HookActive || ids.Count == 0)
        {
            this.Start(plan, now, 1f, "server message", 0);
        }
        else
        {
            this.pending = (plan, now, ids);
        }
    }

    private void OnAnimationPlayed(AnimationSync.PlayEvent e)
    {
        if (this.pending is not { } p || !p.TimelineIds.Contains(e.TimelineId))
            return;
        this.pending = null;
        this.Start(p.Plan, e.TimeMs, e.Speed, "animation start", p.ReceivedMs - e.TimeMs);
    }

    /// <param name="gapMs">How much earlier (+) or later (-) the animation started than the server message.</param>
    private void Start(SkillPlan plan, long startMs, float speed, string source, long gapMs)
    {
        var pulses = plan.Pulses;
        if (Math.Abs(speed - 1f) > 0.01f)
        {
            // The animation plays faster or slower than normal: so does its pattern.
            pulses = pulses.Select(p =>
            {
                var c = p.Clone();
                c.DelayMs = (int)MathF.Round(p.DelayMs / speed);
                c.DurationMs = Math.Max(16, (int)MathF.Round(p.DurationMs / speed));
                return c;
            }).ToList();
        }

        // Off by default. Only then does the plugin mute an animation's own vibration.
        if (this.Config.ReplaceModVibration && plan.Timeline.HasBuiltInVibration)
            this.Engine.SuppressGame(plan.Timeline.LengthMs / speed + 200);

        this.LastStart = new StartInfo(source, gapMs, speed);
        this.Engine.Play(pulses, plan.Scale * this.Config.MasterStrength, startMs: startMs);
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
            case "analyse":
            case "analyze":
                this.Analysis.ReanalyseAll();
                Chat.Print("[XIVibration] Re-analysing all skills. Your own patterns are kept.");
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
        ClientState.TerritoryChanged -= this.OnTerritoryChanged;
        PluginInterface.UiBuilder.Draw -= this.windows.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= this.mainWindow.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi -= this.mainWindow.Toggle;
        CommandManager.RemoveHandler(Command);

        this.Watcher.LocalActionUsed -= this.OnLocalActionUsed;
        this.Watcher.Dispose();
        this.Sync.Played -= this.OnAnimationPlayed;
        this.Sync.Dispose();
        this.Engine.Dispose();
        this.windows.RemoveAllWindows();
        this.mainWindow.Dispose();
    }
}
