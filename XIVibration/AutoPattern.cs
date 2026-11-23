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
    List<VibrationPulse> BuiltInPreview,
    Audio.SkillSound? Sound = null,
    List<int>? SoundMarks = null,
    bool FromSound = false)
{
    public List<float> HitMs => this.Hits.Select(h => h.Frame * TmbParser.MsPerFrame).ToList();

    public string SourceText
    {
        get
        {
            var all = this.Hits.Aggregate(HitSource.None, (a, h) => a | h.Sources);
            if (all == HitSource.None && !this.FromSound)
                return "no animation data, default timing";
            var parts = new List<string>();
            if (this.FromSound) parts.Add("the skill's sounds");
            if (all.HasFlag(HitSource.MultiHit)) parts.Add("MultiHit table");
            if (all.HasFlag(HitSource.BuiltInVibration)) parts.Add("built-in vibration");
            if ((all & (HitSource.Flinch | HitSource.TargetVfx | HitSource.TargetSound | HitSource.ExtraStrike)) != 0) parts.Add("impacts in the animation");
            if (all.HasFlag(HitSource.FlyText)) parts.Add("damage number");
            if (all.HasFlag(HitSource.CasterVfx)) parts.Add("effect beats");
            return string.Join(", ", parts);
        }
    }
}

/// <summary>Builds the automatic vibration for a skill: one pulse on every hit of its animation.</summary>
public static class AutoPattern
{
    public const int MinPulseMs = 60;
    public const int MinOffGapMs = 25;
    public const int DenseGapMs = 90;

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
        var hitPulses = ToPulses(hits, profile, hostile, info?.IsAoE == true);

        // Sounds first: clips start where a sound hits, end where it fades, as strong as it is loud.
        // Hits the sounds don't cover are added from the animation's other cues.
        if (tl.Sound is { Low.Length: > 0 } sound)
        {
            var seg = Audio.SoundSegmenter.Segment(sound);
            if (seg.Clips.Count > 0)
            {
                var pulses = MergeWithHits(seg.Clips, hits, hitPulses);
                return new AutoResult(pulses, scale, hits, tl.HasBuiltInVibration, TmbImport.ToPulses(tl.C120), sound, seg.Marks, true);
            }
        }

        return new AutoResult(hitPulses, scale, hits, tl.HasBuiltInVibration, TmbImport.ToPulses(tl.C120), tl.Sound);
    }

    /// <summary>A hit this close (ms) to a sound clip counts as covered by it.</summary>
    private const int CoverMs = 67;

    private static List<VibrationPulse> MergeWithHits(List<VibrationPulse> clips, List<HitPoint> hits, List<VibrationPulse> hitPulses)
    {
        var result = Configuration.ClonePulses(clips);
        var impacts = ~(HitSource.FlyText | HitSource.CasterVfx | HitSource.None);
        for (var i = 0; i < hits.Count && i < hitPulses.Count; i++)
        {
            var hit = hits[i];
            var at = StartMs(hit);
            result.Sort((a, b) => a.DelayMs.CompareTo(b.DelayMs));

            // A sound starts on this hit: it's already there.
            if (result.Any(c => Math.Abs(c.DelayMs - at) <= CoverMs))
                continue;

            var inside = result.FirstOrDefault(c => c.DelayMs < at && at < c.DelayMs + c.DurationMs);
            if (inside != null)
            {
                // A long sound carries a strike with no onset of its own (a whoosh over several
                // hits): split the clip on the hit and make the part from the hit on stronger.
                var end = inside.DelayMs + inside.DurationMs;
                if ((hit.Sources & impacts) != 0 && at - inside.DelayMs >= 50 && end - at >= 30)
                {
                    var accent = inside.Clone();
                    accent.DelayMs = at;
                    accent.DurationMs = end - at;
                    accent.Strong = Math.Min(100, (int)MathF.Round(Math.Max(inside.Strong, 10) * 1.15f));
                    accent.Weak = Math.Min(100, (int)MathF.Round(Math.Max(inside.Weak, 10) * 1.15f));
                    inside.DurationMs = at - inside.DelayMs;
                    result.Add(accent);
                }

                continue;
            }

            // Right around a clip's end: covered by that sound.
            if (result.Any(c => Math.Abs(c.DelayMs + c.DurationMs - at) <= CoverMs && c.DelayMs + c.DurationMs <= at))
                continue;

            // No sound here: a pulse from the animation's cues, kept clear of the next sound.
            var p = hitPulses[i].Clone();
            var next = result.Where(c => c.DelayMs > at).Select(c => c.DelayMs).DefaultIfEmpty(int.MaxValue).Min();
            p.DurationMs = Math.Min(p.DurationMs, next - at);
            if (p.DurationMs >= 30)
                result.Add(p);
        }

        Audio.SoundSegmenter.Tidy(result);
        return result;
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

            // Keep hits apart. Motors can't render taps shorter than about 60 ms, so hits closer
            // than 90 ms run into each other as one continuous pulse that steps through their levels.
            var startMs = StartMs(hit);
            if (!last)
            {
                var gap = StartMs(hits[i + 1]) - startMs;
                duration = gap < DenseGapMs
                    ? gap
                    : Math.Max(MinPulseMs, Math.Min(duration, gap - MinOffGapMs));
            }
            else
            {
                duration = Math.Max(MinPulseMs, duration);
            }

            pulses.Add(new VibrationPulse
            {
                DelayMs = startMs,
                DurationMs = Math.Max(16, duration),
                Strong = Math.Clamp(level.Strong, 0, 100),
                Weak = Math.Clamp(level.Weak + (aoe ? 10 : 0), 0, 100),
                Shape = shape,
            });
        }

        return pulses;
    }

    private static int StartMs(HitPoint hit) => (int)MathF.Round(hit.Frame * TmbParser.MsPerFrame);

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
