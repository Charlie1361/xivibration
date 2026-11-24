using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Plugin.Ipc;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;

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
/// Spells and many abilities take their body motion from the job's battle animation set
/// (cbbm_mgc_..., chara/action/battle/...), which the game only loads while the weapon is drawn.
/// So a sheathed weapon is drawn first (/battlemode on, as a real skill would), like the game does.
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

    /// <summary>Wait at most this long for the weapon to come out.</summary>
    private const long DrawWeaponTimeoutMs = 3000;

    /// <summary>After the weapon is out, give the battle animations a moment to load.</summary>
    private const long AfterDrawMs = 450;

    private readonly ICallGateSubscriber<nint, int, object?>? redrawn;
    private long lastPlayMs = long.MinValue;
    private Waiting? waiting;

    private enum Step
    {
        DrawWeapon,
        Redraw,
    }

    private sealed class Waiting
    {
        public required uint TimelineId { get; init; }
        public required int ObjectIndex { get; init; }
        public required Action Started { get; init; }
        public required bool ClearEffects { get; init; }
        public Step Step { get; set; }
        public long DeadlineMs { get; set; }
        public long DoneMs { get; set; } = -1;
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

    /// <summary>
    /// Plays the animation on the character, clearing the previous Test's effects first if needed.
    /// <paramref name="started"/> runs at the moment the animation starts (now, or after the redraw).
    /// </summary>
    /// <returns>False with a reason when the character can't play it now.</returns>
    public bool Request(uint timelineId, Action started, out string? reason)
    {
        this.waiting = null;
        if (!CanPlay(timelineId, out reason, out var objectIndex))
            return false;

        var now = Clock.NowMs;
        var clear = now - this.lastPlayMs < EffectsLingerMs && this.redrawn != null;
        var w = new Waiting { TimelineId = timelineId, ObjectIndex = objectIndex, Started = started, ClearEffects = clear };

        if (!WeaponOut() && DrawWeapon())
        {
            w.Step = Step.DrawWeapon;
            w.DeadlineMs = now + DrawWeaponTimeoutMs;
            this.waiting = w;
            return true;
        }

        if (clear && this.StartRedraw(w))
            return true;

        if (!Play(timelineId, out reason))
            return false;
        this.lastPlayMs = Clock.NowMs;
        started();
        return true;
    }

    /// <summary>Drops a waiting Test (Stop, another skill selected...).</summary>
    public void Cancel() => this.waiting = null;

    /// <summary>Called every frame on the framework thread.</summary>
    public void Update()
    {
        if (this.waiting is not { } w)
            return;

        var now = Clock.NowMs;
        if (w.Step == Step.DrawWeapon)
        {
            if (w.DoneMs < 0 && WeaponOut())
                w.DoneMs = now;
            var drawn = w.DoneMs >= 0 && now - w.DoneMs >= AfterDrawMs;
            if (!drawn && now < w.DeadlineMs)
                return;

            // Weapon out (or gave up waiting): clear old effects if needed, else play.
            if (w.ClearEffects && this.StartRedraw(w))
                return;
        }
        else
        {
            var ready = (w.DoneMs >= 0 && now - w.DoneMs >= AfterRedrawMs) || now >= w.DeadlineMs;
            if (!ready)
                return;
        }

        this.waiting = null;
        if (!CanPlay(w.TimelineId, out _, out _) || !Play(w.TimelineId, out _))
            return;
        this.lastPlayMs = Clock.NowMs;
        w.Started();
    }

    private bool StartRedraw(Waiting w)
    {
        if (!Redraw(w.ObjectIndex))
            return false;
        w.Step = Step.Redraw;
        w.DoneMs = -1;
        w.DeadlineMs = Clock.NowMs + RedrawTimeoutMs;
        this.waiting = w;
        return true;
    }

    private static bool WeaponOut()
    {
        try
        {
            return Plugin.ObjectTable.LocalPlayer?.StatusFlags.HasFlag(StatusFlags.WeaponOut) == true;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Draws the weapon the way the player would: the /battlemode command.</summary>
    private static bool DrawWeapon()
    {
        try
        {
            var ui = UIModule.Instance();
            if (ui == null)
                return false;
            var text = Utf8String.FromString("/battlemode on");
            ui->ProcessChatBoxEntry(text, nint.Zero, false);
            text->Dtor(true);
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not draw the weapon");
            return false;
        }
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

    private static bool Play(uint timelineId, out string? reason)
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
