using System;
using System.Collections.Generic;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Lumina.Excel.Sheets;

namespace XIVibration;

/// <summary>
/// Watches the local player's animation sequencer, so patterns start when the animation
/// really starts (not when the server message arrives), and at the speed it really plays.
/// </summary>
public sealed unsafe class AnimationSync : IDisposable
{
    public readonly record struct PlayEvent(uint TimelineId, long TimeMs, float Speed);

    private delegate void PlayTimelineDelegate(ActionTimelineSequencer* self, ushort timelineId, void* a3);

    private readonly Hook<PlayTimelineDelegate>? hook;
    private readonly List<PlayEvent> recent = new();

    public AnimationSync()
    {
        try
        {
            this.hook = Plugin.GameInterop.HookFromAddress<PlayTimelineDelegate>(
                ActionTimelineSequencer.Addresses.PlayTimeline.Value, this.Detour);
            this.hook.Enable();
        }
        catch (Exception ex)
        {
            this.LastError = ex.Message;
            Plugin.Log.Error(ex, "Could not hook ActionTimelineSequencer.PlayTimeline");
        }
    }

    public bool HookActive => this.hook is { IsEnabled: true };

    public string? LastError { get; }

    /// <summary>Raised on the framework thread when the local player starts a timeline.</summary>
    public event Action<PlayEvent>? Played;

    /// <summary>Recent animation starts on your character and slot snapshots, for Diagnostics.</summary>
    public AnimationLog Log { get; } = new();

    /// <summary>The most recent start of one of these timelines within <paramref name="maxAgeMs"/>.</summary>
    public PlayEvent? FindRecent(ISet<uint> timelineIds, long maxAgeMs)
    {
        var now = Clock.NowMs;
        for (var i = this.recent.Count - 1; i >= 0; i--)
        {
            var e = this.recent[i];
            if (now - e.TimeMs > maxAgeMs)
                break;
            if (timelineIds.Contains(e.TimelineId))
                return e;
        }

        return null;
    }

    private void Detour(ActionTimelineSequencer* self, ushort timelineId, void* a3)
    {
        this.hook!.Original(self, timelineId, a3);

        try
        {
            var local = Plugin.ObjectTable.LocalPlayer;
            if (local == null || self == null || (nint)self->Parent != local.Address)
                return;

            this.Log.Add($"play #{timelineId} {AnimationLog.KeyOf(timelineId)}{(a3 == null ? string.Empty : " (with context)")}");
            if (timelineId == 0)
                return;

            var speed = 1f;
            var sheet = Plugin.DataManager.GetExcelSheet<ActionTimeline>();
            if (sheet.TryGetRow(timelineId, out var row))
            {
                var s = self->GetSlotSpeed(row.Slot);
                if (s > 0.05f && s < 20f)
                    speed = s;
            }

            var e = new PlayEvent(timelineId, Clock.NowMs, speed);
            this.recent.Add(e);
            if (this.recent.Count > 16)
                this.recent.RemoveAt(0);
            this.Played?.Invoke(e);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error in PlayTimeline hook");
        }
    }

    public void Dispose() => this.hook?.Dispose();
}

/// <summary>A short text log of what plays on your character (Settings > Diagnostics).</summary>
public sealed unsafe class AnimationLog
{
    private const int MaxLines = 40;
    private readonly List<string> lines = new();

    public IReadOnlyList<string> Lines => this.lines;

    public void Add(string text)
    {
        this.lines.Add($"{Clock.NowMs % 100000,5} ms  {text}");
        if (this.lines.Count > MaxLines)
            this.lines.RemoveAt(0);
    }

    public void Clear() => this.lines.Clear();

    public static string KeyOf(uint timelineId)
    {
        if (timelineId == 0)
            return "(stop)";
        var sheet = Plugin.DataManager.GetExcelSheet<ActionTimeline>();
        return sheet.TryGetRow(timelineId, out var row) ? $"{row.Key.ExtractText()} (slot {row.Slot})" : "?";
    }

    /// <summary>Which timeline each animation slot of your character is playing right now.</summary>
    public void Snapshot(string label)
    {
        var local = Plugin.ObjectTable.LocalPlayer;
        if (local == null || local.Address == nint.Zero)
            return;
        var seq = &((FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)local.Address)->Timeline.TimelineSequencer;
        var parts = new List<string>();
        for (var i = 0u; i < 14; i++)
        {
            var id = seq->GetSlotTimeline(i);
            if (id != 0)
                parts.Add($"slot {i}: #{id} {AnimationLog.KeyOf(id)}");
        }

        this.Add($"{label}: " + (parts.Count == 0 ? "all slots empty" : string.Join("; ", parts)));
    }
}

/// <summary>One clock for everything that schedules vibration.</summary>
public static class Clock
{
    private static readonly System.Diagnostics.Stopwatch Watch = System.Diagnostics.Stopwatch.StartNew();

    public static long NowMs => Watch.ElapsedMilliseconds;
}
