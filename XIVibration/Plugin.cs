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
    public Audio.SoundPreview PreviewMixer { get; } = new();

    /// <summary>Test on your own character (Settings > Test).</summary>
    public CharacterPreview CharaPreview { get; } = new();
    public Audio.PreviewPlayer PreviewPlayer { get; } = new();

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
        if (!ClientState.IsLoggedIn)
            this.ActivePreset = null;
        else if (++this.presetCheck % 30 == 0 || this.ActivePreset == null)
            this.UpdateActivePreset();

        this.Analysis.Update();

        // The animation never showed up: time the pattern from the server message.
        if (this.pending is { } p && Clock.NowMs - p.ReceivedMs > PendingWaitMs)
        {
            this.pending = null;
            this.Start(p.Plan, p.ReceivedMs, 1f, "server message (animation not seen)", 0);
        }

        this.CharaPreview.Update();
        this.Engine.Update();
    }

    // Mods can be switched while zoning; re-read animations on the next use.
    private void OnTerritoryChanged(uint territory) => this.Timelines.Clear();

    private int presetCheck;

    public HitAnalyzer.MultiHitTable? MultiHitFor(uint actionId)
        => this.Config.Hits.Uses(HitSource.MultiHit) && this.MultiHit.TryGet(actionId, out var t) ? t : null;

    private readonly Dictionary<(uint Action, uint Timeline), (int Key, AutoResult Result)> autoCache = new();

    /// <summary>The automatic pattern for a skill, from its current animation.</summary>
    public AutoResult BuildAuto(uint actionId, ActionTimelineInfo tl)
    {
        var key = HashCode.Combine(
            this.Timelines.Generation,
            this.MultiHit.Version,
            this.Config.Hits.Key,
            this.Config.Sound.Key);
        if (this.autoCache.TryGetValue((actionId, tl.TimelineId), out var c) && c.Key == key)
            return c.Result;

        var result = AutoPattern.Build(this.Catalog.Get(actionId), tl, this.MultiHitFor(actionId), this.Config);
        this.autoCache[(actionId, tl.TimelineId)] = (key, result);
        return result;
    }

    // ---------------------------------------------------------------- character presets

    /// <summary>The logged-in character's preset: their own patterns and switched-off skills.</summary>
    public CharacterPreset? ActivePreset { get; private set; }

    /// <summary>Picks the logged-in character's preset (made empty if new). Cheap when nothing changed.</summary>
    private void UpdateActivePreset()
    {
        var lp = ObjectTable.LocalPlayer;
        if (lp == null)
            return;

        var name = lp.Name.TextValue;
        var world = lp.HomeWorld.IsValid ? lp.HomeWorld.Value.Name.ExtractText() : string.Empty;
        if (this.ActivePreset is { } cur && cur.Name == name && cur.World == world)
            return;

        var key = $"{name}@{world}";
        if (!this.Config.Characters.TryGetValue(key, out var preset))
        {
            preset = new CharacterPreset { Name = name, World = world };
            this.Config.Characters[key] = preset;
        }

        this.ActivePreset = preset;

        // Edits from before presets existed move into the first character that logs in.
        if (this.Config.MoveOldEditsToFirstCharacter)
        {
            var moved = 0;
            foreach (var (id, rule) in this.Config.Rules.ToList())
            {
                if (rule.Origin == RuleOrigin.User)
                {
                    preset.Rules[id] = rule;
                    this.Config.Rules.Remove(id);
                    moved++;
                }
                else if (!rule.Enabled)
                {
                    preset.Disabled.Add(id);
                    rule.Enabled = true;
                }
            }

            foreach (var id in this.Config.AutoDisabled)
                preset.Disabled.Add(id);
            this.Config.AutoDisabled.Clear();
            this.Config.MoveOldEditsToFirstCharacter = false;
            Chat.Print($"[XIVibration] Your edited skills ({moved}) are now saved in {name}'s preset. Each character keeps its own from now on.");
        }

        this.Config.Save();
    }

    /// <summary>The user's own pattern for a skill: from the active preset (or, before login, older edits).</summary>
    public ActionRule? UserRule(uint actionId)
    {
        if (this.ActivePreset != null && this.ActivePreset.Rules.TryGetValue(actionId, out var r))
            return r;
        return this.Config.Rules.TryGetValue(actionId, out var g) && g.Origin == RuleOrigin.User ? g : null;
    }

    public bool IsDisabled(uint actionId)
        => this.ActivePreset?.Disabled.Contains(actionId) ?? this.Config.AutoDisabled.Contains(actionId);

    public void SetDisabled(uint actionId, bool off)
    {
        var set = this.ActivePreset?.Disabled ?? this.Config.AutoDisabled;
        if (off)
            set.Add(actionId);
        else
            set.Remove(actionId);
        this.Touch();
    }

    /// <summary>Makes a pattern the user's own and stores it in the active preset (the same object, so editors keep working).</summary>
    public void PutUserRule(ActionRule rule)
    {
        rule.Origin = RuleOrigin.User;
        if (this.ActivePreset == null)
        {
            this.Config.Rules[rule.ActionId] = rule;
            return;
        }

        this.ActivePreset.Rules[rule.ActionId] = rule;
        if (this.Config.Rules.TryGetValue(rule.ActionId, out var g) && ReferenceEquals(g, rule))
            this.Config.Rules.Remove(rule.ActionId);
        this.Touch();
    }

    public void RemoveUserRule(uint actionId)
    {
        this.ActivePreset?.Rules.Remove(actionId);
        if (this.Config.Rules.TryGetValue(actionId, out var g) && g.Origin == RuleOrigin.User)
            this.Config.Rules.Remove(actionId);
        this.Touch();
    }

    private void Touch()
    {
        if (this.ActivePreset != null)
            this.ActivePreset.Updated = DateTime.Now;
    }

    // ---------------------------------------------------------------- plans

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

        var user = this.UserRule(actionId);
        if (user != null)
        {
            // Older patterns counted from the first hit: bake that in, the editor works in animation time.
            if (user.Anchor == PulseAnchor.FirstHit)
            {
                var firstHit = auto.Hits.Count > 0 ? auto.Hits[0].Ms : 0;
                foreach (var p in user.Pulses)
                    p.DelayMs = Math.Max(0, p.DelayMs + firstHit);
                user.Anchor = PulseAnchor.AnimationStart;
            }

            foreach (var p in user.Pulses)
                p.UpgradeShape();

            var pulses = Configuration.ClonePulses(user.Pulses);
            return new SkillPlan(actionId, user.Enabled ? SkillMode.Custom : SkillMode.Off, tl, auto, user, pulses, user.Strength);
        }

        var off = this.IsDisabled(actionId);
        var mode = off ? SkillMode.Off : keepBuiltIn ? SkillMode.BuiltIn : SkillMode.Auto;
        if (this.Config.Rules.TryGetValue(actionId, out var rule))
            return new SkillPlan(actionId, mode, tl, auto, rule, Configuration.ClonePulses(rule.Pulses), rule.Strength * auto.Scale);

        // Not analysed yet: build it live.
        return new SkillPlan(actionId, mode, tl, auto, null, auto.Pulses, auto.Scale);
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
                c.FadeInMs = (int)MathF.Round(p.FadeInMs / speed);
                c.FadeOutMs = (int)MathF.Round(p.FadeOutMs / speed);
                c.FitFades();
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
                this.CharaPreview.Cancel();
                this.Engine.StopAll();
                this.PreviewPlayer.Stop();
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
        this.PreviewPlayer.Dispose();
        this.CharaPreview.Dispose();
        this.Engine.Dispose();
        this.windows.RemoveAllWindows();
        this.mainWindow.Dispose();
    }
}
