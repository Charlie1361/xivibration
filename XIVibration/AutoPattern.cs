using System;
using System.Collections.Generic;
using System.Linq;
using XIVibration.Timeline;

namespace XIVibration;

public sealed record AutoResult(
    List<VibrationPulse> Pulses,
    float Scale,
    List<HitPoint> Hits,
    bool HasBuiltIn,
    List<VibrationPulse> BuiltInPreview)
{
    public List<float> HitMs => this.Hits.Select(h => h.Frame * TmbParser.MsPerFrame).ToList();

    public string SourceText
    {
        get
        {
            var all = this.Hits.Aggregate(HitSource.None, (a, h) => a | h.Sources);
            if (all == HitSource.None)
                return "no animation data, default timing";
            var parts = new List<string>();
            if (all.HasFlag(HitSource.MultiHit)) parts.Add("MultiHit table");
            if (all.HasFlag(HitSource.BuiltInVibration)) parts.Add("built-in vibration");
            if ((all & (HitSource.Flinch | HitSource.TargetVfx | HitSource.TargetSound)) != 0) parts.Add("impacts in the animation");
            if (all.HasFlag(HitSource.FlyText)) parts.Add("damage number");
            if (all.HasFlag(HitSource.CasterVfx)) parts.Add("effect beats");
            return string.Join(", ", parts);
        }
    }
}

/// <summary>Builds the automatic vibration for a skill: one pulse on every hit of its animation.</summary>
public static class AutoPattern
{
    private readonly record struct Level(int Strong, int Weak);

    private sealed record Profile(Level Main, int MainMs, PulseShape MainShape, Level Light, Level Heavy, int MinMs, int MaxMs);

    private static readonly Profile Weaponskill = new(new(65, 70), 150, PulseShape.Constant, new(25, 50), new(85, 85), 70, 180);
    private static readonly Profile Spell = new(new(55, 65), 190, PulseShape.FadeOut, new(20, 45), new(75, 80), 80, 210);
    private static readonly Profile Ability = new(new(75, 75), 210, PulseShape.Constant, new(30, 55), new(95, 90), 80, 230);
    private static readonly Profile BigAbility = new(new(100, 90), 380, PulseShape.FadeOut, new(40, 60), new(100, 95), 90, 380);
    private static readonly Profile Support = new(new(25, 50), 110, PulseShape.Constant, new(12, 35), new(40, 60), 70, 140);
    private static readonly Profile LimitBreak = new(new(100, 100), 600, PulseShape.FadeOut, new(60, 70), new(100, 100), 150, 600);

    public static AutoResult Build(ActionInfo? info, ActionTimelineInfo tl, HitAnalyzer.MultiHitTable? multiHit, Configuration cfg)
    {
        var (profile, scale) = Pick(info, cfg.AutoStrength);
        var hostile = info?.CanTargetHostile ?? true;
        var hits = HitAnalyzer.Analyze(tl.Entries, hostile, multiHit, tl.LengthFrames);
        var pulses = ToPulses(hits, profile, hostile, info?.IsAoE == true);
        return new AutoResult(pulses, scale, hits, tl.HasBuiltInVibration, TmbImport.ToPulses(tl.C120));
    }

    private static List<VibrationPulse> ToPulses(List<HitPoint> hits, Profile profile, bool hostile, bool aoe)
    {
        var pulses = new List<VibrationPulse>();
        var single = hits.Count == 1;
        for (var i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            var weight = hit.Weight;
            var last = i == hits.Count - 1;
            Level level;
            int duration;
            var shape = PulseShape.Constant;

            if (single)
            {
                level = Lerp(profile.Light, profile.Main, Math.Max(weight, 0.6f));
                duration = profile.MainMs;
                shape = profile.MainShape;
            }
            else
            {
                // The last hit of an attack is the finisher.
                if (last && hostile)
                {
                    weight = Math.Max(weight, 0.85f);
                    shape = PulseShape.FadeOut;
                }

                level = Lerp(profile.Light, profile.Heavy, weight);
                duration = (int)MathF.Round(profile.MinMs + (profile.MaxMs - profile.MinMs) * weight);
                if (shape == PulseShape.FadeOut)
                    duration = (int)(duration * 1.5f);
            }

            // Keep hits apart: a pulse never runs into the next hit.
            if (!last)
            {
                var gap = (hits[i + 1].Frame - hit.Frame) * TmbParser.MsPerFrame;
                duration = (int)MathF.Min(duration, MathF.Max(40f, gap - 15f));
            }

            pulses.Add(new VibrationPulse
            {
                DelayMs = (int)MathF.Round(hit.Frame * TmbParser.MsPerFrame),
                DurationMs = Math.Max(40, duration),
                Strong = Math.Clamp(level.Strong, 0, 100),
                Weak = Math.Clamp(level.Weak + (aoe ? 10 : 0), 0, 100),
                Shape = shape,
            });
        }

        return pulses;
    }

    private static (Profile Profile, float Scale) Pick(ActionInfo? info, AutoStrengths s)
    {
        if (info == null)
            return (Weaponskill, s.Weaponskill);
        if (info.Group == SkillGroup.LimitBreak)
            return (LimitBreak, s.LimitBreak);
        if (!info.CanTargetHostile)
            return (Support, s.Support);

        return info.Category switch
        {
            3 => (Weaponskill, s.Weaponskill),
            2 => (Spell, s.Spell),
            4 when info.Recast100ms >= 600 => (BigAbility, s.Ability),
            4 => (Ability, s.Ability),
            _ => (Weaponskill, s.Weaponskill),
        };
    }

    private static Level Lerp(Level a, Level b, float t)
        => new((int)MathF.Round(a.Strong + (b.Strong - a.Strong) * t), (int)MathF.Round(a.Weak + (b.Weak - a.Weak) * t));
}
