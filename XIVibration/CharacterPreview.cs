using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Ipc;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Lumina.Excel.Sheets;
using XIVibration.Timeline;

namespace XIVibration;

/// <summary>
/// Plays a skill's animation on your own character for Test. Only you see it: it's the game's own
/// animation player run locally, so the game plays the animation's sounds and effects itself.
/// <para>
/// Effects from the previous Test would stay on the character and pile up, and the game has no safe
/// call to remove them. So when a Test played recently, the character is first redrawn through
/// Penumbra (which drops every effect attached to it), and the animation starts once that's done.
/// </para>
/// <para>
/// Some animations end through the job's cast set (they start chara/action/battle/magic_pld_end and
/// the like). The game only starts those after the cast pose (battle/magic_pld_loop) has played, as in
/// a real cast; called directly they're silently dropped. So every play is checked on the next frame,
/// and if it didn't stick, the cast pose plays first and the animation is retried until it does.
/// </para>
/// </summary>
public sealed unsafe class CharacterPreview : IDisposable
{
    /// <summary>Effects of a Test this recent may still be on the character.</summary>
    private const long EffectsLingerMs = 12000;

    /// <summary>Wait at most this long for the redraw, then play anyway.</summary>
    private const long RedrawTimeoutMs = 1500;

    /// <summary>Let the new model settle a moment after the redraw.</summary>
    private const long AfterRedrawMs = 120;

    /// <summary>Keep retrying after the cast pose for at most this long.</summary>
    private const long RetryTimeoutMs = 1500;

    private readonly ICallGateSubscriber<nint, int, object?>? redrawn;
    private readonly List<(long At, string Label)> snapshots = new();
    private Dictionary<string, uint>? timelineByKey;
    private long lastPlayMs = long.MinValue;
    private Waiting? waiting;

    private enum Step
    {
        Redraw,
        Verify,
        Retry,
    }

    private sealed class Waiting
    {
        public required uint TimelineId { get; init; }
        public required uint PreludeId { get; init; }
        public required int ObjectIndex { get; init; }
        public required Action<long> Started { get; init; }
        public required Action<string> Failed { get; init; }
        public Step Step { get; set; }
        public long DeadlineMs { get; set; }
        public long DoneMs { get; set; } = -1;
        public long PlayedMs { get; set; }
        public int Tries { get; set; }
    }

    public CharacterPreview()
    {
        try
        {
            this.redrawn = Plugin.PluginInterface.GetIpcSubscriber<nint, int, object?>("Penumbra.GameObjectRedrawn");
            this.redrawn.Subscribe(this.OnRedrawn);
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "Penumbra redraw event not available");
            this.redrawn = null;
        }
    }

    private static AnimationLog? Log => Plugin.Instance?.Sync.Log;

    /// <summary>
    /// Plays the skill's animation on the character. <paramref name="started"/> gets the moment the
    /// animation really started; <paramref name="failed"/> runs if the game never took it.
    /// </summary>
    /// <returns>False with a reason when the character can't play anything now.</returns>
    public bool Request(ActionTimelineInfo tl, Action<long> started, Action<string> failed, out string? reason)
    {
        this.waiting = null;
        if (!CanPlay(tl.TimelineId, out reason, out var objectIndex))
            return false;

        var w = new Waiting
        {
            TimelineId = tl.TimelineId,
            PreludeId = this.PreludeFor(tl),
            ObjectIndex = objectIndex,
            Started = started,
            Failed = failed,
        };
        this.waiting = w;

        var now = Clock.NowMs;
        if (now - this.lastPlayMs < EffectsLingerMs && this.redrawn != null && Redraw(objectIndex))
        {
            w.Step = Step.Redraw;
            w.DeadlineMs = now + RedrawTimeoutMs;
            Log?.Add("Test: clearing the previous Test's effects (redraw)");
            return true;
        }

        this.PlayAndVerify(w);
        return true;
    }

    /// <summary>Drops a waiting Test (Stop, another skill selected...).</summary>
    public void Cancel() => this.waiting = null;

    /// <summary>Called every frame on the framework thread.</summary>
    public void Update()
    {
        for (var i = this.snapshots.Count - 1; i >= 0; i--)
        {
            if (Clock.NowMs < this.snapshots[i].At)
                continue;
            Log?.Snapshot(this.snapshots[i].Label);
            this.snapshots.RemoveAt(i);
        }

        if (this.waiting is not { } w)
            return;

        var now = Clock.NowMs;
        switch (w.Step)
        {
            case Step.Redraw:
                if ((w.DoneMs >= 0 && now - w.DoneMs >= AfterRedrawMs) || now >= w.DeadlineMs)
                    this.PlayAndVerify(w);
                break;

            case Step.Verify:
                if (IsPlaying(w.TimelineId))
                {
                    this.Succeed(w, "playing");
                }
                else if (w.PreludeId != 0)
                {
                    // Dropped: play the cast pose first, like a real cast, then retry.
                    Log?.Add($"Test: the game dropped it; cast pose #{w.PreludeId} {AnimationLog.KeyOf(w.PreludeId)} first");
                    PlayNow(w.PreludeId, out _);
                    w.Step = Step.Retry;
                    w.DeadlineMs = now + RetryTimeoutMs;
                }
                else
                {
                    this.Fail(w);
                }

                break;

            case Step.Retry:
                if (IsPlaying(w.TimelineId))
                {
                    this.Succeed(w, $"playing after the cast pose ({w.Tries} tries)");
                }
                else if (now >= w.DeadlineMs || !CanPlay(w.TimelineId, out _, out _))
                {
                    this.Fail(w);
                }
                else
                {
                    w.Tries++;
                    w.PlayedMs = now;
                    PlayNow(w.TimelineId, out _);
                }

                break;
        }
    }

    private void PlayAndVerify(Waiting w)
    {
        if (!CanPlay(w.TimelineId, out var reason, out _) || !PlayNow(w.TimelineId, out reason))
        {
            this.waiting = null;
            w.Failed(reason ?? "the game refused");
            return;
        }

        Log?.Add($"Test: asked the game to play #{w.TimelineId} {AnimationLog.KeyOf(w.TimelineId)}");
        w.PlayedMs = Clock.NowMs;
        w.Step = Step.Verify;
    }

    private void Succeed(Waiting w, string how)
    {
        this.waiting = null;
        this.lastPlayMs = Clock.NowMs;
        Log?.Add($"Test: #{w.TimelineId} {how}");
        this.ScheduleSnapshots();
        w.Started(w.PlayedMs);
    }

    private void Fail(Waiting w)
    {
        this.waiting = null;
        Log?.Add($"Test: the game didn't start #{w.TimelineId}");
        this.ScheduleSnapshots();
        w.Failed("the game didn't start this animation");
    }

    /// <summary>
    /// The cast pose an animation needs before it: for a sub-timeline chara/action/battle/X_end,
    /// the timeline battle/X_loop (or battle/X_start). 0 when it needs none.
    /// </summary>
    private uint PreludeFor(ActionTimelineInfo tl)
    {
        foreach (var e in tl.Entries)
        {
            if (e.Tag != "C002" || !e.Path.StartsWith("chara/action/battle/", StringComparison.OrdinalIgnoreCase)
                || !e.Path.EndsWith("_end", StringComparison.OrdinalIgnoreCase))
                continue;

            var set = e.Path["chara/action/".Length..^"_end".Length];
            foreach (var key in new[] { set + "_loop", set + "_start" })
            {
                if (this.TimelineByKey().TryGetValue(key, out var id))
                    return id;
            }
        }

        return 0;
    }

    private Dictionary<string, uint> TimelineByKey()
    {
        if (this.timelineByKey != null)
            return this.timelineByKey;

        var map = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Plugin.DataManager.GetExcelSheet<ActionTimeline>())
        {
            var key = row.Key.ExtractText();
            if (key.Length > 0)
                map.TryAdd(key, row.RowId);
        }

        return this.timelineByKey = map;
    }

    /// <summary>Whether one of the character's animation slots is playing this timeline.</summary>
    private static bool IsPlaying(uint timelineId)
    {
        var local = Plugin.ObjectTable.LocalPlayer;
        if (local == null || local.Address == nint.Zero)
            return false;
        var seq = &((Character*)local.Address)->Timeline.TimelineSequencer;
        for (var i = 0u; i < 14; i++)
        {
            if (seq->GetSlotTimeline(i) == timelineId)
                return true;
        }

        return false;
    }

    private void OnRedrawn(nint address, int objectIndex)
    {
        if (this.waiting is { Step: Step.Redraw } w && w.ObjectIndex == objectIndex && w.DoneMs < 0)
            w.DoneMs = Clock.NowMs;
    }

    private static bool Redraw(int objectIndex)
    {
        try
        {
            // RedrawType.Redraw = 0.
            Plugin.PluginInterface.GetIpcSubscriber<int, int, object?>("Penumbra.RedrawObject.V5").InvokeAction(objectIndex, 0);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "Penumbra redraw not available");
            return false;
        }
    }

    private static bool CanPlay(uint timelineId, out string? reason, out int objectIndex)
    {
        reason = null;
        objectIndex = -1;
        if (timelineId == 0 || timelineId > ushort.MaxValue)
        {
            reason = "this skill has no animation";
            return false;
        }

        var local = Plugin.ObjectTable.LocalPlayer;
        if (local == null || local.Address == nint.Zero)
        {
            reason = "no character";
            return false;
        }

        objectIndex = local.ObjectIndex;
        var c = Plugin.Condition;
        if (c[ConditionFlag.Mounted] || c[ConditionFlag.RidingPillion])
            reason = "you're mounted";
        else if (c[ConditionFlag.Casting])
            reason = "you're casting";
        else if (c[ConditionFlag.OccupiedInCutSceneEvent] || c[ConditionFlag.WatchingCutscene] || c[ConditionFlag.WatchingCutscene78])
            reason = "a cutscene is playing";
        else if (c[ConditionFlag.BetweenAreas] || c[ConditionFlag.BetweenAreas51] || c[ConditionFlag.Occupied] || c[ConditionFlag.OccupiedInEvent])
            reason = "your character is busy";
        return reason == null;
    }

    /// <summary>Logs what each animation slot plays shortly after now (Diagnostics).</summary>
    public void ScheduleSnapshots()
    {
        var now = Clock.NowMs;
        this.snapshots.Clear();
        this.snapshots.Add((now + 1, "slots right after"));
        this.snapshots.Add((now + 300, "slots after 0.3 s"));
        this.snapshots.Add((now + 1000, "slots after 1 s"));
    }

    private static bool PlayNow(uint timelineId, out string? reason)
    {
        reason = null;
        try
        {
            var local = Plugin.ObjectTable.LocalPlayer;
            if (local == null || local.Address == nint.Zero)
            {
                reason = "no character";
                return false;
            }

            var chara = (Character*)local.Address;
            chara->Timeline.TimelineSequencer.PlayTimeline((ushort)timelineId, null);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not play the animation on the character");
            reason = "the game refused";
            return false;
        }
    }

    public void Dispose()
    {
        this.waiting = null;
        try
        {
            this.redrawn?.Unsubscribe(this.OnRedrawn);
        }
        catch
        {
            // Penumbra already gone.
        }
    }
}
