using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using XIVibration.Timeline;

namespace XIVibration.Windows;

/// <summary>
/// Timeline editor that works like clips on DAW tracks. The Strong and Weak motors are two
/// tracks showing the same clips; a clip's height in a track is that motor's level.
/// Hits of the animation are numbered waypoints that clips snap to.
/// </summary>
public sealed class PulseTimelineEditor
{
    public sealed class Input
    {
        /// <summary>Identifies the skill (selection, zoom and undo are kept per skill).</summary>
        public required uint Key { get; init; }

        /// <summary>The list to edit, or null when the pattern is read-only.</summary>
        public List<VibrationPulse>? Pulses { get; init; }

        /// <summary>Shown when <see cref="Pulses"/> is null.</summary>
        public IReadOnlyList<VibrationPulse> Display { get; init; } = Array.Empty<VibrationPulse>();

        /// <summary>Added to each pulse's DelayMs to get its time from the animation start.</summary>
        public int OffsetMs { get; init; }

        public float Strength { get; init; } = 1f;

        public IReadOnlyList<HitPoint> Hits { get; init; } = Array.Empty<HitPoint>();

        /// <summary>The automatic pattern; a clip added on a hit copies the auto pulse there.</summary>
        public IReadOnlyList<VibrationPulse> AutoPulses { get; init; } = Array.Empty<VibrationPulse>();

        /// <summary>Turns a read-only pattern into an editable one (offset 0) and returns its list.</summary>
        public Func<List<VibrationPulse>>? MakeEditable { get; init; }

        public Action? Edited { get; init; }

        public float? PlayheadMs { get; init; }

        /// <summary>The skill's sounds as motor levels (10 ms steps), drawn behind the clips.</summary>
        public Audio.SkillSound? Sound { get; init; }

        /// <summary>Moments with cues that the hit rules left out (drawn faintly).</summary>
        public IReadOnlyList<HitPoint> Rejected { get; init; } = Array.Empty<HitPoint>();

        /// <summary>The hit rules in words, for the tooltip of a left-out moment.</summary>
        public string RuleText { get; init; } = string.Empty;

        /// <summary>Where sounds start and end (ms); clips snap to them.</summary>
        public IReadOnlyList<int> SoundMarks { get; init; } = Array.Empty<int>();
    }

    private IReadOnlyList<int> soundMarks = Array.Empty<int>();

    private enum DragMode
    {
        None,
        Move,
        TrimStart,
        Resize,
        LevelStrong,
        LevelWeak,
        Box,
    }

    private readonly record struct Orig(int Start, int Duration, int Strong, int Weak);

    private static readonly Vector4 StrongColor = new(0.95f, 0.55f, 0.25f, 1f);
    private static readonly Vector4 WeakColor = new(0.35f, 0.7f, 1f, 1f);

    private const float SnapPx = 6f;
    private const int MinDurationMs = 16;
    private const int UndoLimit = 50;

    private readonly Dictionary<uint, (List<List<VibrationPulse>> Undo, List<List<VibrationPulse>> Redo)> history = new();
    private readonly HashSet<VibrationPulse> selected = new(ReferenceEqualityComparer.Instance);
    private List<VibrationPulse>? clipboard;

    private uint key = uint.MaxValue;
    private float zoom = 1f;
    private float scrollMs;
    private bool snap = true;

    private DragMode drag;
    private VibrationPulse? grabbed;
    private Vector2 dragStartMouse;
    private Dictionary<VibrationPulse, Orig> originals = new(ReferenceEqualityComparer.Instance);
    private bool dragChanged;
    private VibrationPulse? menuPulse;

    public bool IsSelected(VibrationPulse p) => this.selected.Contains(p);

    public void Select(VibrationPulse p, bool add)
    {
        if (!add)
            this.selected.Clear();
        this.selected.Add(p);
    }

    /// <summary>Save the current state for undo (call before an edit made outside the timeline).</summary>
    public void Snapshot(uint skill, List<VibrationPulse> pulses)
    {
        if (!this.history.TryGetValue(skill, out var h))
            this.history[skill] = h = (new(), new());
        h.Undo.Add(Configuration.ClonePulses(pulses));
        if (h.Undo.Count > UndoLimit)
            h.Undo.RemoveAt(0);
        h.Redo.Clear();
    }

    public void Draw(Input input)
    {
        var scale = ImGuiHelpers.GlobalScale;
        if (input.Key != this.key)
        {
            this.key = input.Key;
            this.selected.Clear();
            this.zoom = 1f;
            this.scrollMs = 0;
            this.drag = DragMode.None;
        }

        this.soundMarks = input.SoundMarks;
        var list = input.Pulses;
        var shown = (IReadOnlyList<VibrationPulse>?)list ?? input.Display;
        var offset = input.OffsetMs;
        var editable = list != null || input.MakeEditable != null;
        this.selected.RemoveWhere(p => !shown.Contains(p));

        // ---- toolbar
        if (ImGui.Button("Fit"))
        {
            this.zoom = 1f;
            this.scrollMs = 0;
        }

        ImGui.SameLine();
        ImGui.Checkbox("Snap", ref this.snap);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Clip edges snap to hits, to where sounds start and end, to other clips and to animation frames (33 ms).\nHold Alt while dragging to place freely, Ctrl for 1 ms steps.");
        ImGui.SameLine();
        using (ImRaii.PushColor(ImGuiCol.Text, new Vector4(0.62f, 0.62f, 0.62f, 1f)))
        {
            ImGui.TextUnformatted(editable
                ? $"Zoom {this.zoom:0.#}x   ·   drag clips to move, edges to trim/resize, top line for level   ·   Ctrl+wheel zoom   ·   Ctrl+Z undo"
                : "Read only");
        }

        // ---- geometry
        var width = Math.Max(100f, ImGui.GetContentRegionAvail().X);
        var badgeH = 22 * scale;
        var laneH = 46 * scale;
        var laneGap = 6 * scale;
        var axisH = ImGui.GetTextLineHeight() + 4 * scale;
        var totalH = badgeH + laneH * 2 + laneGap + axisH;

        var lastEnd = shown.Count == 0 ? 0 : shown.Max(p => p.DelayMs + offset + p.DurationMs);
        var lastHit = input.Hits.Count == 0 ? 0 : input.Hits.Max(h => (float)h.Ms);
        var fitSpan = Math.Max(500f, (float)(Math.Ceiling((Math.Max(lastEnd, lastHit) + 150) / 250.0) * 250));
        var viewSpan = fitSpan / this.zoom;
        this.scrollMs = Math.Clamp(this.scrollMs, 0, Math.Max(0, fitSpan - viewSpan));

        using var child = ImRaii.Child($"##tl{input.Key}", new Vector2(width, totalH), false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (!child)
            return;

        var origin = ImGui.GetCursorScreenPos();
        var pxPerMs = width / viewSpan;
        float X(float ms) => origin.X + (ms - this.scrollMs) * pxPerMs;
        float Ms(float x) => this.scrollMs + (x - origin.X) / pxPerMs;

        var strongTop = origin.Y + badgeH;
        var weakTop = strongTop + laneH + laneGap;
        var lanesBottom = weakTop + laneH;

        ImGui.InvisibleButton("##canvas", new Vector2(width, totalH), ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        var hovered = ImGui.IsItemHovered();
        var io = ImGui.GetIO();
        var mouse = io.MousePos;
        var dl = ImGui.GetWindowDrawList();

        // ---- zoom and scroll
        if (hovered && io.MouseWheel != 0)
        {
            if (io.KeyCtrl)
            {
                var at = Ms(mouse.X);
                this.zoom = Math.Clamp(this.zoom * MathF.Pow(1.2f, io.MouseWheel), 1f, 40f);
                var newSpan = fitSpan / this.zoom;
                this.scrollMs = at - (mouse.X - origin.X) / (width / newSpan);
            }
            else if (this.zoom > 1f)
            {
                this.scrollMs -= io.MouseWheel * viewSpan * 0.15f;
            }

            viewSpan = fitSpan / this.zoom;
            this.scrollMs = Math.Clamp(this.scrollMs, 0, Math.Max(0, fitSpan - viewSpan));
            pxPerMs = width / viewSpan;
        }

        // ---- opaque background, so nothing behind the editor shows through
        var bgCol = ImGui.GetStyle().Colors[(int)ImGuiCol.WindowBg] with { W = 1f };
        dl.AddRectFilled(origin, origin + new Vector2(width, totalH), ImGui.GetColorU32(bgCol));

        // ---- background and grid
        dl.AddRectFilled(new Vector2(origin.X, strongTop), new Vector2(origin.X + width, strongTop + laneH), ImGui.GetColorU32(ImGuiCol.FrameBg), 4f);
        dl.AddRectFilled(new Vector2(origin.X, weakTop), new Vector2(origin.X + width, lanesBottom), ImGui.GetColorU32(ImGuiCol.FrameBg), 4f);

        var (minor, major) = GridSteps(pxPerMs);
        var minorCol = ImGui.GetColorU32(new Vector4(1, 1, 1, 0.05f));
        var majorCol = ImGui.GetColorU32(new Vector4(1, 1, 1, 0.12f));
        var labelCol = ImGui.GetColorU32(new Vector4(0.62f, 0.62f, 0.62f, 1f));
        for (var ms = MathF.Floor(this.scrollMs / minor) * minor; ms <= this.scrollMs + viewSpan; ms += minor)
        {
            var x = X(ms);
            var isMajor = Math.Abs(ms / major - MathF.Round(ms / major)) < 0.001f;
            dl.AddLine(new Vector2(x, strongTop), new Vector2(x, lanesBottom), isMajor ? majorCol : minorCol);
            if (isMajor)
                dl.AddText(new Vector2(x + 2, lanesBottom + 1), labelCol, major >= 1000 || ms % 1000 == 0 ? $"{ms / 1000f:0.0#}s" : $"{ms:0}ms");
        }

        // ---- the skill's sound, like an audio waveform behind the clips
        if (input.Sound is { Low.Length: > 0 } snd)
        {
            var lowCol = ImGui.GetColorU32(StrongColor with { W = 0.22f });
            var highCol = ImGui.GetColorU32(WeakColor with { W = 0.22f });
            var stepMs = Audio.SoundEnvelope.StepMs;
            var first = Math.Max(0, (int)(this.scrollMs / stepMs));
            var last = Math.Min(snd.Low.Length, (int)((this.scrollMs + viewSpan) / stepMs) + 1);
            for (var i = first; i < last; i++)
            {
                var x0 = X(i * stepMs);
                var x1 = Math.Max(x0 + 1, X((i + 1) * stepMs));
                var hl = snd.Low[i] * (laneH - 2);
                var hh = snd.High[i] * (laneH - 2);
                if (hl > 0.5f)
                    dl.AddRectFilled(new Vector2(x0, strongTop + laneH - 1 - hl), new Vector2(x1, strongTop + laneH - 1), lowCol);
                if (hh > 0.5f)
                    dl.AddRectFilled(new Vector2(x0, lanesBottom - 1 - hh), new Vector2(x1, lanesBottom - 1), highCol);
            }
        }

        // ---- waypoints (behind the clips)
        var hitMs = input.Hits.Select(h => (float)h.Ms).ToList();
        for (var i = 0; i < hitMs.Count; i++)
        {
            var x = X(hitMs[i]);
            dl.AddLine(new Vector2(x, strongTop), new Vector2(x, lanesBottom), ImGui.GetColorU32(WaypointColor(input.Hits[i].Sources) with { W = 0.55f }), 1.5f * scale);
        }

        // ---- moments the hit rules left out: faint, with a tooltip saying why
        var hoveredRejected = -1;
        for (var i = 0; i < input.Rejected.Count; i++)
        {
            var x = X(input.Rejected[i].Ms);
            var grey = ImGui.GetColorU32(new Vector4(0.6f, 0.6f, 0.6f, 0.35f));
            dl.AddLine(new Vector2(x, strongTop), new Vector2(x, lanesBottom), grey, 1f);
            dl.AddCircle(new Vector2(x, origin.Y + badgeH / 2f), 5f * scale, grey, 12, 1.5f);
            if (hovered && Math.Abs(mouse.X - x) <= 6 * scale && mouse.Y >= origin.Y && mouse.Y <= origin.Y + badgeH)
                hoveredRejected = i;
        }

        // ---- clips
        for (var i = 0; i < shown.Count; i++)
        {
            var p = shown[i];
            var start = p.DelayMs + offset;
            var x0 = X(start);
            var x1 = X(start + p.DurationMs);
            if (x1 < origin.X - 2 || x0 > origin.X + width + 2)
                continue;
            var sel = this.selected.Contains(p);
            DrawClip(dl, p, x0, x1, strongTop, laneH, p.Strong, StrongColor, input.Strength, sel, i, scale, true);
            DrawClip(dl, p, x0, x1, weakTop, laneH, p.Weak, WeakColor, input.Strength, sel, i, scale, false);
        }

        dl.AddText(new Vector2(origin.X + 4, strongTop + 2), ImGui.GetColorU32(new Vector4(1, 1, 1, 0.55f)), "Strong");
        dl.AddText(new Vector2(origin.X + 4, weakTop + 2), ImGui.GetColorU32(new Vector4(1, 1, 1, 0.55f)), "Weak");

        // ---- waypoint badges
        var hoveredHit = -1;
        var badgeR = 8.5f * scale;
        for (var i = 0; i < hitMs.Count; i++)
        {
            var x = X(hitMs[i]);
            var c = new Vector2(x, origin.Y + badgeH / 2f);
            var col = WaypointColor(input.Hits[i].Sources);
            dl.AddCircleFilled(c, badgeR, ImGui.GetColorU32(col));
            var label = (i + 1).ToString();
            var ts = ImGui.CalcTextSize(label);
            dl.AddText(c - ts / 2f, ImGui.GetColorU32(new Vector4(0.08f, 0.08f, 0.1f, 1f)), label);
            if (hovered && this.drag == DragMode.None && Math.Abs(mouse.X - x) <= badgeR + 1 && mouse.Y >= origin.Y && mouse.Y <= origin.Y + badgeH)
                hoveredHit = i;
        }

        if (hoveredHit < 0 && hoveredRejected >= 0)
        {
            var r = input.Rejected[hoveredRejected];
            ImGui.SetTooltip($"Left out at {r.Ms} ms (frame {r.Frame})\nHad: {SourceText(r.Sources)}\nThe hit rules need: {input.RuleText}");
        }

        if (hoveredHit >= 0)
        {
            var h = input.Hits[hoveredHit];
            ImGui.SetTooltip($"Hit {hoveredHit + 1}  ·  {hitMs[hoveredHit]:0} ms  (frame {h.Frame})\nFound from: {SourceText(h.Sources)}\nStrength weight: {h.Weight:0.00}" +
                             (editable ? this.selected.Count > 0 ? "\nClick: move the selected clips here" : "\nClick: add a clip on this hit" : string.Empty));
        }

        // ---- playhead
        if (input.PlayheadMs is { } ph)
        {
            var x = X(ph);
            dl.AddLine(new Vector2(x, strongTop), new Vector2(x, lanesBottom), ImGui.GetColorU32(new Vector4(1, 1, 1, 0.9f)), 2f);
        }

        if (!editable)
            return;

        // ---- hit testing
        var (hitIndex, hitMode) = this.drag == DragMode.None && hovered
            ? HitTest(shown, offset, mouse, X, strongTop, weakTop, laneH)
            : (-1, DragMode.None);

        if (this.drag == DragMode.None && hovered)
        {
            ImGui.SetMouseCursor(hitMode switch
            {
                DragMode.TrimStart or DragMode.Resize => ImGuiMouseCursor.ResizeEw,
                DragMode.LevelStrong or DragMode.LevelWeak => ImGuiMouseCursor.ResizeNs,
                DragMode.Move => ImGuiMouseCursor.Hand,
                _ => hoveredHit >= 0 ? ImGuiMouseCursor.Hand : ImGuiMouseCursor.Arrow,
            });
        }

        // ---- mouse down
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && this.drag == DragMode.None)
        {
            if (hoveredHit >= 0)
            {
                list = this.Editable(input, ref list, ref offset);
                if (list != null)
                    this.WaypointClick(input, list, offset, (int)MathF.Round(hitMs[hoveredHit]));
            }
            else if (hitIndex >= 0)
            {
                var index = hitIndex;
                list = this.Editable(input, ref list, ref offset);
                if (list != null)
                {
                    var p = list[index];
                    if (io.KeyCtrl)
                    {
                        if (!this.selected.Remove(p))
                            this.selected.Add(p);
                    }
                    else if (!this.selected.Contains(p))
                    {
                        this.Select(p, false);
                    }

                    if (this.selected.Contains(p))
                        this.BeginDrag(input.Key, list, offset, hitMode, p, mouse, io.KeyShift);
                }
            }
            else if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                list = this.Editable(input, ref list, ref offset);
                if (list != null)
                {
                    this.Snapshot(input.Key, list);
                    var at = (int)MathF.Round(this.SnapMs(Ms(mouse.X), hitMs, list, offset, pxPerMs, io));
                    var p = new VibrationPulse { DelayMs = Math.Max(0, at) - offset, DurationMs = 140, Strong = 60, Weak = 65 };
                    list.Add(p);
                    this.Select(p, false);
                    this.Finish(input, list);
                }
            }
            else
            {
                if (!io.KeyCtrl)
                    this.selected.Clear();
                this.drag = DragMode.Box;
                this.dragStartMouse = mouse;
            }
        }

        // ---- right click: clip menu
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right) && hitIndex >= 0 && this.drag == DragMode.None)
        {
            this.menuPulse = shown[hitIndex];
            if (!this.selected.Contains(this.menuPulse))
                this.Select(this.menuPulse, false);
            ImGui.OpenPopup("##clipmenu");
        }

        // ---- box select
        if (this.drag == DragMode.Box)
        {
            var a = Vector2.Min(this.dragStartMouse, mouse);
            var b = Vector2.Max(this.dragStartMouse, mouse);
            dl.AddRectFilled(a, b, ImGui.GetColorU32(new Vector4(1, 1, 1, 0.08f)));
            dl.AddRect(a, b, ImGui.GetColorU32(new Vector4(1, 1, 1, 0.6f)));

            // Live: every clip the box touches, in a lane the box covers.
            var lanesHit = (a.Y <= strongTop + laneH && b.Y >= strongTop) || (a.Y <= lanesBottom && b.Y >= weakTop);
            if (!io.KeyCtrl)
                this.selected.Clear();
            if (lanesHit && b.X - a.X > 2)
            {
                foreach (var p in shown)
                {
                    var x0 = X(p.DelayMs + offset);
                    var x1 = X(p.DelayMs + offset + p.DurationMs);
                    if (x1 >= a.X && x0 <= b.X)
                        this.selected.Add(p);
                }
            }

            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
                this.drag = DragMode.None;
        }

        // ---- dragging clips
        else if (this.drag != DragMode.None && list != null)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                this.ApplyDrag(list, offset, mouse, pxPerMs, laneH, hitMs, io, dl, X, strongTop, lanesBottom);
            else
                this.EndDrag(input, list, offset, X, strongTop, lanesBottom);
        }

        this.DrawClipMenu(input, list, offset);
        this.HandleKeys(input, list, offset, hovered ? Ms(mouse.X) : (float?)null);
    }

    // ---------------------------------------------------------------- drawing helpers

    private static void DrawClip(ImDrawListPtr dl, VibrationPulse p, float x0, float x1, float top, float laneH, int level, Vector4 color, float strength, bool selected, int index, float scale, bool label)
    {
        var bottom = top + laneH;
        var w = Math.Max(2f, x1 - x0);
        x1 = x0 + w;

        // Clip body (the whole length) and its envelope at the motor level.
        dl.AddRectFilled(new Vector2(x0, top + 1), new Vector2(x1, bottom - 1), ImGui.GetColorU32(color with { W = selected ? 0.28f : 0.16f }), 3f);
        var step = Math.Max(1f, 2f * scale);
        var fill = ImGui.GetColorU32(color with { W = 0.9f });
        for (var x = x0; x < x1; x += step)
        {
            var t = (x - x0) / w * p.DurationMs;
            var env = VibrationEngine.Envelope(p.Shape, t, p.DurationMs) * strength;
            var h = Math.Clamp(level * env / 100f, 0f, 1f) * (laneH - 2);
            if (h > 0)
                dl.AddRectFilled(new Vector2(x, bottom - 1 - h), new Vector2(Math.Min(x + step, x1), bottom - 1), fill);
        }

        // Level handle: the raw motor level.
        var ly = bottom - 1 - Math.Clamp(level / 100f, 0f, 1f) * (laneH - 2);
        dl.AddLine(new Vector2(x0 + 1, ly), new Vector2(x1 - 1, ly), ImGui.GetColorU32(new Vector4(1, 1, 1, selected ? 0.95f : 0.45f)), selected ? 2f : 1f);

        var border = selected ? ImGui.GetColorU32(new Vector4(1, 1, 1, 0.95f)) : ImGui.GetColorU32(color with { W = 0.75f });
        dl.AddRect(new Vector2(x0, top + 1), new Vector2(x1, bottom - 1), border, 3f, ImDrawFlags.None, selected ? 2f : 1f);

        if (label && w > 26 * scale)
            dl.AddText(new Vector2(x0 + 3, top + 2), ImGui.GetColorU32(new Vector4(1, 1, 1, 0.85f)), $"{index + 1}");
    }

    private static (float Minor, float Major) GridSteps(float pxPerMs)
    {
        // Major labels about every 70 px; minor lines at animation frames when there's room.
        float[] majors = { 33.333f, 50, 100, 250, 500, 1000, 2000 };
        var major = majors.FirstOrDefault(m => m * pxPerMs >= 70f, 2000);
        var minor = TmbParser.MsPerFrame * pxPerMs >= 7f ? TmbParser.MsPerFrame : major / 5f;
        if (major < 50)
            minor = major;
        return (minor, major);
    }

    private static Vector4 WaypointColor(HitSource s)
    {
        if (s.HasFlag(HitSource.MultiHit)) return new(0.98f, 0.8f, 0.3f, 1f);
        if (s.HasFlag(HitSource.BuiltInVibration)) return new(0.78f, 0.62f, 1f, 1f);
        if ((s & (HitSource.Flinch | HitSource.TargetVfx | HitSource.TargetSound | HitSource.ExtraStrike)) != 0) return new(0.95f, 0.95f, 0.95f, 1f);
        if (s.HasFlag(HitSource.CasterVfx)) return new(0.45f, 0.88f, 0.8f, 1f);
        if (s.HasFlag(HitSource.SoundPeak)) return new(1f, 0.55f, 0.78f, 1f);
        return new(0.7f, 0.7f, 0.7f, 1f);
    }

    public static string SourceText(HitSource s)
    {
        var parts = new List<string>();
        if (s.HasFlag(HitSource.MultiHit)) parts.Add("MultiHit table");
        if (s.HasFlag(HitSource.Flinch)) parts.Add("target flinches");
        if (s.HasFlag(HitSource.TargetVfx)) parts.Add("effect on the target");
        if (s.HasFlag(HitSource.TargetSound)) parts.Add("hit sound on the target");
        if (s.HasFlag(HitSource.ExtraStrike)) parts.Add("extra strike sound");
        if (s.HasFlag(HitSource.BuiltInVibration)) parts.Add("built-in vibration");
        if (s.HasFlag(HitSource.FlyText)) parts.Add("damage number");
        if (s.HasFlag(HitSource.CasterVfx)) parts.Add("effect beat");
        if (s.HasFlag(HitSource.SoundPeak)) parts.Add("peak in the sound (no hit markers)");
        return parts.Count == 0 ? "default timing (no animation data)" : string.Join(", ", parts);
    }

    // ---------------------------------------------------------------- editing

    private static (int Index, DragMode Mode) HitTest(IReadOnlyList<VibrationPulse> pulses, int offset, Vector2 m, Func<float, float> X, float strongTop, float weakTop, float laneH)
    {
        for (var i = pulses.Count - 1; i >= 0; i--)
        {
            var p = pulses[i];
            var x0 = X(p.DelayMs + offset);
            var x1 = Math.Max(x0 + 2, X(p.DelayMs + offset + p.DurationMs));
            foreach (var (top, level, levelMode) in new[] { (strongTop, p.Strong, DragMode.LevelStrong), (weakTop, p.Weak, DragMode.LevelWeak) })
            {
                var bottom = top + laneH;
                if (m.Y < top || m.Y > bottom || m.X < x0 - 4 || m.X > x1 + 4)
                    continue;

                var wide = x1 - x0 > 14;
                if (Math.Abs(m.X - x1) <= 5)
                    return (i, DragMode.Resize);
                if (wide && Math.Abs(m.X - x0) <= 5)
                    return (i, DragMode.TrimStart);
                var ly = bottom - 1 - Math.Clamp(level / 100f, 0f, 1f) * (laneH - 2);
                if (m.X >= x0 && m.X <= x1 && Math.Abs(m.Y - ly) <= 4)
                    return (i, levelMode);
                if (m.X >= x0 && m.X <= x1)
                    return (i, DragMode.Move);
            }
        }

        return (-1, DragMode.None);
    }

    private List<VibrationPulse>? Editable(Input input, ref List<VibrationPulse>? list, ref int offset)
    {
        if (list != null)
            return list;
        if (input.MakeEditable == null)
            return null;

        // The new list holds copies of the shown pulses in the same order: carry the selection over.
        var shownBefore = input.Display;
        var selectedIdx = shownBefore.Select((p, i) => (p, i)).Where(t => this.selected.Contains(t.p)).Select(t => t.i).ToList();
        var made = input.MakeEditable();
        list = made;
        offset = 0;
        this.selected.Clear();
        foreach (var i in selectedIdx.Where(i => i < made.Count))
            this.selected.Add(made[i]);
        return made;
    }

    private void BeginDrag(uint skill, List<VibrationPulse> list, int offset, DragMode mode, VibrationPulse grabbed, Vector2 mouse, bool shift)
    {
        this.Snapshot(skill, list);
        this.drag = mode;
        this.grabbed = grabbed;
        this.dragStartMouse = mouse;
        this.dragChanged = false;
        this.levelBoth = shift;
        this.originals = new Dictionary<VibrationPulse, Orig>(ReferenceEqualityComparer.Instance);
        foreach (var p in this.selected)
            this.originals[p] = new Orig(p.DelayMs + offset, p.DurationMs, p.Strong, p.Weak);
    }

    private bool levelBoth;

    private void ApplyDrag(List<VibrationPulse> list, int offset, Vector2 mouse, float pxPerMs, float laneH, List<float> hitMs, ImGuiIOPtr io, ImDrawListPtr dl, Func<float, float> X, float top, float bottom)
    {
        if (this.grabbed == null || !this.originals.TryGetValue(this.grabbed, out var g))
            return;

        var dx = (mouse.X - this.dragStartMouse.X) / pxPerMs;
        string tip;
        switch (this.drag)
        {
            case DragMode.Move:
            {
                var start = (float)g.Start + dx;
                var snappedStart = this.SnapMs(start, hitMs, list, offset, pxPerMs, io, out var startHit);
                var snappedEnd = this.SnapMs(start + g.Duration, hitMs, list, offset, pxPerMs, io, out var endHit) - g.Duration;
                var target = endHit && !startHit ? snappedEnd : snappedStart;
                var delta = (int)MathF.Round(target) - g.Start;
                var minStart = this.originals.Values.Min(o => o.Start);
                delta = Math.Max(delta, -minStart);
                foreach (var (p, o) in this.originals)
                    p.DelayMs = o.Start + delta - offset;
                tip = $"Start {g.Start + delta} ms";
                break;
            }

            case DragMode.TrimStart:
            {
                var end = g.Start + g.Duration;
                var start = (int)MathF.Round(this.SnapMs(g.Start + dx, hitMs, list, offset, pxPerMs, io));
                start = Math.Clamp(start, 0, end - MinDurationMs);
                var delta = start - g.Start;
                foreach (var (p, o) in this.originals)
                {
                    var oEnd = o.Start + o.Duration;
                    var s = Math.Clamp(o.Start + delta, 0, oEnd - MinDurationMs);
                    p.DelayMs = s - offset;
                    p.DurationMs = oEnd - s;
                }

                tip = $"Start {start} ms  ·  length {end - start} ms";
                break;
            }

            case DragMode.Resize:
            {
                var end = (int)MathF.Round(this.SnapMs(g.Start + g.Duration + dx, hitMs, list, offset, pxPerMs, io));
                end = Math.Max(end, g.Start + MinDurationMs);
                var delta = end - (g.Start + g.Duration);
                foreach (var (p, o) in this.originals)
                    p.DurationMs = Math.Max(MinDurationMs, o.Duration + delta);
                tip = $"Length {this.grabbed.DurationMs} ms";
                break;
            }

            case DragMode.LevelStrong:
            case DragMode.LevelWeak:
            {
                var dy = (int)MathF.Round((this.dragStartMouse.Y - mouse.Y) / laneH * 100f);
                foreach (var (p, o) in this.originals)
                {
                    if (this.drag == DragMode.LevelStrong || this.levelBoth)
                        p.Strong = Math.Clamp(o.Strong + dy, 0, 100);
                    if (this.drag == DragMode.LevelWeak || this.levelBoth)
                        p.Weak = Math.Clamp(o.Weak + dy, 0, 100);
                }

                tip = this.levelBoth
                    ? $"Strong {this.grabbed.Strong}%  ·  Weak {this.grabbed.Weak}%"
                    : this.drag == DragMode.LevelStrong ? $"Strong {this.grabbed.Strong}%" : $"Weak {this.grabbed.Weak}%";
                ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNs);
                break;
            }

            default:
                return;
        }

        this.dragChanged |= this.originals.Any(kv => kv.Key.DelayMs + offset != kv.Value.Start || kv.Key.DurationMs != kv.Value.Duration || kv.Key.Strong != kv.Value.Strong || kv.Key.Weak != kv.Value.Weak);
        ImGui.SetTooltip(tip);
    }

    private void EndDrag(Input input, List<VibrationPulse> list, int offset, Func<float, float> X, float top, float bottom)
    {
        var mode = this.drag;
        this.drag = DragMode.None;
        this.grabbed = null;

        if (mode == DragMode.Box)
            return;

        if (this.dragChanged)
        {
            this.Finish(input, list);
        }
        else if (this.history.TryGetValue(input.Key, out var h) && h.Undo.Count > 0)
        {
            // Just a click: drop the snapshot.
            h.Undo.RemoveAt(h.Undo.Count - 1);
        }
    }

    private void Finish(Input input, List<VibrationPulse> list)
    {
        list.Sort((a, b) => a.DelayMs.CompareTo(b.DelayMs));
        input.Edited?.Invoke();
    }

    private float SnapMs(float ms, List<float> hitMs, IReadOnlyList<VibrationPulse> list, int offset, float pxPerMs, ImGuiIOPtr io)
        => this.SnapMs(ms, hitMs, list, offset, pxPerMs, io, out _);

    /// <summary>Snap a time: to hits and other clips' edges within a few pixels, else to the frame grid.</summary>
    private float SnapMs(float ms, List<float> hitMs, IReadOnlyList<VibrationPulse> list, int offset, float pxPerMs, ImGuiIOPtr io, out bool snappedToObject)
    {
        snappedToObject = false;
        if (io.KeyAlt || !this.snap)
            return MathF.Round(ms);
        if (io.KeyCtrl)
            return MathF.Round(ms);

        var best = float.NaN;
        var bestDist = SnapPx / pxPerMs;
        foreach (var h in hitMs)
            Consider(h);
        foreach (var m in this.soundMarks)
            Consider(m);
        foreach (var p in list)
        {
            if (this.selected.Contains(p))
                continue;
            Consider(p.DelayMs + offset);
            Consider(p.DelayMs + offset + p.DurationMs);
        }

        if (!float.IsNaN(best))
        {
            snappedToObject = true;
            return MathF.Round(best);
        }

        return MathF.Round(MathF.Round(ms / TmbParser.MsPerFrame) * TmbParser.MsPerFrame);

        void Consider(float c)
        {
            var d = Math.Abs(c - ms);
            if (d < bestDist)
            {
                bestDist = d;
                best = c;
            }
        }
    }

    private void WaypointClick(Input input, List<VibrationPulse> list, int offset, int hitAt)
    {
        this.Snapshot(input.Key, list);
        if (this.selected.Count > 0)
        {
            var first = this.selected.Min(p => p.DelayMs + offset);
            var delta = hitAt - first;
            foreach (var p in this.selected)
                p.DelayMs += delta;
        }
        else
        {
            var auto = input.AutoPulses.Where(p => Math.Abs(p.DelayMs - hitAt) <= 2 * TmbParser.MsPerFrame).OrderBy(p => Math.Abs(p.DelayMs - hitAt)).FirstOrDefault();
            var clip = auto?.Clone() ?? new VibrationPulse { DurationMs = 140, Strong = 60, Weak = 65 };
            clip.DelayMs = hitAt - offset;
            list.Add(clip);
            this.Select(clip, false);
        }

        this.Finish(input, list);
    }

    private void DrawClipMenu(Input input, List<VibrationPulse>? list, int offset)
    {
        using var popup = ImRaii.Popup("##clipmenu");
        if (!popup || list == null)
            return;

        foreach (var s in Enum.GetValues<PulseShape>())
        {
            if (ImGui.Selectable($"Shape: {MainWindow.ShapeName(s)}", this.menuPulse?.Shape == s))
            {
                this.Snapshot(input.Key, list);
                foreach (var p in this.selected)
                    p.Shape = s;
                this.Finish(input, list);
            }
        }

        ImGui.Separator();
        if (ImGui.Selectable("Duplicate  (Ctrl+D)"))
            this.Duplicate(input, list, offset);
        if (ImGui.Selectable("Delete  (Del)"))
            this.Delete(input, list);
    }

    private void HandleKeys(Input input, List<VibrationPulse>? list, int offset, float? mouseMs)
    {
        if (list == null || ImGui.GetIO().WantTextInput || !ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
            return;

        var io = ImGui.GetIO();
        if (ImGui.IsKeyPressed(ImGuiKey.Delete) && this.selected.Count > 0)
            this.Delete(input, list);

        if (!io.KeyCtrl)
            return;

        if (ImGui.IsKeyPressed(ImGuiKey.A))
        {
            this.selected.Clear();
            foreach (var p in list)
                this.selected.Add(p);
        }
        else if (ImGui.IsKeyPressed(ImGuiKey.D) && this.selected.Count > 0)
        {
            this.Duplicate(input, list, offset);
        }
        else if (ImGui.IsKeyPressed(ImGuiKey.C) && this.selected.Count > 0)
        {
            var sel = list.Where(this.selected.Contains).ToList();
            var first = sel.Min(p => p.DelayMs);
            this.clipboard = sel.Select(p =>
            {
                var c = p.Clone();
                c.DelayMs -= first;
                return c;
            }).ToList();
        }
        else if (ImGui.IsKeyPressed(ImGuiKey.V) && this.clipboard is { Count: > 0 })
        {
            this.Snapshot(input.Key, list);
            var at = mouseMs is { } m ? (int)MathF.Round(m) - offset : (list.Count == 0 ? 0 : list.Max(p => p.DelayMs + p.DurationMs) + 50);
            this.selected.Clear();
            foreach (var c in this.clipboard)
            {
                var p = c.Clone();
                p.DelayMs += Math.Max(-offset, at);
                list.Add(p);
                this.selected.Add(p);
            }

            this.Finish(input, list);
        }
        else if (ImGui.IsKeyPressed(ImGuiKey.Z) && !io.KeyShift)
        {
            this.Undo(input, list);
        }
        else if (ImGui.IsKeyPressed(ImGuiKey.Y) || (ImGui.IsKeyPressed(ImGuiKey.Z) && io.KeyShift))
        {
            this.Redo(input, list);
        }
    }

    private void Duplicate(Input input, List<VibrationPulse> list, int offset)
    {
        if (this.selected.Count == 0)
            return;
        this.Snapshot(input.Key, list);
        var sel = list.Where(this.selected.Contains).ToList();
        var shift = sel.Max(p => p.DelayMs + p.DurationMs) - sel.Min(p => p.DelayMs) + 50;
        this.selected.Clear();
        foreach (var p in sel)
        {
            var c = p.Clone();
            c.DelayMs += shift;
            list.Add(c);
            this.selected.Add(c);
        }

        this.Finish(input, list);
    }

    private void Delete(Input input, List<VibrationPulse> list)
    {
        this.Snapshot(input.Key, list);
        list.RemoveAll(this.selected.Contains);
        this.selected.Clear();
        this.Finish(input, list);
    }

    public void Undo(Input input, List<VibrationPulse> list)
    {
        if (!this.history.TryGetValue(input.Key, out var h) || h.Undo.Count == 0)
            return;
        h.Redo.Add(Configuration.ClonePulses(list));
        var prev = h.Undo[^1];
        h.Undo.RemoveAt(h.Undo.Count - 1);
        list.Clear();
        list.AddRange(prev);
        this.selected.Clear();
        input.Edited?.Invoke();
    }

    public void Redo(Input input, List<VibrationPulse> list)
    {
        if (!this.history.TryGetValue(input.Key, out var h) || h.Redo.Count == 0)
            return;
        h.Undo.Add(Configuration.ClonePulses(list));
        var next = h.Redo[^1];
        h.Redo.RemoveAt(h.Redo.Count - 1);
        list.Clear();
        list.AddRange(next);
        this.selected.Clear();
        input.Edited?.Invoke();
    }
}
