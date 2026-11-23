using System;
using System.Collections.Generic;
using System.Linq;

namespace XIVibration.Audio;

/// <summary>
/// Loudness of one sound over time, in 10 ms steps, split in two bands:
/// bass (drives the strong, heavy motor) and the rest (drives the weak, buzzing motor).
/// Values are linear RMS (0..1) so several sounds can be mixed.
/// </summary>
public sealed class SoundEnvelope
{
    public const int StepMs = 10;

    /// <summary>Bass / treble split, Hz.</summary>
    public const float CrossoverHz = 200f;

    public required float[] Low { get; init; }

    public required float[] High { get; init; }

    public int LengthMs => this.Low.Length * StepMs;

    public static SoundEnvelope From(DecodedSound sound)
    {
        var step = Math.Max(1, sound.SampleRate * StepMs / 1000);
        var n = (sound.Samples.Length + step - 1) / step;
        var low = new float[n];
        var high = new float[n];
        var a = 1f - MathF.Exp(-2f * MathF.PI * CrossoverHz / sound.SampleRate);
        var lp = 0f;
        for (var w = 0; w < n; w++)
        {
            double sl = 0, sh = 0;
            var end = Math.Min(sound.Samples.Length, (w + 1) * step);
            var count = Math.Max(1, end - w * step);
            for (var i = w * step; i < end; i++)
            {
                var x = sound.Samples[i];
                lp += a * (x - lp);
                sl += lp * lp;
                sh += (x - lp) * (x - lp);
            }

            low[w] = (float)Math.Sqrt(sl / count);
            high[w] = (float)Math.Sqrt(sh / count);
        }

        return new SoundEnvelope { Low = low, High = high };
    }
}

/// <summary>One sound a skill plays.</summary>
public sealed record SkillSoundRef(string GamePath, string Resolved, bool FromMod, int StartFrame, bool Decoded);

/// <summary>
/// All sounds of a skill mixed together on the animation's time line, as motor levels 0..1
/// (10 ms steps). This is what the vibration follows.
/// </summary>
public sealed class SkillSound
{
    /// <summary>
    /// Loudness range mapped to 0..100% (dBFS, 10 ms RMS). Game sound files are mastered loud
    /// (skill sounds peak around -5 dB), so the range is wide and curved: attacks feel strong,
    /// tails and quiet layers clearly weaker.
    /// </summary>
    public const float FloorDb = -40f;
    public const float FullDb = -2f;
    public const float Curve = 1.6f;

    /// <summary>Bass is felt more than heard: give the strong motor a head start.</summary>
    public const float BassBoostDb = 6f;

    public required float[] Low { get; init; }

    public required float[] High { get; init; }

    public required IReadOnlyList<SkillSoundRef> Sounds { get; init; }

    public int LengthMs => this.Low.Length * SoundEnvelope.StepMs;

    public static float Level(float rms, float boostDb = 0f)
    {
        if (rms <= 1e-6f)
            return 0f;
        var db = 20f * MathF.Log10(rms) + boostDb;
        return MathF.Pow(Math.Clamp((db - FloorDb) / (FullDb - FloorDb), 0f, 1f), Curve);
    }

    /// <summary>Mixes sounds placed at their start times (ms from the animation start).</summary>
    public static SkillSound? Mix(IReadOnlyList<(int StartMs, SoundEnvelope Envelope)> sounds, IReadOnlyList<SkillSoundRef> refs)
    {
        if (sounds.Count == 0)
            return null;

        var steps = sounds.Max(s => s.StartMs / SoundEnvelope.StepMs + s.Envelope.Low.Length) + 1;
        var low2 = new double[steps];
        var high2 = new double[steps];
        foreach (var (start, env) in sounds)
        {
            var o = Math.Max(0, (int)MathF.Round(start / (float)SoundEnvelope.StepMs));
            for (var i = 0; i < env.Low.Length && o + i < steps; i++)
            {
                low2[o + i] += env.Low[i] * env.Low[i];
                high2[o + i] += env.High[i] * env.High[i];
            }
        }

        var low = new float[steps];
        var high = new float[steps];
        for (var i = 0; i < steps; i++)
        {
            low[i] = Level((float)Math.Sqrt(low2[i]), BassBoostDb);
            high[i] = Level((float)Math.Sqrt(high2[i]));
        }

        return new SkillSound { Low = low, High = high, Sounds = refs };
    }
}

/// <summary>Cuts a skill's sound into vibration clips: each starts where a sound hits, ends where it fades.</summary>
public static class SoundSegmenter
{
    /// <summary>Below this level nothing vibrates.</summary>
    public const float Floor = 0.08f;

    /// <summary>A rise this big within <see cref="RiseWindowMs"/> is an attack and starts a new clip.</summary>
    public const float Rise = 0.2f;

    public const int RiseWindowMs = 60;

    /// <summary>A clip ends when the sound falls below this share of the clip's peak.</summary>
    public const float EndShare = 0.35f;

    public const int ReleaseMs = 40;

    public const int MinClipMs = 60;
    public const int MergeBelowMs = 40;
    public const int MinGapMs = 25;

    public sealed record Result(List<VibrationPulse> Clips, List<int> Marks);

    /// <summary>Inside a sound, a new segment starts when the level moves this far from the segment's average.</summary>
    public const float StepTolerance = 0.22f;

    public const int MinSegmentMs = 120;
    public const int MaxSegmentMs = 500;

    public static Result Segment(SkillSound sound)
    {
        const int step = SoundEnvelope.StepMs;
        var n = sound.Low.Length;
        var e = new float[n];
        for (var i = 0; i < n; i++)
            e[i] = Math.Max(sound.Low[i], sound.High[i]);

        // Attacks: a clear rise over the recent minimum, both absolute and relative.
        var window = Math.Max(1, RiseWindowMs / step);
        var attack = new bool[n];
        var lastAttack = -100;
        for (var t = 0; t < n; t++)
        {
            var min = t == 0 ? 0f : 1f;
            for (var k = Math.Max(0, t - window); k < t; k++)
                min = Math.Min(min, e[k]);
            var rising = e[t] >= Floor && e[t] - min >= Rise && e[t] >= min * 1.4f + 0.04f;
            if (rising && t - lastAttack >= 6)
            {
                attack[t] = true;
                lastAttack = t;
            }
        }

        // Sounding regions: from an attack until the sound fades (release follower) or the next attack.
        var release = 1f - MathF.Exp(-step / (float)ReleaseMs);
        var pulses = new List<VibrationPulse>();
        for (var t = 0; t < n; t++)
        {
            if (!attack[t])
                continue;

            var start = t;
            var peak = 0f;
            var follow = 0f;
            var end = n;
            for (var k = start; k < n; k++)
            {
                if (k > start && attack[k])
                {
                    end = k;
                    break;
                }

                follow = e[k] > follow ? e[k] : follow + (e[k] - follow) * release;
                peak = Math.Max(peak, e[k]);
                if (k > start && follow < Math.Max(Floor, EndShare * peak))
                {
                    end = k;
                    break;
                }
            }

            AddRegion(sound, e, start, Math.Max(start + 1, end), pulses);
        }

        Tidy(pulses);
        var marks = pulses.SelectMany(p => new[] { p.DelayMs, p.DelayMs + p.DurationMs }).Distinct().ToList();
        return new Result(pulses, marks);
    }

    /// <summary>
    /// One sounding region as a few segments that follow its loudness: a new segment when the
    /// level moves away from the current one, the last one fading out with the sound.
    /// </summary>
    private static void AddRegion(SkillSound sound, float[] e, int start, int end, List<VibrationPulse> pulses)
    {
        const int step = SoundEnvelope.StepMs;
        var maxLen = MaxSegmentMs / step;
        var minLen = MinSegmentMs / step;
        var segStart = start;
        var sum = 0f;
        var count = 0;
        var first = pulses.Count;

        for (var t = start; t <= end; t++)
        {
            var done = t == end;
            if (!done && count >= minLen)
            {
                // Only a sustained change (the next 30 ms) starts a new segment, not a single spike.
                var mean = sum / count;
                var ahead = 0f;
                var aheadCount = 0;
                for (var k = t; k < Math.Min(end, t + 3); k++)
                {
                    ahead += e[k];
                    aheadCount++;
                }

                ahead /= Math.Max(1, aheadCount);
                done = (end - t >= minLen && Math.Abs(ahead - mean) > StepTolerance) || t - segStart >= maxLen;
            }

            if (done && count > 0)
            {
                pulses.Add(Segment(sound, e, segStart, t));
                segStart = t;
                sum = 0;
                count = 0;
            }

            if (t < end)
            {
                sum += e[t];
                count++;
            }
        }

        // The first segment keeps the attack's shape; the last one fades with the sound.
        if (pulses.Count > first)
        {
            // A fade-out only fits the region's last segment; otherwise it would dip mid-sound.
            var firstShape = ShapeOf(e, start, Math.Min(end, start + (pulses[first].DurationMs / step)));
            pulses[first].Shape = pulses.Count - first == 1 || firstShape == PulseShape.FadeIn ? firstShape : PulseShape.Constant;
            var last = pulses[^1];
            var ls = last.DelayMs / step;
            if (ls + 1 < end && e[end - 1] < 0.6f * e[ls])
                last.Shape = PulseShape.FadeOut;
        }
    }

    private static VibrationPulse Segment(SkillSound sound, float[] e, int s, int en)
    {
        // Levels: the loud part of the segment (average of its upper half), per motor.
        static float Loud(float[] v, int a, int b)
        {
            var vals = new List<float>(b - a);
            for (var i = a; i < b; i++)
                vals.Add(v[i]);
            vals.Sort();
            var half = vals.Skip(vals.Count / 2).ToList();
            return half.Count == 0 ? 0 : half.Average();
        }

        return new VibrationPulse
        {
            DelayMs = s * SoundEnvelope.StepMs,
            DurationMs = (en - s) * SoundEnvelope.StepMs,
            Strong = (int)MathF.Round(Loud(sound.Low, s, en) * 100),
            Weak = (int)MathF.Round(Loud(sound.High, s, en) * 100),
            Shape = PulseShape.Constant,
        };
    }

    /// <summary>Make clips motor-friendly: no tiny clips, no tiny gaps.</summary>
    public static void Tidy(List<VibrationPulse> pulses)
    {
        pulses.Sort((a, b) => a.DelayMs.CompareTo(b.DelayMs));

        // Tiny clips join the previous one when it's right before them.
        for (var i = pulses.Count - 1; i > 0; i--)
        {
            var p = pulses[i];
            var prev = pulses[i - 1];
            if (p.DurationMs < MergeBelowMs && p.DelayMs - (prev.DelayMs + prev.DurationMs) < MinGapMs)
            {
                prev.DurationMs = p.DelayMs + p.DurationMs - prev.DelayMs;
                prev.Strong = Math.Max(prev.Strong, p.Strong);
                prev.Weak = Math.Max(prev.Weak, p.Weak);
                pulses.RemoveAt(i);
            }
        }

        for (var i = 0; i < pulses.Count; i++)
        {
            var p = pulses[i];
            var next = i + 1 < pulses.Count ? pulses[i + 1] : null;
            var room = next == null ? int.MaxValue : next.DelayMs - p.DelayMs;

            // At least MinClipMs, but never into the next clip.
            p.DurationMs = Math.Min(Math.Max(p.DurationMs, MinClipMs), room);

            // A gap too small to feel: close it.
            if (next != null && next.DelayMs - (p.DelayMs + p.DurationMs) < MinGapMs)
                p.DurationMs = room;
        }

        pulses.RemoveAll(p => p.DurationMs <= 0 || (p.Strong <= 0 && p.Weak <= 0));
    }

    private static PulseShape ShapeOf(float[] e, int s, int en)
    {
        var len = en - s;
        if (len < 4)
            return PulseShape.Constant;

        var peakAt = s;
        for (var t = s; t < en; t++)
        {
            if (e[t] > e[peakAt])
                peakAt = t;
        }

        var peak = Math.Max(1e-3f, e[peakAt]);
        var q = Math.Max(1, len / 4);
        var head = Avg(e, s, s + q) / peak;
        var tail = Avg(e, en - q, en) / peak;
        var pos = (peakAt - s) / (float)len;

        if (pos <= 0.3f && tail < 0.6f) return PulseShape.FadeOut;
        if (pos >= 0.7f && head < 0.6f) return PulseShape.FadeIn;
        if (head < 0.7f && tail < 0.7f) return PulseShape.Bump;
        return PulseShape.Constant;
    }

    private static float Avg(float[] e, int a, int b)
    {
        var sum = 0f;
        var c = 0;
        for (var i = Math.Max(0, a); i < Math.Min(e.Length, b); i++)
        {
            sum += e[i];
            c++;
        }

        return c == 0 ? 0 : sum / c;
    }
}

/// <summary>Finds, reads and mixes all sounds (C063) of a timeline. Shared by the plugin and offline checks.</summary>
public static class SkillSoundBuilder
{
    /// <param name="read">Reads a game path the way the game would (mods included): data, resolved path, from a mod.</param>
    /// <param name="cache">Decoded sounds by resolved path, size and index (null = could not decode).</param>
    /// <param name="signature">Gets each sound's resolved path and size appended.</param>
    public static SkillSound? Build(
        IReadOnlyList<Timeline.TmbParser.Entry> entries,
        Func<string, (byte[]? Data, string Resolved, bool FromMod)> read,
        Dictionary<string, SoundEnvelope?> cache,
        System.Text.StringBuilder? signature = null)
    {
        var placed = new List<(int StartMs, SoundEnvelope Envelope)>();
        var refs = new List<SkillSoundRef>();
        foreach (var e in entries)
        {
            if (e.Tag != "C063" || !e.Path.EndsWith(".scd", StringComparison.OrdinalIgnoreCase))
                continue;

            var index = e.Fields.Length > 3 ? Math.Max(0, e.Fields[3]) : 0;
            var (data, resolved, fromMod) = read(e.Path);
            signature?.Append($"|{resolved}:{data?.Length ?? -1}");
            SoundEnvelope? env = null;
            if (data != null)
            {
                var key = $"{resolved}:{data.Length}:{index}";
                if (!cache.TryGetValue(key, out env))
                {
                    try
                    {
                        var decoded = ScdReader.Decode(data, index);
                        env = decoded == null || decoded.Samples.Length == 0 ? null : SoundEnvelope.From(decoded);
                    }
                    catch
                    {
                        env = null;
                    }

                    cache[key] = env;
                }
            }

            refs.Add(new SkillSoundRef(e.Path, resolved, fromMod, e.TimeFrames, env != null));
            if (env != null)
                placed.Add(((int)MathF.Round(e.TimeFrames * Timeline.TmbParser.MsPerFrame), env));
        }

        return placed.Count == 0 ? (refs.Count == 0 ? null : new SkillSound { Low = Array.Empty<float>(), High = Array.Empty<float>(), Sounds = refs })
            : SkillSound.Mix(placed, refs);
    }
}
