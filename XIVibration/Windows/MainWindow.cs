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

namespace XIVibration.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private static readonly Vector4 Dim = new(0.62f, 0.62f, 0.62f, 1f);
    private static readonly Vector4 Good = new(0.45f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 Bad = new(0.95f, 0.45f, 0.4f, 1f);
    private static readonly Vector4 StrongColor = new(0.95f, 0.55f, 0.25f, 1f);
    private static readonly Vector4 WeakColor = new(0.35f, 0.7f, 1f, 1f);

    private readonly Plugin plugin;
    private readonly FileDialogManager fileDialog = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();

    private uint? selectedId;
    private bool fallbackSelected;

    private string addSearch = string.Empty;
    private bool addCurrentJobOnly = true;
    private bool addIncludePvP;
    private List<ActionInfo>? addResults;
    private string addResultsKey = string.Empty;

    private string importPath = string.Empty;
    private bool importAppend;
    private bool showImport;
    private string? importMessage;

    private List<VibrationPulse>? clipboard;
    private long testStartMs = -1;
    private long testLengthMs;

    private bool dirty;
    private long lastEditMs;

    public MainWindow(Plugin plugin)
        : base("XIVibration###XIVibrationMain")
    {
        this.plugin = plugin;
        this.SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(760, 480),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        this.Size = new Vector2(980, 640);
        this.SizeCondition = ImGuiCond.FirstUseEver;
    }

    private Configuration Config => this.plugin.Config;

    public override void OnClose() => this.SaveNow();

    public void Dispose() => this.SaveNow();

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
        var scale = ImGuiHelpers.GlobalScale;
        var listWidth = 290 * scale;

        using (var left = ImRaii.Child("##left", new Vector2(listWidth, 0), true))
        {
            if (left)
                this.DrawRuleList();
        }

        ImGui.SameLine();

        using (var right = ImRaii.Child("##right", Vector2.Zero, true))
        {
            if (right)
                this.DrawEditor();
        }
    }

    private void DrawRuleList()
    {
        var scale = ImGuiHelpers.GlobalScale;

        if (ImGui.Button("Add skill...", new Vector2(-1, 0)))
        {
            this.addResults = null;
            ImGui.OpenPopup("##addskill");
        }

        this.DrawAddSkillPopup();

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Recently used", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var recent = this.plugin.Watcher.Recent;
            if (recent.Count == 0)
            {
                using var c = ImRaii.PushColor(ImGuiCol.Text, Dim);
                ImGui.TextWrapped("Use a skill in game and it shows up here. Click it to add vibration.");
            }

            foreach (var id in recent.Take(8).ToArray())
            {
                var has = this.Config.Rules.ContainsKey(id);
                using var pid = ImRaii.PushId((int)id + 1_000_000);
                if (this.ActionRow(id, !this.fallbackSelected && this.selectedId == id, has ? "(configured)" : "+ click to add"))
                {
                    if (!has)
                        this.AddRule(id);
                    this.Select(id);
                }
            }
        }

        ImGui.Spacing();
        ImGui.TextUnformatted("Skills with vibration");
        ImGui.Separator();

        // Fallback rule
        {
            var useFallback = this.Config.UseFallback;
            if (ImGui.Checkbox("##fallbackOn", ref useFallback))
            {
                this.Config.UseFallback = useFallback;
                this.MarkDirty();
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Turn the fallback pattern on or off");
            ImGui.SameLine();
            if (ImGui.Selectable("All other skills (fallback)", this.fallbackSelected))
            {
                this.fallbackSelected = true;
                this.selectedId = null;
            }
        }

        if (this.Config.Rules.Count == 0)
        {
            using var c = ImRaii.PushColor(ImGuiCol.Text, Dim);
            ImGui.TextWrapped("No skills yet. Use \"Add skill...\" or click a recently used skill.");
            return;
        }

        foreach (var rule in this.Config.Rules.Values.OrderBy(r => this.plugin.Catalog.NameOf(r.ActionId)).ToArray())
        {
            using var pid = ImRaii.PushId((int)rule.ActionId);
            var on = rule.Enabled;
            if (ImGui.Checkbox("##on", ref on))
            {
                rule.Enabled = on;
                this.MarkDirty();
            }

            ImGui.SameLine();
            var info = this.plugin.Catalog.Get(rule.ActionId);
            if (this.ActionRow(rule.ActionId, !this.fallbackSelected && this.selectedId == rule.ActionId, info?.JobAbbr ?? string.Empty, 22 * scale))
                this.Select(rule.ActionId);
        }
    }

    private bool ActionRow(uint id, bool selected, string suffix, float iconSize = 0)
    {
        if (iconSize <= 0)
            iconSize = 24 * ImGuiHelpers.GlobalScale;
        var info = this.plugin.Catalog.Get(id);
        var start = ImGui.GetCursorPos();
        var clicked = ImGui.Selectable("##row", selected, ImGuiSelectableFlags.None, new Vector2(0, iconSize));
        ImGui.SetCursorPos(start);
        DrawIcon(info?.Icon ?? 0, iconSize);
        ImGui.SameLine();
        ImGui.SetCursorPosY(start.Y + (iconSize - ImGui.GetTextLineHeight()) / 2f);
        ImGui.TextUnformatted(this.plugin.Catalog.NameOf(id));
        if (suffix.Length > 0)
        {
            ImGui.SameLine();
            using var c = ImRaii.PushColor(ImGuiCol.Text, Dim);
            ImGui.TextUnformatted(suffix);
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

    private void DrawAddSkillPopup()
    {
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.SetNextWindowSize(new Vector2(420, 460) * scale);
        using var popup = ImRaii.Popup("##addskill");
        if (!popup)
            return;

        if (ImGui.IsWindowAppearing())
            ImGui.SetKeyboardFocusHere();
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##search", "Search by name, job (DRG) or ID...", ref this.addSearch, 64);
        ImGui.Checkbox("Current job only", ref this.addCurrentJobOnly);
        ImGui.SameLine();
        ImGui.Checkbox("Include PvP", ref this.addIncludePvP);

        var job = ActionCatalog.CurrentJob();
        var key = $"{this.addSearch}|{this.addCurrentJobOnly}|{this.addIncludePvP}|{job.Job}";
        if (this.addResults == null || key != this.addResultsKey)
        {
            this.addResultsKey = key;
            this.addResults = this.plugin.Catalog.PlayerActions
                .Where(a => (this.addIncludePvP || !a.IsPvP)
                            && (!this.addCurrentJobOnly || ActionCatalog.MatchesJob(a, job))
                            && ActionCatalog.MatchesSearch(a, this.addSearch.Trim()))
                .Take(300)
                .ToList();
        }

        ImGui.Separator();
        using var list = ImRaii.Child("##results", Vector2.Zero);
        if (!list)
            return;

        if (this.addResults.Count == 0)
        {
            using var c = ImRaii.PushColor(ImGuiCol.Text, Dim);
            ImGui.TextUnformatted("No matching skills.");
        }

        foreach (var a in this.addResults)
        {
            using var pid = ImRaii.PushId((int)a.Id);
            var suffix = $"{a.JobAbbr} Lv{a.Level}" + (a.IsPvP ? " PvP" : string.Empty) + (this.Config.Rules.ContainsKey(a.Id) ? "  (added)" : string.Empty);
            if (this.ActionRow(a.Id, false, suffix))
            {
                if (!this.Config.Rules.ContainsKey(a.Id))
                    this.AddRule(a.Id);
                this.Select(a.Id);
                ImGui.CloseCurrentPopup();
            }
        }
    }

    private void AddRule(uint id)
    {
        this.Config.Rules[id] = new ActionRule { ActionId = id, Pulses = Presets.Copy(Presets.All[1]) };
        this.MarkDirty();
    }

    private void Select(uint id)
    {
        this.selectedId = id;
        this.fallbackSelected = false;
        this.importMessage = null;
    }

    // ---------------------------------------------------------------- editor

    private void DrawEditor()
    {
        ActionRule? rule = null;
        if (this.fallbackSelected)
            rule = this.Config.Fallback;
        else if (this.selectedId is { } id && this.Config.Rules.TryGetValue(id, out var r))
            rule = r;

        if (rule == null)
        {
            using var c = ImRaii.PushColor(ImGuiCol.Text, Dim);
            ImGui.TextWrapped("Pick a skill on the left, or add one, to design its vibration.");
            return;
        }

        var scale = ImGuiHelpers.GlobalScale;

        // Header
        if (this.fallbackSelected)
        {
            ImGui.TextUnformatted("All other skills (fallback)");
            using var c = ImRaii.PushColor(ImGuiCol.Text, Dim);
            ImGui.TextWrapped("Plays for every skill you use that has no rule of its own. Turn it on with the checkbox in the list.");
        }
        else
        {
            var info = this.plugin.Catalog.Get(rule.ActionId);
            DrawIcon(info?.Icon ?? 0, 40 * scale);
            ImGui.SameLine();
            using (ImRaii.Group())
            {
                ImGui.TextUnformatted(this.plugin.Catalog.NameOf(rule.ActionId));
                using var c = ImRaii.PushColor(ImGuiCol.Text, Dim);
                ImGui.TextUnformatted($"{info?.JobAbbr} Lv{info?.Level}  ·  ID {rule.ActionId}");
                if (!string.IsNullOrEmpty(info?.Timeline))
                {
                    ImGui.TextUnformatted(info.Timeline);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Animation timeline (.tmb) of this skill. Click to use it as the import path.");
                    if (ImGui.IsItemClicked())
                    {
                        this.importPath = info.Timeline;
                        this.showImport = true;
                    }
                }
            }

            var enabled = rule.Enabled;
            if (ImGui.Checkbox("Enabled##rule", ref enabled))
            {
                rule.Enabled = enabled;
                this.MarkDirty();
            }

            ImGui.SameLine();
        }

        ImGui.SetNextItemWidth(160 * scale);
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
                this.Test(rule);
        }

        if (!this.fallbackSelected)
        {
            ImGui.SameLine();
            var ctrl = ImGui.GetIO().KeyCtrl;
            using (ImRaii.Disabled(!ctrl))
            {
                if (ImGui.Button("Remove"))
                {
                    this.Config.Rules.Remove(rule.ActionId);
                    this.selectedId = null;
                    this.MarkDirty();
                    return;
                }
            }

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("Hold Ctrl and click to remove this skill.");
        }

        ImGui.Spacing();
        this.DrawPatternTools(rule);
        ImGui.Spacing();
        this.DrawTimeline(rule);
        ImGui.Spacing();
        this.DrawPulseTable(rule);
    }

    private void Test(ActionRule rule)
    {
        this.plugin.Engine.StopAll();
        this.plugin.Engine.Play(rule.Pulses, rule.Strength * this.Config.MasterStrength, ignoreGates: true);
        this.testStartMs = this.clock.ElapsedMilliseconds;
        this.testLengthMs = rule.Pulses.Count == 0 ? 0 : rule.Pulses.Max(p => p.DelayMs + p.DurationMs);
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
                        this.MarkDirty();
                    }

                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(p.Description);
                }
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Copy"))
            this.clipboard = rule.Pulses.Select(p => p.Clone()).ToList();
        ImGui.SameLine();
        using (ImRaii.Disabled(this.clipboard == null))
        {
            if (ImGui.Button("Paste") && this.clipboard != null)
            {
                rule.Pulses = this.clipboard.Select(p => p.Clone()).ToList();
                this.MarkDirty();
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

        ImGui.TextWrapped("Reads the C120 \"Controller Vibration\" entries from an animation timeline and turns them into pulses. " +
                          "Use a file from a mod folder (for example the Classic DRG mod) or a game path like chara/action/ws/bt_2sp_emp/ws_s20.tmb.");
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
                    this.importMessage = "No C120 entries in this file. This animation has no vibration of its own.";
                }
                else
                {
                    var pulses = TmbImport.ToPulses(entries);
                    if (this.importAppend)
                        rule.Pulses.AddRange(pulses);
                    else
                        rule.Pulses = pulses;
                    rule.Pulses.Sort((a, b) => a.DelayMs.CompareTo(b.DelayMs));
                    this.MarkDirty();
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

    private void DrawTimeline(ActionRule rule)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var dl = ImGui.GetWindowDrawList();
        var width = ImGui.GetContentRegionAvail().X;
        var laneH = 34 * scale;
        var labelH = ImGui.GetTextLineHeight();
        var height = laneH * 2 + 6 * scale + labelH;
        var origin = ImGui.GetCursorScreenPos();

        var total = rule.Pulses.Count == 0 ? 0 : rule.Pulses.Max(p => p.DelayMs + p.DurationMs);
        var span = Math.Max(500, (int)(Math.Ceiling(total / 250.0) * 250));
        float X(float ms) => origin.X + width * ms / span;

        dl.AddRectFilled(origin, origin + new Vector2(width, laneH * 2 + 6 * scale), ImGui.GetColorU32(ImGuiCol.FrameBg), 4f);

        // grid every 100 ms, labels every 500 ms
        var gridCol = ImGui.GetColorU32(new Vector4(1, 1, 1, 0.07f));
        var labelCol = ImGui.GetColorU32(Dim);
        for (var ms = 0; ms <= span; ms += 100)
        {
            var x = X(ms);
            dl.AddLine(new Vector2(x, origin.Y), new Vector2(x, origin.Y + laneH * 2 + 6 * scale), gridCol);
            if (ms % 500 == 0)
                dl.AddText(new Vector2(x + 2, origin.Y + laneH * 2 + 6 * scale), labelCol, $"{ms / 1000f:0.0}s");
        }

        var strongTop = origin.Y;
        var weakTop = origin.Y + laneH + 6 * scale;
        var strongCol = ImGui.GetColorU32(StrongColor);
        var weakCol = ImGui.GetColorU32(WeakColor);
        var step = Math.Max(1f, 2f * scale);

        foreach (var p in rule.Pulses)
        {
            if (p.DurationMs <= 0)
                continue;
            var x0 = X(p.DelayMs);
            var x1 = X(p.DelayMs + p.DurationMs);
            for (var x = x0; x < x1; x += step)
            {
                var t = (x - x0) / Math.Max(1f, x1 - x0) * p.DurationMs;
                var env = VibrationEngine.Envelope(p.Shape, t, p.DurationMs) * rule.Strength;
                var hs = Math.Clamp(p.Strong * env / 100f, 0f, 1f) * laneH;
                var hw = Math.Clamp(p.Weak * env / 100f, 0f, 1f) * laneH;
                var xe = Math.Min(x + step, x1);
                if (hs > 0)
                    dl.AddRectFilled(new Vector2(x, strongTop + laneH - hs), new Vector2(xe, strongTop + laneH), strongCol);
                if (hw > 0)
                    dl.AddRectFilled(new Vector2(x, weakTop + laneH - hw), new Vector2(xe, weakTop + laneH), weakCol);
            }
        }

        dl.AddText(new Vector2(origin.X + 4, strongTop + 2), ImGui.GetColorU32(ImGuiCol.Text), "Strong");
        dl.AddText(new Vector2(origin.X + 4, weakTop + 2), ImGui.GetColorU32(ImGuiCol.Text), "Weak");

        // playhead while testing
        if (this.testStartMs >= 0)
        {
            var elapsed = this.clock.ElapsedMilliseconds - this.testStartMs;
            if (elapsed <= Math.Max(this.testLengthMs, 1))
            {
                var x = X(elapsed);
                dl.AddLine(new Vector2(x, origin.Y), new Vector2(x, origin.Y + laneH * 2 + 6 * scale), ImGui.GetColorU32(new Vector4(1, 1, 1, 0.9f)), 2f);
            }
            else
            {
                this.testStartMs = -1;
            }
        }

        ImGui.Dummy(new Vector2(width, height + 2 * scale));
    }

    private void DrawPulseTable(ActionRule rule)
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
            this.MarkDirty();
        }

        ImGui.SameLine();
        if (ImGui.Button("Sort by time"))
        {
            rule.Pulses.Sort((a, b) => a.DelayMs.CompareTo(b.DelayMs));
            this.MarkDirty();
        }

        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Text, Dim))
            ImGui.TextUnformatted("Strong = left, heavy motor. Weak = right, light buzz. Delay counts from when the skill goes off.");

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

            ImGui.TableNextColumn();
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted($"{i + 1}");

            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1);
            var delay = p.DelayMs;
            if (ImGui.DragInt("##delay", ref delay, 5f, 0, 10000, "%d"))
            {
                p.DelayMs = Math.Clamp(delay, 0, 10000);
                this.MarkDirty();
            }

            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1);
            var dur = p.DurationMs;
            if (ImGui.DragInt("##dur", ref dur, 5f, 16, 5000, "%d"))
            {
                p.DurationMs = Math.Clamp(dur, 16, 5000);
                this.MarkDirty();
            }

            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1);
            var strong = p.Strong;
            using (ImRaii.PushColor(ImGuiCol.SliderGrab, StrongColor))
            {
                if (ImGui.SliderInt("##strong", ref strong, 0, 100, "%d%%"))
                {
                    p.Strong = strong;
                    this.MarkDirty();
                }
            }

            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1);
            var weak = p.Weak;
            using (ImRaii.PushColor(ImGuiCol.SliderGrab, WeakColor))
            {
                if (ImGui.SliderInt("##weak", ref weak, 0, 100, "%d%%"))
                {
                    p.Weak = weak;
                    this.MarkDirty();
                }
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
                            this.MarkDirty();
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
            this.MarkDirty();
        }

        if (moveDown is { } d)
        {
            (rule.Pulses[d + 1], rule.Pulses[d]) = (rule.Pulses[d], rule.Pulses[d + 1]);
            this.MarkDirty();
        }

        if (duplicate is { } c2)
        {
            var copy = rule.Pulses[c2].Clone();
            copy.DelayMs += rule.Pulses[c2].DurationMs + 50;
            rule.Pulses.Insert(c2 + 1, copy);
            this.MarkDirty();
        }

        if (remove is { } r)
        {
            rule.Pulses.RemoveAt(r);
            this.MarkDirty();
        }
    }

    private static string ShapeName(PulseShape s) => s switch
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
        var respect = this.Config.RespectGameSetting;
        if (ImGui.Checkbox("Follow the game's \"Enable vibration\" setting", ref respect))
        {
            this.Config.RespectGameSetting = respect;
            this.MarkDirty();
        }

        var combat = this.Config.OnlyInCombat;
        if (ImGui.Checkbox("Only vibrate in combat", ref combat))
        {
            this.Config.OnlyInCombat = combat;
            this.MarkDirty();
        }

        var cancel = this.Config.CancelPreviousOnNewAction;
        if (ImGui.Checkbox("A new skill cuts off the previous skill's vibration", ref cancel))
        {
            this.Config.CancelPreviousOnNewAction = cancel;
            this.MarkDirty();
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
        StatusLine("Gamepad device (InputDeviceManager > PadDevice)", e.PadFound, e.PadFound ? "found" : "not found");
        StatusLine("Vibration hook (PadDeviceInterface::SetVibration)", e.HookActive, e.HookActive ? "active" : e.LastError ?? "not active, calling it directly");
        StatusLine("Action hook (ActionEffectHandler::Receive)", this.plugin.Watcher.HookActive, this.plugin.Watcher.HookActive ? "active" : this.plugin.Watcher.LastError ?? "failed");
        var gv = e.GameVibrationEnabled;
        StatusLine("Game option: Enable vibration", gv != false, gv == null ? "could not read" : gv.Value ? "on" : "off");

        ImGui.Spacing();
        using (ImRaii.PushColor(ImGuiCol.Text, Dim))
            ImGui.TextWrapped("Commands: /xivib (open), /xivib on | off | toggle | test | stop");
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
        ImGui.TextWrapped("Where the Classic DRG mod's vibration comes from");
        ImGui.Separator();
        ImGui.TextWrapped(
            "Every skill animation has a timeline file (.tmb). Timelines hold entries such as C012 (spawn a VFX), C063 (play a sound) and " +
            "C120, \"Controller Vibration\". When the game plays a C120 entry it rumbles the pad. The Classic DRG mod adds C120 entries to its " +
            "skills' .tmb files (33 of them). The Classic Paladin mod has C120 only in the tank limit break, which already vibrates in the " +
            "unmodded game. That is why DRG vibrates and PLD does not. Screen shake (the CameraQuake effector in .avfx files) is " +
            "in both mods, so it is not the cause.");
        ImGui.Spacing();
        ImGui.TextWrapped(
            "This plugin skips the files. It watches for your skills and calls the same function the game uses (PadDevice::SetVibration). " +
            "So any skill can vibrate, with or without mods, and on any controller the game supports. Use \"Import from .tmb...\" to copy " +
            "the exact timing of a mod's C120 entries onto any skill.");
        ImGui.Spacing();
        ImGui.TextWrapped("C120 entry: id, start time (frames at 30 fps), duration (frames), unknown (0), wave type (1 heavy, 3 medium, 2 light; a guess based on how the mod uses them).");
    }
}
