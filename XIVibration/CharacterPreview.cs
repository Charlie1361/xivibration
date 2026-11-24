using System;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace XIVibration;

/// <summary>
/// Plays a skill's animation on your own character for Test. Only you see it: it's the game's own
/// animation player run locally, so the game plays the animation's sounds and effects itself.
/// </summary>
public static unsafe class CharacterPreview
{
    public static bool TryPlay(uint timelineId, out string? reason)
    {
        reason = null;
        try
        {
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

            var c = Plugin.Condition;
            if (c[ConditionFlag.Mounted] || c[ConditionFlag.RidingPillion])
                reason = "you're mounted";
            else if (c[ConditionFlag.Casting])
                reason = "you're casting";
            else if (c[ConditionFlag.OccupiedInCutSceneEvent] || c[ConditionFlag.WatchingCutscene] || c[ConditionFlag.WatchingCutscene78])
                reason = "a cutscene is playing";
            else if (c[ConditionFlag.BetweenAreas] || c[ConditionFlag.BetweenAreas51] || c[ConditionFlag.Occupied] || c[ConditionFlag.OccupiedInEvent])
                reason = "your character is busy";
            if (reason != null)
                return false;

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
}
