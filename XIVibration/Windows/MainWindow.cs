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
            r.Origin = RuleOrigin.User;
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
            this.plugin.Engine.StopAll();
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
        if (this.Config.Rules.TryGetValue(id, out var rule))
        {
            rule.Enabled = on;
        }
        else
        {
            if (on)
            {
                this.Config.AutoDisabled.Remove(id);
                if (!this.Config.AutoEnabled)
                    this.Config.AutoEnabled = true;
            }
            else
            {
                this.Config.AutoDisabled.Add(id);
            }
        }

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
        var on = !this.Config.AutoDisabled.Contains(id);
        if (ImGui.Checkbox("Vibrate this skill", ref on))
            this.SetSkillOn(id, on);

        ImGui.SameLine();
        var builtIn = plan.Auto.HasBuiltIn && !this.Config.ReplaceModVibration;
        var preview = builtIn ? plan.Auto.BuiltInPreview : plan.Pulses;
        using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.2f, 0.5f, 0.25f, 1f)))
        {
            if (ImGui.Button("  Test  "))
                this.Test(preview, builtIn ? 1f : plan.Scale);
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
                ImGui.TextWrapped("Automatic: follows the skill's sounds. Each clear bump in the sound is a solid block at the sound's level with a short fade-out " +
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
        this.Config.Rules[plan.ActionId] = rule;
        this.Config.AutoDisabled.Remove(plan.ActionId);
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
                this.Test(plan.Pulses, plan.Scale);
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

        // Anchor
        ImGui.SetNextItemWidth(200 * scale);
        var firstHit = plan.Auto.Hits.Count > 0 ? (int)MathF.Round(plan.Auto.Hits[0].Frame * TmbParser.MsPerFrame) : 0;
        using (var combo = ImRaii.Combo("Delays count from", rule.Anchor == PulseAnchor.FirstHit ? "the first hit" : "the animation start"))
        {
            if (combo)
            {
                if (ImGui.Selectable("the first hit", rule.Anchor == PulseAnchor.FirstHit) && rule.Anchor != PulseAnchor.FirstHit)
                    this.SetAnchor(rule, PulseAnchor.FirstHit, firstHit);
                if (ImGui.Selectable("the animation start", rule.Anchor == PulseAnchor.AnimationStart) && rule.Anchor != PulseAnchor.AnimationStart)
                    this.SetAnchor(rule, PulseAnchor.AnimationStart, firstHit);
            }
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"First hit of this skill is at {firstHit} ms. Counting from it keeps presets on the hit.");

        ImGui.Spacing();
        this.BeginUndoSection(rule);
        this.DrawPatternTools(rule);
        this.EndUndoSection(rule);
        var offset = rule.Anchor == PulseAnchor.FirstHit ? firstHit : 0;
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
            Edited = this.MarkEdited,
            PlayheadMs = this.PlayheadMs,
        });
        ImGui.Spacing();
        this.BeginUndoSection(rule);
        this.DrawPulseTable(rule, offset);
        this.EndUndoSection(rule);
    }

    /// <summary>Switch anchor and shift delays so the pattern stays where it was.</summary>
    private void SetAnchor(ActionRule rule, PulseAnchor anchor, int firstHitMs)
    {
        var shift = anchor == PulseAnchor.FirstHit ? -firstHitMs : firstHitMs;
        foreach (var p in rule.Pulses)
            p.DelayMs = anchor == PulseAnchor.FirstHit ? Math.Max(-firstHitMs, p.DelayMs + shift) : Math.Max(0, p.DelayMs + shift);
        rule.Anchor = anchor;
        this.MarkEdited();
    }

    private void Test(IReadOnlyList<VibrationPulse> pulses, float strength)
    {
        this.plugin.Engine.StopAll();
        this.plugin.Engine.Play(pulses, strength * this.Config.MasterStrength, ignoreGates: true);
        this.testStartMs = Clock.NowMs;
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
                        rule.Anchor = PulseAnchor.FirstHit;
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
        if (ImGui.Button("Sort by time"))
        {
            rule.Pulses.Sort((a, b) => a.DelayMs.CompareTo(b.DelayMs));
            this.MarkEdited();
        }

        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Text, Dim))
            ImGui.TextUnformatted("Strong = left, heavy motor. Weak = right, light buzz.");

        if (rule.Pulses.Count == 0)
        {
            using var c = ImRaii.PushColor(ImGuiCol.Text, Dim);
            ImGui.TextUnformatted("No pulses. This skill won't vibrate.");
            return;
        }

        using var table = ImRaii.Table("##pulses", 7, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY);
        if (!table)
            return;

        ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 22 * scale);
        ImGui.TableSetupColumn("Delay (ms)", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Length (ms)", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Strong %", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("Weak %", ImGuiTableColumnFlags.WidthStretch, 1.2f);
        ImGui.TableSetupColumn("Shape", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn(string.Empty, ImGuiTableColumnFlags.WidthFixed, 92 * scale);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        int? remove = null, moveUp = null, moveDown = null, duplicate = null;
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

            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1);
            var delay = p.DelayMs;
            if (ImGui.DragInt("##delay", ref delay, 5f, -offset, 10000, "%d"))
            {
                p.DelayMs = Math.Clamp(delay, -offset, 10000);
                this.MarkEditedContinuous();
            }

            if (ImGui.IsItemActivated())
                this.timeline.Snapshot(rule.ActionId, this.undoBefore);

            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1);
            var dur = p.DurationMs;
            if (ImGui.DragInt("##dur", ref dur, 5f, 16, 5000, "%d"))
            {
                p.DurationMs = Math.Clamp(dur, 16, 5000);
                this.MarkEditedContinuous();
            }

            if (ImGui.IsItemActivated())
                this.timeline.Snapshot(rule.ActionId, this.undoBefore);

            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1);
            var strong = p.Strong;
            using (ImRaii.PushColor(ImGuiCol.SliderGrab, StrongColor))
            {
                if (ImGui.SliderInt("##strong", ref strong, 0, 100, "%d%%"))
                {
                    p.Strong = strong;
                    this.MarkEditedContinuous();
                }

            if (ImGui.IsItemActivated())
                this.timeline.Snapshot(rule.ActionId, this.undoBefore);
            }

            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1);
            var weak = p.Weak;
            using (ImRaii.PushColor(ImGuiCol.SliderGrab, WeakColor))
            {
                if (ImGui.SliderInt("##weak", ref weak, 0, 100, "%d%%"))
                {
                    p.Weak = weak;
                    this.MarkEditedContinuous();
                }

            if (ImGui.IsItemActivated())
                this.timeline.Snapshot(rule.ActionId, this.undoBefore);
            }

            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1);
            using (var combo = ImRaii.Combo("##shape", ShapeName(p.Shape)))
            {
                if (combo)
                {
                    foreach (var s in Enum.GetValues<PulseShape>())
                    {
                        if (ImGui.Selectable(ShapeName(s), s == p.Shape))
                        {
                            p.Shape = s;
                            this.MarkEdited();
                        }
                    }
                }
            }

            ImGui.TableNextColumn();
            if (ImGuiComponents.IconButton("##up", FontAwesomeIcon.ArrowUp) && i > 0)
                moveUp = i;
            ImGui.SameLine(0, 2 * scale);
            if (ImGuiComponents.IconButton("##down", FontAwesomeIcon.ArrowDown) && i < rule.Pulses.Count - 1)
                moveDown = i;
            ImGui.SameLine(0, 2 * scale);
            if (ImGuiComponents.IconButton("##dup", FontAwesomeIcon.Copy))
                duplicate = i;
            ImGui.SameLine(0, 2 * scale);
            if (ImGuiComponents.IconButton("##del", FontAwesomeIcon.Trash))
                remove = i;
        }

        if (moveUp is { } u)
        {
            (rule.Pulses[u - 1], rule.Pulses[u]) = (rule.Pulses[u], rule.Pulses[u - 1]);
            this.MarkEdited();
        }

        if (moveDown is { } d)
        {
            (rule.Pulses[d + 1], rule.Pulses[d]) = (rule.Pulses[d], rule.Pulses[d + 1]);
            this.MarkEdited();
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

    private void DrawSettingsTab()
    {
        var scale = ImGuiHelpers.GlobalScale;

        this.Check("Follow the game's \"Enable vibration\" setting", this.Config.RespectGameSetting, v => this.Config.RespectGameSetting = v);
        this.Check("Only vibrate in combat", this.Config.OnlyInCombat, v => this.Config.OnlyInCombat = v);
        this.Check("Only my character's animations may vibrate", this.Config.OnlyMyCharacter, v => this.Config.OnlyMyCharacter = v);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Mods apply to every character's animations, so a mod with built-in vibration also makes the game\n" +
                             "vibrate when other players or NPCs near you use that skill. With this on, the game's own vibration\n" +
                             "only gets through while one of your skills is playing.");
        }
        this.Check("A new skill cuts off the previous skill's vibration", this.Config.CancelPreviousOnNewAction, v => this.Config.CancelPreviousOnNewAction = v);

        ImGui.Spacing();
        ImGui.TextUnformatted("Automatic vibration");
        ImGui.Separator();
        this.Check("Vibrate every skill automatically, on its hits", this.Config.AutoEnabled, v => this.Config.AutoEnabled = v);

        this.Check("Use hit timings from the MultiHit plugin", this.Config.UseMultiHit, v => this.Config.UseMultiHit = v);
        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Text, Dim))
        {
            var mh = this.plugin.MultiHit;
            mh.Refresh();
            ImGui.TextUnformatted(mh.Available ? $"({mh.Count} skills found)" : "(MultiHit not installed)");
        }

        this.Check("Replace built-in vibration from other mods", this.Config.ReplaceModVibration, v => this.Config.ReplaceModVibration = v);
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Off (default): skills whose animation already vibrates keep that vibration, and the plugin adds nothing.\n" +
                             "On: the plugin mutes that vibration while the skill plays and uses its own pattern, keeping the same hit timing.");
        }

        ImGui.TextUnformatted("Strength by skill type");
        var s = this.Config.AutoStrength;
        this.Strength("Weaponskills", s.Weaponskill, v => s.Weaponskill = v, scale);
        this.Strength("Spells", s.Spell, v => s.Spell = v, scale);
        this.Strength("Abilities", s.Ability, v => s.Ability = v, scale);
        this.Strength("Buffs, heals, movement", s.Support, v => s.Support = v, scale);
        this.Strength("Limit breaks", s.LimitBreak, v => s.LimitBreak = v, scale);

        ImGui.Spacing();
        ImGui.TextUnformatted("Sound");
        using (ImRaii.PushColor(ImGuiCol.Text, Dim))
            ImGui.TextWrapped("A skill's sound has a loud background (reverb, held tones) with louder bumps on top: the hits. Each bump becomes a solid block at the sound's level with a short fade-out; the background doesn't vibrate. Patterns rebuild when you let go of a slider.");
        var snd = this.Config.Sound;
        var rebuild = false;

        ImGui.SetNextItemWidth(200 * scale);
        var thr = snd.Threshold * 100f;
        if (ImGui.SliderFloat("Threshold##snd", ref thr, 3f, 40f, "%.0f"))
        {
            snd.Threshold = thr / 100f;
            this.MarkDirty();
        }

        rebuild |= ImGui.IsItemDeactivatedAfterEdit();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("How far a sound must rise above its own background to vibrate.\nHigher: only the clearest hits. Lower: smaller bumps too.");

        ImGui.SetNextItemWidth(200 * scale);
        var tail = snd.FadeTailMs;
        if (ImGui.SliderInt("Fade-out##snd", ref tail, 0, 300, "%d ms"))
        {
            snd.FadeTailMs = tail;
            this.MarkDirty();
        }

        rebuild |= ImGui.IsItemDeactivatedAfterEdit();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Length of the fade-out after each block.");

        ImGui.SetNextItemWidth(200 * scale);
        var join = snd.JoinGapMs;
        if (ImGui.SliderInt("Join bumps closer than##snd", ref join, 0, 300, "%d ms"))
        {
            snd.JoinGapMs = join;
            this.MarkDirty();
        }

        rebuild |= ImGui.IsItemDeactivatedAfterEdit();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Bumps closer together than this become one block.\nHigher: fewer, longer blocks. Lower: more separate blocks.");

        var silent = snd.PulseSilentHits;
        if (ImGui.Checkbox("Also pulse on hits without a sound bump##snd", ref silent))
        {
            snd.PulseSilentHits = silent;
            this.MarkDirty();
            rebuild = true;
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Hits found in the animation (target flinches, MultiHit tables) where the sound has no bump get a pulse of their own.");

        if (ImGui.Button("Reset sound settings"))
        {
            snd.Threshold = SoundTuning.DefaultThreshold;
            snd.FadeTailMs = SoundTuning.DefaultFadeTailMs;
            snd.JoinGapMs = SoundTuning.DefaultJoinGapMs;
            snd.PulseSilentHits = false;
            this.MarkDirty();
            rebuild = true;
        }

        if (rebuild)
        {
            this.SaveNow();
            this.plugin.Analysis.ReanalyseAll();
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Skill analysis");
        ImGui.Separator();
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

        ImGui.Spacing();
        ImGui.TextUnformatted("Motor test");
        ImGui.Separator();
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
        ImGui.Separator();
        var e = this.plugin.Engine;
        StatusLine("Gamepad device", e.PadFound, e.PadFound ? "found" : "not found");
        StatusLine("Vibration hook", e.HookActive, e.HookActive ? "active" : e.LastError ?? "not active, calling it directly");
        StatusLine("Skill hook", this.plugin.Watcher.HookActive, this.plugin.Watcher.HookActive ? "active" : this.plugin.Watcher.LastError ?? "failed");
        StatusLine("Vibration from other characters", true, this.Config.OnlyMyCharacter
            ? $"blocked {this.plugin.Engine.BlockedFromOthers} time(s) this session"
            : "allowed (\"Only my character's animations may vibrate\" is off)");
        StatusLine("Animation timing hook", this.plugin.Sync.HookActive, this.plugin.Sync.HookActive ? "active, patterns start with the animation" : (this.plugin.Sync.LastError ?? "failed") + ", timing from the server message");
        var gv = e.GameVibrationEnabled;
        StatusLine("Game option: Enable vibration", gv != false, gv == null ? "could not read" : gv.Value ? "on" : "off");
        var pen = this.plugin.Timelines.PenumbraConnected;
        StatusLine("Penumbra (reads modded animations)", pen != false, pen == null ? "not checked yet" : pen.Value ? "connected" : "not found, using unmodded animations");

        ImGui.Spacing();
        ImGui.TextUnformatted("Last skill");
        ImGui.Separator();
        this.DrawLastSkill();

        ImGui.Spacing();
        using (ImRaii.PushColor(ImGuiCol.Text, Dim))
            ImGui.TextWrapped("Commands: /xivib (open), /xivib on | off | toggle | test | stop");
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

    private void Strength(string label, float value, Action<float> set, float scale)
    {
        ImGui.SetNextItemWidth(200 * scale);
        var v = value * 100f;
        if (ImGui.SliderFloat($"{label}##auto", ref v, 0f, 200f, "%.0f%%"))
        {
            set(v / 100f);
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
            "Sounds come first: every sound the skill plays is read. Each clear bump in the sound becomes a solid block at the sound's " +
            "level with a short fade-out; the sound's background (reverb, held tones) doesn't vibrate (tune it under Settings > Sound). Bass drives the strong motor, crisp sounds the weak one. " +
            "Skills without readable sounds get pulses from the animation's hits (the target's hit reaction, effects on the target, MultiHit tables); " +
            "buffs and heals without sounds follow their effect beats. The numbered waypoints show the animation's hits either way.");
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
