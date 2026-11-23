using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using XIVibration.Timeline;

namespace XIVibration.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private static readonly Vector4 Dim = new(0.62f, 0.62f, 0.62f, 1f);
    private static readonly Vector4 Good = new(0.45f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 Bad = new(0.95f, 0.45f, 0.4f, 1f);
    private static readonly Vector4 CustomColor = new(0.95f, 0.8f, 0.35f, 1f);
    private static readonly Vector4 BuiltInColor = new(0.75f, 0.6f, 1f, 1f);
    private static readonly Vector4 StrongColor = new(0.95f, 0.55f, 0.25f, 1f);
    private static readonly Vector4 WeakColor = new(0.35f, 0.7f, 1f, 1f);
    private static readonly Vector4 HitColor = new(1f, 1f, 1f, 0.55f);

    private static readonly (SkillGroup Group, string Label, bool Open)[] Groups =
    {
        (SkillGroup.Weaponskill, "Weaponskills", true),
        (SkillGroup.Spell, "Spells", true),
        (SkillGroup.Ability, "Abilities", true),
        (SkillGroup.Role, "Role actions", false),
        (SkillGroup.LimitBreak, "Limit breaks", false),
        (SkillGroup.Other, "Other", false),
    };

    private readonly Plugin plugin;
    private readonly FileDialogManager fileDialog = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();

    private uint? selectedId;
    private uint? pinnedJob;
    private string search = string.Empty;

    private bool showImport;
    private string importPath = string.Empty;
    private bool importAppend;
    private string? importMessage;

    private List<VibrationPulse>? clipboard;
    private readonly PulseTimelineEditor timeline = new();
    private long testStartMs = -1;
    private long testLengthMs;

    private int loadBudget;
    private bool dirty;
    private long lastEditMs;

    public MainWindow(Plugin plugin)
        : base("XIVibration###XIVibrationMain")
    {
        this.plugin = plugin;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(780, 500),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        this.Size = new Vector2(1000, 680);
        this.SizeCondition = ImGuiCond.FirstUseEver;
    }

    private Configuration Config => this.plugin.Config;

    public override void OnClose() => this.SaveNow();

    public void Dispose() => this.SaveNow();

    /// <summary>The rule shown in the editor; editing a generated one makes it the user's own.</summary>
    private ActionRule? editingRule;

    private void MarkEdited()
    {
        this.discreteEdit = true;
        this.MarkEditedContinuous();
    }

    /// <summary>For drags and sliders: undo snapshots are taken when they start, not every frame.</summary>
    private void MarkEditedContinuous()
    {
        if (this.editingRule is { Origin: RuleOrigin.Generated } r)
            this.plugin.PutUserRule(r);
        this.MarkDirty();
    }

    private bool discreteEdit;
    private List<VibrationPulse> undoBefore = new();

    private void BeginUndoSection(ActionRule rule)
    {
        this.undoBefore = Configuration.ClonePulses(rule.Pulses);
        this.discreteEdit = false;
    }

    private void EndUndoSection(ActionRule rule)
    {
        if (this.discreteEdit)
            this.timeline.Snapshot(rule.ActionId, this.undoBefore);
        this.discreteEdit = false;
    }

    private void MarkDirty()
    {
        this.dirty = true;
        this.lastEditMs = this.clock.ElapsedMilliseconds;
    }

    private void SaveNow()
    {
        if (!this.dirty)
            return;
        this.dirty = false;
        this.Config.Save();
    }

    public override void Draw()
    {
        // Reading animation files is spread over frames so opening a job doesn't stutter.
        this.loadBudget = 6;

        this.DrawTopBar();

        using (var tabs = ImRaii.TabBar("##tabs"))
        {
            if (tabs)
            {
                using (var tab = ImRaii.TabItem("Skills"))
                    if (tab) this.DrawSkillsTab();
                using (var tab = ImRaii.TabItem("Presets"))
                    if (tab) this.DrawPresetsTab();
                using (var tab = ImRaii.TabItem("Settings"))
                    if (tab) this.DrawSettingsTab();
                using (var tab = ImRaii.TabItem("How it works"))
                    if (tab) DrawHelpTab();
            }
        }

        this.fileDialog.Draw();

        if (this.dirty && this.clock.ElapsedMilliseconds - this.lastEditMs > 800)
            this.SaveNow();
    }

    // ---------------------------------------------------------------- top bar

    private void DrawTopBar()
    {
        var enabled = this.Config.Enabled;
        if (ImGui.Checkbox("Enabled", ref enabled))
        {
            this.Config.Enabled = enabled;
            this.MarkDirty();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(180 * ImGuiHelpers.GlobalScale);
        var master = this.Config.MasterStrength * 100f;
        if (ImGui.SliderFloat("Master strength", ref master, 0f, 200f, "%.0f%%"))
        {
            this.Config.MasterStrength = master / 100f;
            this.MarkDirty();
        }

        ImGui.SameLine();
        this.DrawLiveMeter(160 * ImGuiHelpers.GlobalScale);

        ImGui.SameLine();
        if (ImGuiComponents.IconButton("##stopall", FontAwesomeIcon.Stop))
        {
            this.plugin.Engine.StopAll();
            this.plugin.PreviewPlayer.Stop();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Stop all vibration");

        var e = this.plugin.Engine;
        if (!e.PadFound || !this.plugin.Watcher.HookActive || (this.Config.RespectGameSetting && e.GameVibrationEnabled == false))
        {
            using var c = ImRaii.PushColor(ImGuiCol.Text, Bad);
            ImGui.TextUnformatted(!e.PadFound
                ? "Gamepad device not ready yet. Connect a controller and check the Settings tab."
                : !this.plugin.Watcher.HookActive
                    ? "Action hook failed. Skills cannot be detected (see Settings tab)."
                    : "Vibration is turned off in the game's settings (System Configuration > Gamepad > Enable vibration).");
        }

        ImGui.Separator();
    }

    private void DrawLiveMeter(float width)
    {
        var e = this.plugin.Engine;
        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var h = ImGui.GetFrameHeight();
        var half = (h - 2) / 2f;
        dl.AddRectFilled(pos, pos + new Vector2(width, h), ImGui.GetColorU32(ImGuiCol.FrameBg), 3f);
        dl.AddRectFilled(pos, pos + new Vector2(width * e.CurrentStrong / 100f, half), ImGui.GetColorU32(StrongColor), 2f);
        dl.AddRectFilled(pos + new Vector2(0, half + 2), pos + new Vector2(width * e.CurrentWeak / 100f, h), ImGui.GetColorU32(WeakColor), 2f);
        ImGui.Dummy(new Vector2(width, h));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Live output from this plugin\nStrong (left motor): {e.CurrentStrong}%\nWeak (right motor): {e.CurrentWeak}%");
    }

    // ---------------------------------------------------------------- skills tab

    private void DrawSkillsTab()
    {
        var listWidth = 320 * ImGuiHelpers.GlobalScale;

        using (var left = ImRaii.Child("##left", new Vector2(listWidth, 0), true))
        {
            if (left)
                this.DrawSkillList();
        }

        ImGui.SameLine();

        using (var right = ImRaii.Child("##right", Vector2.Zero, true))
        {
            if (right)
                this.DrawEditor();
        }
    }

    private uint ShownJob => this.pinnedJob ?? ActionCatalog.CurrentJobId();

    private void DrawJobSelector()
    {
        var current = ActionCatalog.CurrentJobId();
        var currentAbbr = this.plugin.Catalog.GetJob(current)?.Abbr ?? "-";
        var label = this.pinnedJob is { } pj
            ? JobLabel(this.plugin.Catalog.GetJob(pj))
            : $"Current job ({currentAbbr})";

        ImGui.SetNextItemWidth(-1);
        using var combo = ImRaii.Combo("##job", label, ImGuiComboFlags.HeightLarge);
        if (!combo)
            return;

        if (ImGui.Selectable($"Current job ({currentAbbr})", this.pinnedJob == null))
            this.pinnedJob = null;
        ImGui.Separator();
        foreach (var job in this.plugin.Catalog.Jobs)
        {
            if (ImGui.Selectable(JobLabel(job), this.pinnedJob == job.Id))
                this.pinnedJob = job.Id;
        }
    }

    private static string JobLabel(JobInfo? job) => job == null ? "-" : $"{job.Abbr}  {job.Name}";

    private void DrawSkillList()
    {
        this.DrawJobSelector();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##search", "Search skills...", ref this.search, 64);

        var recent = this.plugin.Watcher.Recent;
        if (recent.Count > 0 && ImGui.CollapsingHeader($"Recently used##recent", ImGuiTreeNodeFlags.DefaultOpen))
        {
            foreach (var id in recent.Take(5).ToArray())
            {
                using var pid = ImRaii.PushId((int)id + 1_000_000);
                this.SkillRow(id);
            }
        }

        var job = this.ShownJob;
        var skills = this.plugin.Catalog.ForJob(job);
        if (skills.Count == 0)
        {
            using var c = ImRaii.PushColor(ImGuiCol.Text, Dim);
            ImGui.TextWrapped(job == 0 ? "Log in to see your job's skills, or pick a job above." : "No skills found for this job.");
            return;
        }

        var term = this.search.Trim();
        foreach (var (group, label, open) in Groups)
        {
            var items = skills.Where(a => a.Group == group && ActionCatalog.MatchesSearch(a, term)).ToList();
            if (items.Count == 0)
                continue;

            var flags = open || term.Length > 0 ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
            if (!ImGui.CollapsingHeader($"{label} ({items.Count})##{group}", flags))
                continue;

            foreach (var a in items)
            {
                using var pid = ImRaii.PushId((int)a.Id);
                this.SkillRow(a.Id);
            }
        }
    }

    /// <summary>One line in the list: on/off box, icon, name, badge.</summary>
    private void SkillRow(uint id)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var cached = this.plugin.Timelines.IsCached(id);
        SkillPlan? plan = null;
        if (cached || this.loadBudget-- > 0)
            plan = this.plugin.PlanFor(id);

        var on = plan?.Mode is SkillMode.Auto or SkillMode.Custom or SkillMode.BuiltIn;
        if (ImGui.Checkbox("##on", ref on))
            this.SetSkillOn(id, on);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(plan?.Mode == SkillMode.BuiltIn
                ? "This animation vibrates on its own, and that is kept.\nThis switch only controls the plugin's pattern (used when \"Replace built-in vibration\" is on)."
                : on ? "Vibrates. Click to switch off." : "Off. Click to switch on.");
        }

        ImGui.SameLine();
        var (badge, color) = plan?.Mode switch
        {
            SkillMode.Custom => ("Custom", CustomColor),
            SkillMode.Auto => (plan.Pulses.Count > 1 ? $"Auto x{plan.Pulses.Count}" : "Auto", Good),
            SkillMode.BuiltIn => ("Built-in", BuiltInColor),
            SkillMode.Off => ("Off", Dim),
            _ => ("...", Dim),
        };

        if (this.ActionRow(id, this.selectedId == id, badge, color, 22 * scale))
            this.Select(id);
    }

    private void SetSkillOn(uint id, bool on)
    {
        var user = this.plugin.UserRule(id);
        if (user != null)
            user.Enabled = on;
        else
            this.plugin.SetDisabled(id, !on);
        this.MarkDirty();
    }

    private bool ActionRow(uint id, bool selected, string badge, Vector4 badgeColor, float iconSize)
    {
        var info = this.plugin.Catalog.Get(id);
        var start = ImGui.GetCursorPos();
        var clicked = ImGui.Selectable("##row", selected, ImGuiSelectableFlags.None, new Vector2(0, iconSize));
        ImGui.SetCursorPos(start);
        DrawIcon(info?.Icon ?? 0, iconSize);
        ImGui.SameLine();
        ImGui.SetCursorPosY(start.Y + (iconSize - ImGui.GetTextLineHeight()) / 2f);
        ImGui.TextUnformatted(this.plugin.Catalog.NameOf(id));
        if (badge.Length > 0)
        {
            ImGui.SameLine();
            using var c = ImRaii.PushColor(ImGuiCol.Text, badgeColor);
            ImGui.TextUnformatted(badge);
        }

        ImGui.SetCursorPos(start + new Vector2(0, iconSize + ImGui.GetStyle().ItemSpacing.Y));
        ImGui.Dummy(Vector2.Zero);
        return clicked;
    }

    private static void DrawIcon(uint icon, float size)
    {
        if (icon == 0)
        {
            ImGui.Dummy(new Vector2(size));
            return;
        }

        var wrap = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(icon)).GetWrapOrEmpty();
        ImGui.Image(wrap.Handle, new Vector2(size));
    }

    private void Select(uint id)
    {
        this.selectedId = id;
        this.importMessage = null;
    }

    // ---------------------------------------------------------------- editor

    private void DrawEditor()
    {
        if (this.selectedId is not { } id)
        {
            using var c = ImRaii.PushColor(ImGuiCol.Text, Dim);
            ImGui.TextWrapped("Every skill vibrates on its hits automatically. Pick a skill on the left to see its pattern, test it, or make your own.");
            return;
        }

        var scale = ImGuiHelpers.GlobalScale;
        var plan = this.plugin.PlanFor(id);
        var info = this.plugin.Catalog.Get(id);

        // Header
        DrawIcon(info?.Icon ?? 0, 40 * scale);
        ImGui.SameLine();
        using (ImRaii.Group())
        {
            ImGui.TextUnformatted(this.plugin.Catalog.NameOf(id));
            using var c = ImRaii.PushColor(ImGuiCol.Text, Dim);
            ImGui.TextUnformatted($"{info?.JobAbbr} Lv{info?.Level}  ·  {info?.Group}  ·  ID {id}");
            this.DrawTimelineInfo(plan);
        }

        ImGui.Spacing();
        this.editingRule = plan.Rule;
        if (plan.Rule != null)
            this.DrawCustomEditor(plan, plan.Rule);
        else
            this.DrawAutoEditor(plan);
    }

    private void DrawTimelineInfo(SkillPlan plan)
    {
        var tl = plan.Timeline;
        var hits = plan.Auto.HitMs.Count == 0
            ? "-"
            : string.Join(", ", plan.Auto.HitMs.Select(ms => $"{ms / 1000f:0.00}s"));
        var source = plan.Auto.SourceText;
        ImGui.TextUnformatted($"Hits at {hits}  ({source})");

        if (tl.GamePath.Length > 0)
        {
            ImGui.TextUnformatted(tl.FromMod ? $"{tl.GamePath}  (mod)" : tl.GamePath);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip((tl.ResolvedPath ?? tl.GamePath) + (tl.Error != null ? $"\n{tl.Error}" : string.Empty) + "\nClick to use it as the import path.");
            if (ImGui.IsItemClicked())
            {
                this.importPath = tl.ResolvedPath ?? tl.GamePath;
                this.showImport = plan.Rule != null;
            }
        }
        else if (tl.Error != null)
        {
            ImGui.TextUnformatted(tl.Error);
        }
    }

    private void DrawAutoEditor(SkillPlan plan)
    {
        var id = plan.ActionId;
        var on = !this.plugin.IsDisabled(id);
        if (ImGui.Checkbox("Vibrate this skill", ref on))
            this.SetSkillOn(id, on);

        ImGui.SameLine();
        var builtIn = plan.Auto.HasBuiltIn && !this.Config.ReplaceModVibration;
        var preview = builtIn ? plan.Auto.BuiltInPreview : plan.Pulses;
        using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.2f, 0.5f, 0.25f, 1f)))
        {
            if (ImGui.Button("  Test  "))
                this.Test(preview, builtIn ? 1f : plan.Scale, plan.Timeline);
        }

        if (builtIn && ImGui.IsItemHovered())
            ImGui.SetTooltip("Plays an approximation of the animation's own vibration.");

        ImGui.SameLine();
        if (ImGui.Button("Customize"))
            this.CreateCustomRule(plan, preview, builtIn);

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Make your own pattern for this skill, starting from this one.");

        ImGui.Spacing();
        using (ImRaii.PushColor(ImGuiCol.Text, builtIn ? BuiltInColor : Dim))
        {
            if (builtIn)
            {
                ImGui.TextWrapped("This skill's animation already has its own vibration (from a mod or the game). " +
                                  "It is kept as it is and the plugin adds nothing. To use the plugin's pattern instead, turn on " +
                                  "\"Replace built-in vibration\" in Settings, or click Customize.");
            }
            else
            {
                ImGui.TextWrapped("Automatic: one pulse on each hit of the animation, as strong as the skill's sound is loud at that hit; the last hit is the strongest " +
                                  "(bass = strong motor, crisp sounds = weak motor). Without readable sounds, pulses come from the animation's hits. " +
                                  "The colored shading behind the clips is the sound.");
            }
        }

        ImGui.Spacing();
        this.timeline.Draw(new PulseTimelineEditor.Input
        {
            Key = id,
            Display = preview,
            Strength = builtIn ? 1f : plan.Scale,
            Hits = plan.Auto.Hits,
            AutoPulses = plan.Auto.Pulses,
            Sound = plan.Auto.Sound,
            SoundMarks = plan.Auto.SoundMarks ?? new List<int>(),
            Tracks = plan.Timeline.Sound?.Tracks ?? Array.Empty<Audio.SoundTrack>(),
            Muted = this.MutedFor(plan.ActionId),
            ToggleMute = key => this.ToggleMute(plan.ActionId, key),
            Rejected = plan.Auto.Rejected ?? new List<HitPoint>(),
            RuleText = this.RuleText(),
            MakeEditable = () => this.CreateCustomRule(plan, preview, builtIn).Pulses,
            Edited = this.MarkEdited,
            PlayheadMs = this.PlayheadMs,
        });
    }

    private ActionRule CreateCustomRule(SkillPlan plan, IReadOnlyList<VibrationPulse> preview, bool builtIn)
    {
        var rule = new ActionRule
        {
            ActionId = plan.ActionId,
            Anchor = PulseAnchor.AnimationStart,
            Strength = builtIn ? 1f : plan.Scale,
            Pulses = Configuration.ClonePulses(preview.Count > 0 ? preview : plan.Auto.Pulses),
        };
        this.plugin.PutUserRule(rule);
        this.plugin.SetDisabled(plan.ActionId, false);
        this.editingRule = rule;
        this.MarkDirty();
        return rule;
    }

    /// <summary>Where the Test playhead is, in ms from the animation start, while a test plays.</summary>
    private float? PlayheadMs
    {
        get
        {
            if (this.testStartMs < 0)
                return null;
            var elapsed = Clock.NowMs - this.testStartMs;
            if (elapsed <= Math.Max(this.testLengthMs, 1))
                return elapsed;
            this.testStartMs = -1;
            return null;
        }
    }

    private void DrawCustomEditor(SkillPlan plan, ActionRule rule)
    {
        var scale = ImGuiHelpers.GlobalScale;

        var enabled = rule.Enabled;
        if (ImGui.Checkbox("Enabled##rule", ref enabled))
        {
            rule.Enabled = enabled;
            this.MarkDirty();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(140 * scale);
        var strength = rule.Strength * 100f;
        if (ImGui.SliderFloat("Strength##rule", ref strength, 0f, 200f, "%.0f%%"))
        {
            rule.Strength = strength / 100f;
            this.MarkDirty();
        }

        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.2f, 0.5f, 0.25f, 1f)))
        {
            if (ImGui.Button("  Test  "))
                this.Test(plan.Pulses, plan.Scale, plan.Timeline);
        }

        ImGui.SameLine();
        var generated = rule.Origin == RuleOrigin.Generated;
        var ctrl = ImGui.GetIO().KeyCtrl;
        using (ImRaii.Disabled(!generated && !ctrl))
        {
            if (ImGui.Button(generated ? "Re-analyse" : "Reset to auto"))
            {
                this.plugin.Timelines.Clear();
                this.plugin.Analysis.Regenerate(rule.ActionId);
                return;
            }
        }

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(generated
                ? "Read the skill's current animation again and rebuild this pattern."
                : "Hold Ctrl and click to throw away your pattern and rebuild it from the animation.");
        }

        using (ImRaii.PushColor(ImGuiCol.Text, plan.Mode == SkillMode.BuiltIn ? BuiltInColor : Dim))
        {
            if (plan.Mode == SkillMode.BuiltIn)
            {
                ImGui.TextWrapped("This skill's animation already has its own vibration (from a mod or the game). It is kept as it is and " +
                                  "this pattern doesn't play. It plays instead when \"Replace built-in vibration\" is on in Settings.");
            }
            else if (generated)
            {
                ImGui.TextWrapped("Made automatically from this skill's sounds and animation hits (the shading behind the clips is the sound). " +
                                  "Edit anything below and it becomes your own pattern, which re-analysis never changes.");
            }
            else
            {
                ImGui.TextWrapped("Your own pattern.");
            }
        }

        this.firstHitMs = plan.Auto.Hits.Count > 0 ? plan.Auto.Hits[0].Ms : 0;

        ImGui.Spacing();
        this.BeginUndoSection(rule);
        this.DrawPatternTools(rule);
        this.EndUndoSection(rule);
        const int offset = 0;
        ImGui.Spacing();
        this.timeline.Draw(new PulseTimelineEditor.Input
        {
            Key = rule.ActionId,
            Pulses = rule.Pulses,
            OffsetMs = offset,
            Strength = plan.Scale,
            Hits = plan.Auto.Hits,
            AutoPulses = plan.Auto.Pulses,
            Sound = plan.Auto.Sound,
            SoundMarks = plan.Auto.SoundMarks ?? new List<int>(),
            Tracks = plan.Timeline.Sound?.Tracks ?? Array.Empty<Audio.SoundTrack>(),
            Muted = this.MutedFor(plan.ActionId),
            ToggleMute = key => this.ToggleMute(plan.ActionId, key),
            Rejected = plan.Auto.Rejected ?? new List<HitPoint>(),
            RuleText = this.RuleText(),
            Edited = this.MarkEdited,
            PlayheadMs = this.PlayheadMs,
        });
        ImGui.Spacing();
        this.BeginUndoSection(rule);
        this.DrawPulseTable(rule, offset);
        this.EndUndoSection(rule);
    }

    /// <summary>Sounds muted in Test, per skill (not saved).</summary>
    private readonly Dictionary<uint, HashSet<string>> muted = new();

    private HashSet<string> MutedFor(uint actionId)
    {
        if (!this.muted.TryGetValue(actionId, out var set))
            this.muted[actionId] = set = new HashSet<string>();
        return set;
    }

    private void ToggleMute(uint actionId, string key)
    {
        var set = this.MutedFor(actionId);
        if (!set.Remove(key))
            set.Add(key);
    }

    /// <summary>First hit of the skill in the editor (ms): presets start there.</summary>
    private int firstHitMs;

    private void Test(IReadOnlyList<VibrationPulse> pulses, float strength, Timeline.ActionTimelineInfo? timeline = null)
    {
        this.plugin.Engine.StopAll();
        this.plugin.PreviewPlayer.Stop();

        // The skill's sound, mixed at its animation times (mods included).
        byte[]? wav = null;
        if (this.Config.PreviewVolume > 0 && timeline is { Found: true })
        {
            try
            {
                wav = this.plugin.PreviewMixer.Mix(timeline.Entries, this.plugin.Timelines.ReadFile, this.Config.PreviewVolume / 100f, this.MutedFor(this.selectedId ?? 0));
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "Could not mix the Test sound");
            }
        }

        // Sound and vibration start from the same moment.
        var t0 = Clock.NowMs;
        if (wav != null)
            this.plugin.PreviewPlayer.Play(wav);
        this.plugin.Engine.Play(pulses, strength * this.Config.MasterStrength, ignoreGates: true, startMs: t0 + this.Config.PreviewSyncMs);
        this.testStartMs = t0 + this.Config.PreviewSyncMs;
        this.testLengthMs = pulses.Count == 0 ? 0 : pulses.Max(p => p.DelayMs + p.DurationMs);
    }

    private void DrawPatternTools(ActionRule rule)
    {
        var scale = ImGuiHelpers.GlobalScale;

        ImGui.SetNextItemWidth(200 * scale);
        using (var combo = ImRaii.Combo("##preset", "Apply preset..."))
        {
            if (combo)
            {
                foreach (var p in Presets.All)
                {
                    if (ImGui.Selectable(p.Name))
                    {
                        rule.Pulses = Presets.Copy(p);
                        foreach (var c in rule.Pulses)
                            c.DelayMs += this.firstHitMs;
                        rule.Anchor = PulseAnchor.AnimationStart;
                        this.MarkEdited();
                    }

                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(p.Description + "\nStarts on the skill's first hit.");
                }
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Copy"))
            this.clipboard = Configuration.ClonePulses(rule.Pulses);
        ImGui.SameLine();
        using (ImRaii.Disabled(this.clipboard == null))
        {
            if (ImGui.Button("Paste") && this.clipboard != null)
            {
                rule.Pulses = Configuration.ClonePulses(this.clipboard);
                this.MarkEdited();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button(this.showImport ? "Hide import" : "Import from .tmb..."))
        {
            this.importMessage = null;
            this.showImport = !this.showImport;
        }

        if (this.showImport)
            this.DrawImportPanel(rule);
    }

    private void DrawImportPanel(ActionRule rule)
    {
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.Separator();
        ImGui.TextWrapped("Turns the vibration entries (C120) of an animation timeline into pulses. " +
                          "Use a .tmb file on disk or a game path like chara/action/ws/bt_2sp_emp/ws_s20.tmb.");
        ImGui.Spacing();
        ImGui.SetNextItemWidth(-90 * scale);
        ImGui.InputTextWithHint("##importpath", "File on disk or game path", ref this.importPath, 512);
        ImGui.SameLine();
        if (ImGui.Button("Browse..."))
        {
            this.fileDialog.OpenFileDialog("Pick a .tmb file", "Timeline{.tmb}", (ok, paths) =>
            {
                if (ok && paths.Count > 0)
                    this.importPath = paths[0];
            }, 1, null, false);
        }

        ImGui.Checkbox("Add to the current pulses instead of replacing them", ref this.importAppend);

        if (ImGui.Button("Import", new Vector2(120 * scale, 0)))
        {
            try
            {
                var entries = TmbImport.ReadC120(TmbImport.Load(this.importPath));
                if (entries.Count == 0)
                {
                    this.importMessage = "No vibration entries in this file.";
                }
                else
                {
                    var pulses = TmbImport.ToPulses(entries);
                    if (rule.Anchor == PulseAnchor.FirstHit && !this.importAppend)
                        rule.Anchor = PulseAnchor.AnimationStart; // C120 times count from the animation start
                    if (this.importAppend)
                        rule.Pulses.AddRange(pulses);
                    else
                        rule.Pulses = pulses;
                    rule.Pulses.Sort((a, b) => a.DelayMs.CompareTo(b.DelayMs));
                    this.MarkEdited();
                    this.importMessage = $"Imported {entries.Count} pulse(s): " + string.Join(", ",
                        entries.Select(e => $"frame {e.TimeFrames} for {e.DurationFrames}f, type {e.WaveType}"));
                }
            }
            catch (Exception ex)
            {
                this.importMessage = "Error: " + ex.Message;
            }
        }

        if (this.importMessage != null)
        {
            using var c = ImRaii.PushColor(ImGuiCol.Text, this.importMessage.StartsWith("Error") ? Bad : Good);
            ImGui.TextWrapped(this.importMessage);
        }

        ImGui.Separator();
    }

    /// <summary>Shapes offered in the editor; fading is done with the clip's fades.</summary>
    private static readonly PulseShape[] EditorShapes = { PulseShape.Constant, PulseShape.Bump, PulseShape.Throb };

    private void DrawPulseTable(ActionRule rule, int offset)
    {
        var scale = ImGuiHelpers.GlobalScale;

        if (ImGui.Button("+ Add pulse"))
        {
            var last = rule.Pulses.Count == 0 ? null : rule.Pulses.MaxBy(p => p.DelayMs + p.DurationMs);
            rule.Pulses.Add(new VibrationPulse
            {
                DelayMs = last == null ? 0 : last.DelayMs + last.DurationMs + 100,
                DurationMs = 200,
                Strong = last?.Strong ?? 60,
                Weak = last?.Weak ?? 60,
            });
            this.MarkEdited();
        }

        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Text, Dim))
            ImGui.TextUnformatted("Strong = left, heavy motor. Weak = right, light buzz. Curve: + drops fast with a thin tail, - holds then drops.");

        if (rule.Pulses.Count == 0)
        {
            using var c = ImRaii.PushColor(ImGuiCol.Text, Dim);
            ImGui.TextUnformatted("No pulses. This skill won't vibrate.");
            return;
        }

        using (var table = ImRaii.Table("##pulses", 10, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY))
        {
            if (!table)
                return;

            ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 22 * scale);
            ImGui.TableSetupColumn("Start (ms)", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("Length (ms)", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("Strong %", ImGuiTableColumnFlags.WidthStretch, 1.1f);
            ImGui.TableSetupColumn("Weak %", ImGuiTableColumnFlags.WidthStretch, 1.1f);
            ImGui.TableSetupColumn("Shape", ImGuiTableColumnFlags.WidthStretch, 0.9f);
            ImGui.TableSetupColumn("Fade in", ImGuiTableColumnFlags.WidthStretch, 0.8f);
            ImGui.TableSetupColumn("Fade out", ImGuiTableColumnFlags.WidthStretch, 0.8f);
            ImGui.TableSetupColumn("Curve", ImGuiTableColumnFlags.WidthStretch, 0.8f);
            ImGui.TableSetupColumn(string.Empty, ImGuiTableColumnFlags.WidthFixed, 46 * scale);
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableHeadersRow();

            int? remove = null, duplicate = null;
            for (var i = 0; i < rule.Pulses.Count; i++)
            {
                var p = rule.Pulses[i];
                using var pid = ImRaii.PushId(i);
                ImGui.TableNextRow();
                if (this.timeline.IsSelected(p))
                    ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.12f)));

                ImGui.TableNextColumn();
                if (ImGui.Selectable($"{i + 1}", this.timeline.IsSelected(p)))
                    this.timeline.Select(p, ImGui.GetIO().KeyCtrl);

                this.IntCell(rule, "##delay", p.DelayMs, -offset, 10000, 5f, "%d", v => p.DelayMs = v);
                this.IntCell(rule, "##dur", p.DurationMs, 16, 5000, 5f, "%d", v =>
                {
                    p.DurationMs = v;
                    p.FadeInMs = Math.Min(p.FadeInMs, v);
                    p.FadeOutMs = Math.Min(p.FadeOutMs, v - p.FadeInMs);
                });
                this.LevelCell(rule, "##strong", p.Strong, StrongColor, v => p.Strong = v);
                this.LevelCell(rule, "##weak", p.Weak, WeakColor, v => p.Weak = v);

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(-1);
                using (var combo = ImRaii.Combo("##shape", ShapeName(p.Shape)))
                {
                    if (combo)
                    {
                        foreach (var sh in EditorShapes)
                        {
                            if (ImGui.Selectable(ShapeName(sh), sh == p.Shape))
                            {
                                p.Shape = sh;
                                this.MarkEdited();
                            }
                        }
                    }
                }

                this.IntCell(rule, "##fin", p.FadeInMs, 0, p.DurationMs, 2f, "%d ms", v => p.FadeInMs = Math.Min(v, p.DurationMs - p.FadeOutMs));
                this.IntCell(rule, "##fout", p.FadeOutMs, 0, p.DurationMs, 2f, "%d ms", v => p.FadeOutMs = Math.Min(v, p.DurationMs - p.FadeInMs));
                this.IntCell(rule, "##fcurve", p.FadeOutCurve, -100, 100, 1f, "%+d", v => p.FadeOutCurve = v);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Fade-out curve. Drag the dot on the fade in the timeline to bend it there too (fade-in has its own dot).");

                ImGui.TableNextColumn();
                if (ImGuiComponents.IconButton("##dup", FontAwesomeIcon.Copy))
                    duplicate = i;
                ImGui.SameLine(0, 2 * scale);
                if (ImGuiComponents.IconButton("##del", FontAwesomeIcon.Trash))
                    remove = i;
            }

            if (duplicate is { } c2)
            {
                var copy = rule.Pulses[c2].Clone();
                copy.DelayMs += rule.Pulses[c2].DurationMs + 50;
                rule.Pulses.Insert(c2 + 1, copy);
                this.MarkEdited();
            }

            if (remove is { } r)
            {
                rule.Pulses.RemoveAt(r);
                this.MarkEdited();
            }
        }

        // Keep the clips in time order (stable, and not while a value is being dragged).
        if (!ImGui.IsAnyItemActive())
        {
            var sorted = rule.Pulses.OrderBy(p => p.DelayMs).ToList();
            if (!sorted.SequenceEqual(rule.Pulses))
            {
                rule.Pulses.Clear();
                rule.Pulses.AddRange(sorted);
            }
        }
    }

    private void IntCell(ActionRule rule, string id, int value, int min, int max, float speed, string format, Action<int> set)
    {
        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(-1);
        var v = value;
        if (ImGui.DragInt(id, ref v, speed, min, max, format))
        {
            set(Math.Clamp(v, min, Math.Max(min, max)));
            this.MarkEditedContinuous();
        }

        if (ImGui.IsItemActivated())
            this.timeline.Snapshot(rule.ActionId, this.undoBefore);
    }

    private void LevelCell(ActionRule rule, string id, int value, Vector4 color, Action<int> set)
    {
        ImGui.TableNextColumn();
        ImGui.SetNextItemWidth(-1);
        var v = value;
        using (ImRaii.PushColor(ImGuiCol.SliderGrab, color))
        {
            if (ImGui.SliderInt(id, ref v, 0, 100, "%d%%"))
            {
                set(v);
                this.MarkEditedContinuous();
            }
        }

        if (ImGui.IsItemActivated())
            this.timeline.Snapshot(rule.ActionId, this.undoBefore);
    }

    internal static string ShapeName(PulseShape s) => s switch
    {
        PulseShape.Constant => "Constant",
        PulseShape.FadeOut => "Fade out",
        PulseShape.FadeIn => "Fade in",
        PulseShape.Bump => "Swell",
        PulseShape.Throb => "Throb",
        _ => s.ToString(),
    };

    // ---------------------------------------------------------------- settings tab

    // ---------------------------------------------------------------- presets tab

    private CharacterPreset? pendingImport;
    private string? presetMessage;
    private bool presetMessageIsError;

    private void PresetInfo(string text, bool error = false)
    {
        this.presetMessage = text;
        this.presetMessageIsError = error;
    }

    private void DrawPresetsTab()
    {
        var active = this.plugin.ActivePreset;
        using (ImRaii.PushColor(ImGuiCol.Text, Dim))
        {
            ImGui.TextWrapped("Every skill you change (your own patterns and skills you switch off) is saved in your character's preset. " +
                              "Each character has its own, and it switches when you log in. Automatic patterns are shared by all characters.");
        }

        ImGui.Spacing();
        if (active == null)
        {
            using var c = ImRaii.PushColor(ImGuiCol.Text, Bad);
            ImGui.TextUnformatted("Log in to use character presets.");
        }
        else
        {
            ImGui.TextUnformatted($"Active: {active.Name} @ {active.World}  ·  {active.Rules.Count} own pattern(s), {active.Disabled.Count} skill(s) switched off");

            if (ImGui.Button("Copy code"))
            {
                ImGui.SetClipboardText(PresetTransfer.ToCode(active));
                this.PresetInfo("Preset code copied to the clipboard. Paste it anywhere to share it.");
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("A short text code (XIVIB1:...) you can paste in Discord or a note.");

            ImGui.SameLine();
            if (ImGui.Button("Export file..."))
            {
                var preset = active;
                this.fileDialog.SaveFileDialog("Export preset", ".json", $"xivibration-{preset.Name.Replace(' ', '_')}", ".json", (ok, path) =>
                {
                    if (!ok)
                        return;
                    try
                    {
                        System.IO.File.WriteAllText(path, PresetTransfer.ToJson(preset));
                        this.PresetInfo($"Saved to {path}");
                    }
                    catch (Exception ex)
                    {
                        this.PresetInfo("Could not save: " + ex.Message, true);
                    }
                });
            }

            ImGui.SameLine();
            ImGui.TextUnformatted("|");
            ImGui.SameLine();
            if (ImGui.Button("Import from clipboard"))
                this.StartImport(ImGui.GetClipboardText());

            ImGui.SameLine();
            if (ImGui.Button("Import file..."))
            {
                this.fileDialog.OpenFileDialog("Import preset", ".json", (ok, paths) =>
                {
                    if (!ok || paths.Count == 0)
                        return;
                    try
                    {
                        this.StartImport(System.IO.File.ReadAllText(paths[0]));
                    }
                    catch (Exception ex)
                    {
                        this.PresetInfo("Could not read the file: " + ex.Message, true);
                    }
                }, 1, null, false);
            }

            // Import preview: merge or replace into the active preset.
            if (this.pendingImport is { } imp)
            {
                ImGui.Separator();
                var conflicts = imp.Rules.Keys.Count(active.Rules.ContainsKey);
                var from = imp.Name.Length > 0 ? $"{imp.Name} @ {imp.World}" : "an unnamed preset";
                ImGui.TextWrapped($"Import from {from}: {imp.Rules.Count} pattern(s), {imp.Disabled.Count} switched-off skill(s). " +
                                  $"{conflicts} of them replace patterns you already have.");
                if (ImGui.Button("Merge"))
                {
                    PresetTransfer.Apply(active, imp, false);
                    this.pendingImport = null;
                    this.MarkDirty();
                    this.PresetInfo($"Merged into {active.Name}'s preset.");
                }

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Imported skills replace yours; everything else you changed stays.");

                ImGui.SameLine();
                if (ImGui.Button("Replace"))
                {
                    PresetTransfer.Apply(active, imp, true);
                    this.pendingImport = null;
                    this.MarkDirty();
                    this.PresetInfo($"{active.Name}'s preset is now the imported one.");
                }

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Your preset becomes exactly the imported one.");

                ImGui.SameLine();
                if (ImGui.Button("Cancel"))
                    this.pendingImport = null;
                ImGui.Separator();
            }
        }

        if (this.presetMessage != null)
        {
            using var c = ImRaii.PushColor(ImGuiCol.Text, this.presetMessageIsError ? Bad : Good);
            ImGui.TextWrapped(this.presetMessage);
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("All characters");
        using var table = ImRaii.Table("##presets", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp);
        if (!table)
            return;

        ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch, 2f);
        ImGui.TableSetupColumn("Changes", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Last change", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn(string.Empty, ImGuiTableColumnFlags.WidthStretch, 2.2f);
        ImGui.TableHeadersRow();

        string? delete = null;
        foreach (var (key, preset) in this.Config.Characters.OrderBy(k => k.Key).ToList())
        {
            using var pid = ImRaii.PushId(key);
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var isActive = ReferenceEquals(preset, active);
            using (ImRaii.PushColor(ImGuiCol.Text, Good, isActive))
            {
                if (ImGui.TreeNode($"{preset.Name} @ {preset.World}{(isActive ? "  (active)" : string.Empty)}###node"))
                {
                    foreach (var id in preset.Rules.Keys.OrderBy(k => this.plugin.Catalog.NameOf(k)))
                        ImGui.BulletText(this.plugin.Catalog.NameOf(id) + (preset.Rules[id].Enabled ? string.Empty : "  (off)"));
                    foreach (var id in preset.Disabled.OrderBy(k => this.plugin.Catalog.NameOf(k)))
                        ImGui.BulletText(this.plugin.Catalog.NameOf(id) + "  (switched off)");
                    ImGui.TreePop();
                }
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted($"{preset.Rules.Count} + {preset.Disabled.Count} off");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(preset.Updated.ToString("yyyy-MM-dd HH:mm"));
            ImGui.TableNextColumn();
            if (ImGui.SmallButton("Copy code"))
            {
                ImGui.SetClipboardText(PresetTransfer.ToCode(preset));
                this.PresetInfo($"Code for {preset.Name} copied to the clipboard.");
            }

            if (active != null && !isActive)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Copy to current"))
                {
                    this.pendingImport = PresetTransfer.Parse(PresetTransfer.ToJson(preset));
                    this.PresetInfo($"Choose Merge or Replace above to copy {preset.Name}'s preset to {active.Name}.");
                }
            }

            ImGui.SameLine();
            using (ImRaii.Disabled(!ImGui.GetIO().KeyCtrl || isActive))
            {
                if (ImGui.SmallButton("Delete"))
                    delete = key;
            }

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(isActive ? "The active character's preset can't be deleted." : "Hold Ctrl and click to delete this preset.");
        }

        if (delete != null)
        {
            this.Config.Characters.Remove(delete);
            this.MarkDirty();
        }
    }

    private void StartImport(string text)
    {
        try
        {
            this.pendingImport = PresetTransfer.Parse(text);
            this.PresetInfo("Preset read. Choose Merge or Replace.");
        }
        catch (FormatException ex)
        {
            this.pendingImport = null;
            this.PresetInfo(ex.Message, true);
        }
    }

    private void DrawSettingsTab()
    {
        var scale = ImGuiHelpers.GlobalScale;

        if (ImGui.CollapsingHeader("General", ImGuiTreeNodeFlags.DefaultOpen))
        {
            this.Check("Follow the game's \"Enable vibration\" setting", this.Config.RespectGameSetting, v => this.Config.RespectGameSetting = v);
            this.Check("Only vibrate in combat", this.Config.OnlyInCombat, v => this.Config.OnlyInCombat = v);
            this.Check("Only my character's animations may vibrate", this.Config.OnlyMyCharacter, v => this.Config.OnlyMyCharacter = v);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Mods apply to every character's animations, so a mod with built-in vibration also makes the game\n" +
                                 "vibrate when other players or NPCs near you use that skill. With this on, the game's own vibration\n" +
                                 "only gets through while one of your skills is playing.");
            }

            this.Check("Replace built-in vibration from other mods", this.Config.ReplaceModVibration, v => this.Config.ReplaceModVibration = v);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Off (default): skills whose animation already vibrates keep that vibration, and the plugin adds nothing.\n" +
                                 "On: the plugin mutes that vibration while the skill plays and uses its own pattern, keeping the same hit timing.");
            }
        }

        if (ImGui.CollapsingHeader("Hit detection", ImGuiTreeNodeFlags.DefaultOpen))
            this.DrawHitDetection(scale);

        if (ImGui.CollapsingHeader("Sound"))
            this.DrawSoundSettings(scale);

        if (ImGui.CollapsingHeader("Test"))
        {
            ImGui.SetNextItemWidth(200 * scale);
            var vol = this.Config.PreviewVolume;
            if (ImGui.SliderInt("Sound volume##preview", ref vol, 0, 100, "%d%%"))
            {
                this.Config.PreviewVolume = vol;
                this.MarkDirty();
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Volume of the skill's sound when you press Test. 0% = no sound.");

            ImGui.SetNextItemWidth(200 * scale);
            var sync = this.Config.PreviewSyncMs;
            if (ImGui.SliderInt("Vibration delay vs. sound##preview", ref sync, -200, 300, "%d ms"))
            {
                this.Config.PreviewSyncMs = sync;
                this.MarkDirty();
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("If the Test sound comes out late on your system, delay the vibration by the same amount (or the other way round).");
        }

        if (ImGui.CollapsingHeader("Analysis"))
        {
            var an = this.plugin.Analysis;
            if (an.Running)
                ImGui.ProgressBar(an.Total == 0 ? 0f : an.Done / (float)an.Total, new Vector2(300 * scale, 0), $"{an.Done}/{an.Total}");
            using (ImRaii.PushColor(ImGuiCol.Text, Dim))
                ImGui.TextWrapped(an.Status);

            var ctrl = ImGui.GetIO().KeyCtrl;
            using (ImRaii.Disabled(!ctrl || an.Running))
            {
                if (ImGui.Button("Re-analyse all skills"))
                    an.ReanalyseAll();
            }

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip("Hold Ctrl and click. Reads every skill's current animation again (for example after changing mods)\n" +
                                 "and rebuilds the automatic patterns. Patterns you edited yourself are kept.");
            }
        }

        if (ImGui.CollapsingHeader("Diagnostics"))
        {
            ImGui.TextUnformatted("Motor test");
            if (ImGui.Button("Strong motor"))
                this.plugin.Engine.Play(new[] { new VibrationPulse { DurationMs = 600, Strong = 100, Weak = 0 } }, this.Config.MasterStrength, true);
            ImGui.SameLine();
            if (ImGui.Button("Weak motor"))
                this.plugin.Engine.Play(new[] { new VibrationPulse { DurationMs = 600, Strong = 0, Weak = 100 } }, this.Config.MasterStrength, true);
            ImGui.SameLine();
            if (ImGui.Button("Both"))
                this.plugin.Engine.Play(new[] { new VibrationPulse { DurationMs = 600, Strong = 100, Weak = 100 } }, this.Config.MasterStrength, true);
            ImGui.SameLine();
            if (ImGui.Button("Stop"))
                this.plugin.Engine.StopAll();

            ImGui.Spacing();
            ImGui.TextUnformatted("Status");
            var e = this.plugin.Engine;
            StatusLine("Gamepad device", e.PadFound, e.PadFound ? "found" : "not found");
            StatusLine("Vibration hook", e.HookActive, e.HookActive ? "active" : e.LastError ?? "not active, calling it directly");
            StatusLine("Skill hook", this.plugin.Watcher.HookActive, this.plugin.Watcher.HookActive ? "active" : this.plugin.Watcher.LastError ?? "failed");
            StatusLine("Animation timing hook", this.plugin.Sync.HookActive, this.plugin.Sync.HookActive ? "active, patterns start with the animation" : (this.plugin.Sync.LastError ?? "failed") + ", timing from the server message");
            StatusLine("Vibration from other characters", true, this.Config.OnlyMyCharacter
                ? $"blocked {this.plugin.Engine.BlockedFromOthers} time(s) this session"
                : "allowed (\"Only my character's animations may vibrate\" is off)");
            var pv = this.plugin.PreviewPlayer.LastError;
            StatusLine("Test sound", pv == null, pv == null ? "ready" : $"not available ({pv})");
            var gv = e.GameVibrationEnabled;
            StatusLine("Game option: Enable vibration", gv != false, gv == null ? "could not read" : gv.Value ? "on" : "off");
            var pen = this.plugin.Timelines.PenumbraConnected;
            StatusLine("Penumbra (reads modded animations)", pen != false, pen == null ? "not checked yet" : pen.Value ? "connected" : "not found, using unmodded animations");

            ImGui.Spacing();
            ImGui.TextUnformatted("Last skill");
            this.DrawLastSkill();
        }

        ImGui.Spacing();
        using (ImRaii.PushColor(ImGuiCol.Text, Dim))
            ImGui.TextWrapped("Commands: /xivib (open), /xivib on | off | toggle | test | stop | analyse");
    }

    private void DrawSoundSettings(float scale)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, Dim))
            ImGui.TextWrapped("Pulses sit on the animation's hits, as strong as the skill's sound is loud at each hit (bass: strong motor, the rest: weak motor). Skills without hit markers use the peaks in their sound as hits.");

        var snd = this.Config.Sound;
        var rebuild = false;

        ImGui.SetNextItemWidth(200 * scale);
        var thr = snd.Threshold * 100f;
        if (ImGui.SliderFloat("Peak threshold##snd", ref thr, 3f, 40f, "%.0f"))
        {
            snd.Threshold = thr / 100f;
            this.MarkDirty();
        }

        rebuild |= ImGui.IsItemDeactivatedAfterEdit();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How far a peak must stand out from the sound around it to count.\nHigher: only the clearest peaks. Lower: smaller ones too.");

        ImGui.SetNextItemWidth(200 * scale);
        var win = snd.PeakWindow * 100f;
        if (ImGui.SliderFloat("Peak loudness window##snd", ref win, 5f, 60f, "%.0f%%"))
        {
            snd.PeakWindow = win / 100f;
            this.MarkDirty();
        }

        rebuild |= ImGui.IsItemDeactivatedAfterEdit();
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Vibration made from peaks only uses peaks about as loud as the skill's loudest one.\n" +
                             "20%: a peak must be at least 80% as loud. Quiet echoes and side ripples are left out.");
        }

        var fromSound = snd.Enabled;
        if (ImGui.Checkbox("Build patterns from sound bumps instead of hits##snd", ref fromSound))
        {
            snd.Enabled = fromSound;
            this.MarkDirty();
            rebuild = true;
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Off: one pulse per hit of the animation, strength from the sound.\n" +
                             "On: each clear bump in the skill's sound becomes a solid block with a short fade-out;\n" +
                             "the sound's background doesn't vibrate.");
        }

        using (ImRaii.Disabled(!snd.Enabled))
        {
            ImGui.SetNextItemWidth(200 * scale);
            var tail = snd.FadeTailMs;
            if (ImGui.SliderInt("Block fade-out##snd", ref tail, 0, 300, "%d ms"))
            {
                snd.FadeTailMs = tail;
                this.MarkDirty();
            }

            rebuild |= ImGui.IsItemDeactivatedAfterEdit();
        }

        if (ImGui.Button("Reset sound settings"))
        {
            this.Config.Sound = new SoundTuning();
            this.MarkDirty();
            rebuild = true;
        }

        if (rebuild)
        {
            this.SaveNow();
            this.plugin.Analysis.ReanalyseAll();
        }
    }

    private static readonly (HitSource Source, string Label, string Tip)[] HitCues =
    {
        (HitSource.Flinch, "Target flinches", "The target's hit reaction in the animation (C067). The most reliable marker of an impact."),
        (HitSource.TargetSound, "Hit sound on the target", "A sound played on the target (..._t.scd, ..._mt.scd)."),
        (HitSource.TargetVfx, "Effect on the target", "An effect spawned on the target (..._t0p.avfx). Often starts before or after the real impact."),
        (HitSource.ExtraStrike, "Follow-up strike sound", "Sounds like ..._add.scd: often the only marker of a skill's last strike."),
        (HitSource.BuiltInVibration, "Built-in vibration (mods)", "Vibration entries (C120) a mod placed on the hits."),
        (HitSource.MultiHit, "MultiHit table", "Per-hit timings from the MultiHit plugin's config, made for specific mods."),
        (HitSource.FlyText, "Damage number", "When the damage number pops. A weak cue: it's often a few frames off the visible impact."),
        (HitSource.CasterVfx, "Effect beats (buffs, heals)", "For skills that don't hit a target: the beats of the caster's effects. A weak cue."),
    };

    /// <summary>The hit rules in words.</summary>
    private string RuleText()
    {
        var h = this.Config.Hits;
        var names = HitCues.Where(c => h.Uses(c.Source)).Select(c => c.Label.ToLowerInvariant()).ToList();
        var list = names.Count == 0 ? "(no cues chosen)" : string.Join(", ", names);
        return h.Match switch
        {
            HitMatch.AtLeast => $"at least {h.MinMatches} of: {list}, within {h.WindowFrames} frames",
            HitMatch.All => $"all of: {list}, within {h.WindowFrames} frames",
            _ => $"any of: {list}",
        };
    }

    private void DrawHitDetection(float scale)
    {
        var h = this.Config.Hits;
        var rebuild = false;

        using (ImRaii.PushColor(ImGuiCol.Text, Dim))
            ImGui.TextWrapped("Which cues in the animation make a hit, and how many must meet at the same moment. Skills without any hit markers use the peaks in their sound as hits (Sound > Threshold sets how clear a peak must be).");

        foreach (var (source, label, tip) in HitCues)
        {
            var on = (h.Sources & source) != 0;
            if (ImGui.Checkbox($"{label}##cue{(int)source}", ref on))
            {
                h.Sources = on ? h.Sources | source : h.Sources & ~source;
                rebuild = true;
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(tip);

            if (source == HitSource.MultiHit)
            {
                ImGui.SameLine();
                using var c = ImRaii.PushColor(ImGuiCol.Text, Dim);
                var mh = this.plugin.MultiHit;
                mh.Refresh();
                ImGui.TextUnformatted(mh.Available ? $"({mh.Count} skills found)" : "(MultiHit not installed)");
            }
        }

        ImGui.SetNextItemWidth(260 * scale);
        var modes = new[] { "any one of these", "at least N at the same moment", "all of these at the same moment" };
        var mode = (int)h.Match;
        using (var combo = ImRaii.Combo("A hit needs##hits", modes[Math.Clamp(mode, 0, 2)]))
        {
            if (combo)
            {
                for (var i = 0; i < modes.Length; i++)
                {
                    if (ImGui.Selectable(modes[i], i == mode))
                    {
                        h.Match = (HitMatch)i;
                        rebuild = true;
                    }
                }
            }
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("\"All\" and \"at least N\" only count cue types the skill's animation actually has,\n" +
                             "so a skill without built-in vibration or a MultiHit table isn't silenced by them.");
        }

        if (h.Match == HitMatch.AtLeast)
        {
            var chosen = Math.Max(1, System.Numerics.BitOperations.PopCount((uint)h.EffectiveSources));
            ImGui.SetNextItemWidth(160 * scale);
            var n = Math.Clamp(h.MinMatches, 1, chosen);
            if (ImGui.SliderInt("N##hits", ref n, 1, chosen))
            {
                h.MinMatches = n;
                this.MarkDirty();
            }

            rebuild |= ImGui.IsItemDeactivatedAfterEdit();
        }

        ImGui.SetNextItemWidth(160 * scale);
        var win = h.WindowFrames;
        if (ImGui.SliderInt("Same moment within##hits", ref win, 1, 8, $"%d frames ({win * 33} ms)"))
        {
            h.WindowFrames = win;
            this.MarkDirty();
        }

        rebuild |= ImGui.IsItemDeactivatedAfterEdit();

        if (ImGui.Button("Reset hit detection"))
        {
            this.Config.Hits = new HitDetection();
            rebuild = true;
        }

        using (ImRaii.PushColor(ImGuiCol.Text, Dim))
            ImGui.TextWrapped($"A hit needs {this.RuleText()}.  {this.plugin.Analysis.OriginSummary}");

        if (rebuild)
        {
            this.MarkDirty();
            this.SaveNow();
            this.plugin.Analysis.ReanalyseAll();
        }
    }

    private void DrawLastSkill()
    {
        var plan = this.plugin.LastPlan;
        using var c = ImRaii.PushColor(ImGuiCol.Text, Dim);
        if (plan == null)
        {
            ImGui.TextUnformatted("Use a skill to see what was played for it.");
            return;
        }

        var tl = plan.Timeline;
        ImGui.TextUnformatted($"{this.plugin.Catalog.NameOf(plan.ActionId)} (ID {plan.ActionId}) at {this.plugin.LastPlanTime:HH:mm:ss}");
        ImGui.TextUnformatted($"Mode: {plan.Mode}" + (plan.Mode == SkillMode.BuiltIn ? "  (built-in kept, 0 plugin pulses)" : $"  ({plan.Pulses.Count} pulse(s))"));
        ImGui.TextUnformatted($"Timeline #{tl.TimelineId}: {tl.ResolvedPath ?? tl.GamePath}" + (tl.FromMod ? "  (mod)" : string.Empty));
        ImGui.TextUnformatted($"Hits (frames): {string.Join(", ", plan.Auto.Hits.Select(h => h.Frame))}   from: {plan.Auto.SourceText}   built-in vibration entries: {tl.C120.Count}");
        if (plan.Pulses.Count > 0)
            ImGui.TextUnformatted("Pulses at: " + string.Join(", ", plan.Pulses.Select(p => $"{p.DelayMs} ms")));
        if (this.plugin.LastStart is { } st && plan.Mode is SkillMode.Auto or SkillMode.Custom)
        {
            var gap = st.GapMs == 0 ? "same moment" : st.GapMs > 0 ? $"animation {st.GapMs} ms before the server message" : $"animation {-st.GapMs} ms after the server message";
            ImGui.TextUnformatted($"Started from: {st.Source} ({gap})   animation speed: {st.Speed:0.00}x");
        }
        if (tl.Sound is { Sounds.Count: > 0 } snd)
        {
            ImGui.TextUnformatted("Sounds:");
            foreach (var r in snd.Sounds)
                ImGui.TextUnformatted($"   frame {r.StartFrame,3}  {r.GamePath}{(r.FromMod ? "  (mod)" : string.Empty)}{(r.Decoded ? string.Empty : "  - not found / not readable")}");
        }

        if (tl.Error != null)
            ImGui.TextUnformatted("Note: " + tl.Error);
    }

    private void Check(string label, bool value, Action<bool> set)
    {
        if (ImGui.Checkbox(label, ref value))
        {
            set(value);
            this.MarkDirty();
        }
    }

    private static void StatusLine(string label, bool ok, string state)
    {
        using (ImRaii.PushColor(ImGuiCol.Text, ok ? Good : Bad))
            ImGui.TextUnformatted(ok ? "OK " : "!! ");
        ImGui.SameLine();
        ImGui.TextUnformatted(label);
        ImGui.SameLine();
        using var c = ImRaii.PushColor(ImGuiCol.Text, Dim);
        ImGui.TextUnformatted($"({state})");
    }

    // ---------------------------------------------------------------- help tab

    private static void DrawHelpTab()
    {
        ImGui.TextWrapped("How it works");
        ImGui.Separator();
        ImGui.TextWrapped(
            "After you log in, the plugin analyses the animation of every skill of every job (the one actually loaded, through " +
            "Penumbra if you use mods) and saves a vibration pattern for each skill: one pulse on every hit. Skills that already " +
            "have a pattern are not touched.");
        ImGui.Spacing();
        ImGui.TextWrapped(
            "Hits decide when, the sound decides how strong. You choose which animation cues make a hit and how many must meet at the " +
            "same moment (Settings > Hit detection; MultiHit and 'effect on the target' are off by default). Skills without any hit " +
            "markers use the clear peaks of their whole sound as hits (only peaks about as loud as the loudest one). Each pulse is as strong as the skill's sound is loud at that moment: bass drives the " +
            "strong motor, crisp sounds the weak one. The last hit is always the strongest. Optionally (Settings > Sound), patterns can be " +
            "built from the sound's bumps instead.");
        ImGui.Spacing();
        ImGui.TextWrapped(
            "The vibration goes through the game's own controller function, so it works with every controller the game supports and " +
            "follows the game's vibration setting. Animations that already vibrate on their own are left alone unless you turn on " +
            "\"Replace built-in vibration\" in Settings.");
        ImGui.Spacing();
        ImGui.TextWrapped(
            "Patterns start when the animation actually starts on your character and follow its playback speed. Generated patterns " +
            "are rebuilt automatically when a skill's animation changes (another mod or option).");
        ImGui.Spacing();
        ImGui.TextWrapped("Editing");
        ImGui.Separator();
        ImGui.TextWrapped(
            "In the Skills tab you can switch single skills off, test them, and edit any pattern. An edited pattern is yours and " +
            "is never changed by re-analysis. The timeline works like clips on DAW tracks: the Strong and Weak motors are two tracks, " +
            "and every hit of the animation is a numbered waypoint.");
        ImGui.BulletText("Drag a clip to move it. Drag its left edge to trim the start and its right edge to change the length.");
        ImGui.BulletText("Drag the line on top of a clip to set that motor's level. Hold Shift to set both motors.");
        ImGui.BulletText("Clips snap to hits, other clips and animation frames. Alt: no snapping. Ctrl: 1 ms steps.");
        ImGui.BulletText("Click a waypoint to add a clip on that hit, or to move the selected clips there. Double-click to add a clip.");
        ImGui.BulletText("Ctrl-click or drag a box to select. Del, Ctrl+D, Ctrl+C / Ctrl+V, Ctrl+A, Ctrl+Z / Ctrl+Y.");
        ImGui.BulletText("Right-click a clip to change its shape. Ctrl + mouse wheel zooms, the wheel scrolls.");
    }
}
