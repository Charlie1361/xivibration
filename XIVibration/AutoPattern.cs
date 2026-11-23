using System;
using System.Collections.Generic;
using System.Linq;
using XIVibration.Timeline;

namespace XIVibration;

public enum AutoSource
{
    /// <summary>Hit frames (C011) read from the animation.</summary>
    Animation,

    /// <summary>Per-hit timings from the MultiHit plugin.</summary>
    MultiHit,

    /// <summary>The animation's own C120 vibration frames (only used when replacing it).</summary>
    BuiltInFrames,

    /// <summary>No animation data, default hit delay.</summary>
    Default,
}

public sealed record AutoResult(
    List<VibrationPulse> Pulses,
    float Scale,
    List<float> HitMs,
    AutoSource Source,
    bool KeptBuiltIn,
    List<VibrationPulse> BuiltInPreview);

/// <summary>Builds the automatic vibration for a skill, timed to its hits.</summary>
public static class AutoPattern
{
    private readonly record struct Level(int Strong, int Weak);

    private sealed record Profile(Level Main, int MainMs, PulseShape MainShape, Level Light, Level Heavy, int MinMs, int MaxMs);

    private static readonly Profile Weaponskill = new(new(60, 65), 140, PulseShape.Constant, new(25, 50), new(80, 80), 80, 170);
    private static readonly Profile Spell = new(new(50, 60), 180, PulseShape.FadeOut, new(20, 45), new(70, 75), 90, 200);
    private static readonly Profile Ability = new(new(70, 70), 200, PulseShape.Constant, new(30, 55), new(90, 85), 90, 220);
    private static readonly Profile BigAbility = new(new(100, 90), 380, PulseShape.FadeOut, new(40, 60), new(100, 90), 100, 380);
    private static readonly Profile Support = new(new(15, 40), 90, PulseShape.Constant, new(10, 30), new(25, 50), 70, 110);
    private static readonly Profile LimitBreak = new(new(100, 100), 600, PulseShape.FadeOut, new(60, 70), new(100, 100), 150, 600);

    public static AutoResult Build(ActionInfo? info, ActionTimelineInfo tl, IReadOnlyList<MultiHitData.Hit>? multiHit, Configuration cfg)
    {
        var (profile, scale) = Pick(info, cfg.AutoStrength);
        var builtInPreview = TmbImport.ToPulses(tl.C120);

        // Hit frames and the weight (damage share) of each.
        List<(int Frame, float Weight)> hits;
        AutoSource source;
        if (multiHit is { Count: > 0 })
        {
            var max = Math.Max(1, multiHit.Max(h => h.Percent));
            hits = multiHit.Select(h => (tl.FirstHitFrame + h.Frame, h.Percent / (float)max)).ToList();
            source = AutoSource.MultiHit;
        }
        else if (cfg.ReplaceModVibration && tl.HasBuiltInVibration && tl.C120.Count > 1)
        {
            // The mod author placed these on the hits; keep the timing, use the plugin's feel.
            hits = tl.C120.Select(c => ((int)c.TimeFrames, WaveWeight(c.WaveType))).ToList();
            source = AutoSource.BuiltInFrames;
        }
        else if (tl.HitFrames.Count > 0)
        {
            hits = tl.HitFrames.Select(f => (f, 1f)).ToList();
            source = AutoSource.Animation;
        }
        else
        {
            hits = new() { (ActionTimelineInfo.DefaultHitFrame, 1f) };
            source = AutoSource.Default;
        }

        hits = hits.OrderBy(h => h.Frame).ToList();
        var hitMs = hits.Select(h => h.Frame * TmbParser.MsPerFrame).ToList();

        if (tl.HasBuiltInVibration && !cfg.ReplaceModVibration)
            return new AutoResult(new(), scale, hitMs, source, true, builtInPreview);

        var aoe = info?.IsAoE == true ? 10 : 0;
        var pulses = new List<VibrationPulse>();
        var single = hits.Count == 1;
        for (var i = 0; i < hits.Count; i++)
        {
            var (frame, weight) = hits[i];
            var start = frame * TmbParser.MsPerFrame;
            Level level;
            int duration;
            PulseShape shape;
            if (single)
            {
                level = profile.Main;
                duration = profile.MainMs;
                shape = profile.MainShape;
            }
            else
            {
                level = Lerp(profile.Light, profile.Heavy, weight);
                duration = (int)MathF.Round(profile.MinMs + (profile.MaxMs - profile.MinMs) * weight);
                var biggest = weight >= 0.999f && (i == hits.Count - 1 || hits.Skip(i + 1).All(h => h.Weight < 0.999f));
                shape = biggest ? PulseShape.FadeOut : PulseShape.Constant;
                if (biggest)
                    duration = (int)(duration * 1.5f);
            }

            // Keep hits apart: a pulse never runs into the next hit.
            if (i + 1 < hits.Count)
            {
                var gap = (hits[i + 1].Frame - frame) * TmbParser.MsPerFrame;
                duration = (int)MathF.Min(duration, MathF.Max(45f, gap - 20f));
            }

            pulses.Add(new VibrationPulse
            {
                DelayMs = (int)MathF.Round(start),
                DurationMs = Math.Max(45, duration),
                Strong = Math.Clamp(level.Strong, 0, 100),
                Weak = Math.Clamp(level.Weak + aoe, 0, 100),
                Shape = shape,
            });
        }

        return new AutoResult(pulses, scale, hitMs, source, false, builtInPreview);
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

    // C120 wave types: 1 heavy, 3 medium, 2 light.
    private static float WaveWeight(int waveType) => waveType switch { 1 => 1f, 3 => 0.6f, _ => 0.3f };

    private static Level Lerp(Level a, Level b, float t)
        => new((int)MathF.Round(a.Strong + (b.Strong - a.Strong) * t), (int)MathF.Round(a.Weak + (b.Weak - a.Weak) * t));
}
