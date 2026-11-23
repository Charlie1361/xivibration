using System;
using System.Collections.Generic;
using System.Linq;
using XIVibration.Timeline;

namespace XIVibration;

/// <summary>Where a skill's hits came from.</summary>
public enum HitOrigin
{
    /// <summary>Real markers in the animation (flinch, hit sounds, MultiHit, ...).</summary>
    Markers,

    /// <summary>No markers: the peaks in the skill's sound.</summary>
    SoundPeaks,

    /// <summary>Only weak cues: damage number or effect beats.</summary>
    WeakCues,

    /// <summary>Nothing found: the damage number frame.</summary>
    DamageNumber,

    None,
}

public sealed record AutoResult(
    List<VibrationPulse> Pulses,
    float Scale,
    List<HitPoint> Hits,
    bool HasBuiltIn,
    List<VibrationPulse> BuiltInPreview,
    Audio.SkillSound? Sound = null,
    List<int>? SoundMarks = null,
    bool FromSound = false,
    List<HitPoint>? Rejected = null,
    HitOrigin Origin = HitOrigin.Markers)
{
    public List<float> HitMs => this.Hits.Select(h => (float)h.Ms).ToList();

    public string SourceText
    {
        get
        {
            var all = this.Hits.Aggregate(HitSource.None, (a, h) => a | h.Sources);
            var parts = new List<string>();
            if (this.FromSound) parts.Add("bumps in the sound");
            switch (this.Origin)
            {
                case HitOrigin.SoundPeaks:
                    parts.Add("no hit markers: peaks in the sound");
                    break;
                case HitOrigin.DamageNumber:
                    parts.Add("nothing matched: damage number");
                    break;
                case HitOrigin.None:
                    parts.Add("nothing matched the hit rules");
                    break;
                default:
                    if (all.HasFlag(HitSource.MultiHit)) parts.Add("MultiHit table");
                    if (all.HasFlag(HitSource.BuiltInVibration)) parts.Add("built-in vibration");
                    if ((all & (HitSource.Flinch | HitSource.TargetVfx | HitSource.TargetSound | HitSource.ExtraStrike)) != 0) parts.Add("impacts in the animation");
                    if (all.HasFlag(HitSource.FlyText)) parts.Add("damage number");
                    if (all.HasFlag(HitSource.CasterVfx)) parts.Add("effect beats");
                    break;
            }

            return parts.Count == 0 ? "no animation data" : string.Join(", ", parts);
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
        var sound = tl.Sound is { Low.Length: > 0 } s ? s : null;
        var analysis = HitAnalyzer.Analyze(tl.Entries, hostile, multiHit, tl.LengthFrames, cfg.Hits);

        // Where the hits come from: real markers, else the peaks in the sound, else weak cues
        // (damage number, effect beats), else the damage number frame.
        List<HitPoint> hits;
        HitOrigin origin;
        List<HitPoint> soundPeaks;
        if (analysis.HasMarkers)
        {
            hits = analysis.Hits;
            origin = HitOrigin.Markers;
        }
        else if (sound != null && (soundPeaks = SoundPeakHits(sound, cfg.Sound.Threshold)).Count > 0)
        {
            hits = soundPeaks;
            origin = HitOrigin.SoundPeaks;
        }
        else if (analysis.Hits.Count > 0)
        {
            hits = analysis.Hits;
            origin = HitOrigin.WeakCues;
        }
        else if (cfg.Hits.FallbackToDamageNumber)
        {
            hits = new() { new HitPoint(analysis.FlyTextFrame >= 0 ? analysis.FlyTextFrame : 10, 1f, HitSource.FlyText) };
            origin = HitOrigin.DamageNumber;
        }
        else
        {
            hits = new();
            origin = HitOrigin.None;
        }

        var hitPulses = ToPulses(hits, profile, hostile, info?.IsAoE == true);

        // Hits decide when; the sound decides how strong.
        if (sound != null)
            ApplySoundLevels(hitPulses, hits, sound);

        // Optional: build from the sound's bumps instead of the hits.
        if (cfg.Sound.Enabled && sound != null)
        {
            var seg = Audio.SoundSegmenter.Segment(sound, cfg.Sound);
            if (seg.Clips.Count > 0)
            {
                var pulses = cfg.Sound.PulseSilentHits ? MergeWithHits(seg.Clips, hits, hitPulses) : seg.Clips;
                MakeLastHitLoud(pulses, hits);
                return new AutoResult(pulses, scale, hits, tl.HasBuiltInVibration, TmbImport.ToPulses(tl.C120), sound, seg.Marks, true, analysis.Rejected, origin);
            }
        }

        MakeLastHitLoud(hitPulses, hits);
        return new AutoResult(hitPulses, scale, hits, tl.HasBuiltInVibration, TmbImport.ToPulses(tl.C120), tl.Sound, null, false, analysis.Rejected, origin);
    }

    /// <summary>The clear peaks in a skill's sound, as hits.</summary>
    private static List<HitPoint> SoundPeakHits(Audio.SkillSound sound, float threshold)
    {
        var (peaks, _) = Audio.SoundSegmenter.FindPeaks(sound, threshold);
        return peaks.Select(p =>
        {
            var ms = p.Index * Audio.SoundEnvelope.StepMs;
            return new HitPoint((int)MathF.Round(ms / TmbParser.MsPerFrame), Math.Clamp(0.4f + p.Prominence * 1.5f, 0f, 1f), HitSource.SoundPeak, ms);
        }).ToList();
    }

    /// <summary>Loudness is read from a little before the hit to a little after (the sound's attack can lag).</summary>
    private const int LevelBeforeMs = 30;
    private const int LevelAfterMs = 100;

    /// <summary>A hit is always felt: its stronger motor gets at least this (%).</summary>
    private const int MinHitLevel = 25;

    /// <summary>The last hit: at least this on both motors.</summary>
    private const int FinisherLevel = 90;

    /// <summary>Each hit pulse as strong as the sound is loud at that hit (bass: strong motor, the rest: weak motor).</summary>
    private static void ApplySoundLevels(List<VibrationPulse> pulses, List<HitPoint> hits, Audio.SkillSound sound)
    {
        const int step = Audio.SoundEnvelope.StepMs;
        for (var i = 0; i < pulses.Count && i < hits.Count; i++)
        {
            var at = StartMs(hits[i]);
            var a = Math.Max(0, (at - LevelBeforeMs) / step);
            var b = Math.Min(sound.Low.Length, (at + LevelAfterMs) / step + 1);
            if (a >= b)
                continue; // no sound at this hit: keep the animation-based level

            float low = 0, high = 0;
            for (var k = a; k < b; k++)
            {
                low = Math.Max(low, sound.Low[k]);
                high = Math.Max(high, sound.High[k]);
            }

            var strong = (int)MathF.Round(low * 100);
            var weak = (int)MathF.Round(high * 100);
            var top = Math.Max(strong, weak);
            if (top <= 0)
                continue;
            if (top < MinHitLevel)
            {
                strong = (int)MathF.Round(strong * MinHitLevel / (float)top);
                weak = (int)MathF.Round(weak * MinHitLevel / (float)top);
            }

            pulses[i].Strong = Math.Clamp(strong, 0, 100);
            pulses[i].Weak = Math.Clamp(weak, 0, 100);
        }
    }

    /// <summary>The last hit is the skill's strongest pulse: at least <see cref="FinisherLevel"/> on both motors.</summary>
    private static void MakeLastHitLoud(List<VibrationPulse> pulses, List<HitPoint> hits)
    {
        if (pulses.Count == 0)
            return;

        // The pulse on the last hit (or the last pulse, if no hit has one).
        var target = pulses[^1];
        if (hits.Count > 0)
        {
            var lastHit = StartMs(hits[^1]);
            var near = pulses.OrderBy(p => Math.Abs(p.DelayMs - lastHit)).First();
            if (Math.Abs(near.DelayMs - lastHit) <= 150)
                target = near;
        }

        var maxStrong = pulses.Max(p => p.Strong);
        var maxWeak = pulses.Max(p => p.Weak);
        target.Strong = Math.Max(target.Strong, Math.Max(maxStrong, FinisherLevel));
        target.Weak = Math.Max(target.Weak, Math.Max(maxWeak, FinisherLevel));
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
                // A sound carries a strike with no attack of its own (a whoosh over several hits):
                // split the clip on the hit and make the part from the hit on a full, stronger pulse.
                var end = inside.DelayMs + inside.DurationMs;
                if ((hit.Sources & impacts) != 0 && at - inside.DelayMs >= 50)
                {
                    var nextStart = result.Where(c => c.DelayMs > at).Select(c => c.DelayMs).DefaultIfEmpty(int.MaxValue).Min();
                    var accent = inside.Clone();
                    accent.DelayMs = at;
                    accent.DurationMs = Math.Min(Math.Max(end - at, MinPulseMs), nextStart - at);
                    accent.Strong = Math.Min(100, (int)MathF.Round(Math.Max(inside.Strong, 10) * 1.15f));
                    accent.Weak = Math.Min(100, (int)MathF.Round(Math.Max(inside.Weak, 10) * 1.15f));
                    accent.Shape = PulseShape.FadeOut;
                    inside.DurationMs = at - inside.DelayMs;
                    result.Add(accent);
                }

                continue;
            }

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

    private static int StartMs(HitPoint hit) => hit.Ms;

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
