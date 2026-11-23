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

/// <summary>
/// Cuts a skill's sound into vibration clips. A skill's sound usually has a loud bed (reverb,
/// held tones) with louder bumps on top: the hits. Each bump above the bed becomes a solid
/// block at the sound's level, followed by a short fade-out. The bed itself doesn't vibrate.
/// </summary>
public static class SoundSegmenter
{
    /// <summary>Below this level nothing vibrates.</summary>
    public const float Floor = 0.08f;

    /// <summary>The loudness is smoothed over this window first, so ripples don't flicker.</summary>
    public const int SmoothMs = 30;

    /// <summary>How far each way to look for the dips that make a peak stand out.</summary>
    public const int ProminenceWindowMs = 400;

    /// <summary>A block is at most this long before its fade-out, so a broad bump doesn't become a buzz.</summary>
    public const int MaxBlockMs = 250;

    /// <summary>The stronger motor of a block is at least this (%), the other in proportion.</summary>
    public const int MinBlockLevel = 25;

    /// <summary>A block covers the top of its bump: this share of the bump's height.</summary>
    public const float TopShare = 0.3f;

    public const int MinClipMs = 60;
    public const int MergeBelowMs = 40;
    public const int MinGapMs = 25;

    /// <summary>Quiet left before the next block's start.</summary>
    public const int GapBeforeBlockMs = 30;

    public sealed record Result(List<VibrationPulse> Clips, List<int> Marks);

    public static Result Segment(SkillSound sound, SoundTuning tuning)
    {
        const int step = SoundEnvelope.StepMs;
        var n = sound.Low.Length;
        if (n == 0)
            return new Result(new(), new());

        // Bumps: the clear peaks; each block covers the top of its bump.
        var (peaks, e) = FindPeaks(sound, tuning.Threshold);
        var regions = new List<(int Start, int End)>();
        foreach (var (i, prominence) in peaks)
        {
            var level = e[i] - prominence * TopShare;
            var s0 = i;
            while (s0 > 0 && e[s0 - 1] >= level)
                s0--;
            var s1 = i + 1;
            while (s1 < n && e[s1] >= level)
                s1++;
            regions.Add((s0, s1));
        }

        regions.Sort((x, y) => x.Start.CompareTo(y.Start));
        for (var i = regions.Count - 1; i > 0; i--)
        {
            // Overlapping tops of the same bump.
            if (regions[i].Start < regions[i - 1].End)
            {
                regions[i - 1] = (regions[i - 1].Start, Math.Max(regions[i - 1].End, regions[i].End));
                regions.RemoveAt(i);
            }
        }

        // Bumps close together are one block.
        var joinSteps = Math.Max(0, tuning.JoinGapMs / step);
        for (var i = regions.Count - 1; i > 0; i--)
        {
            if (regions[i].Start - regions[i - 1].End <= joinSteps)
            {
                regions[i - 1] = (regions[i - 1].Start, regions[i].End);
                regions.RemoveAt(i);
            }
        }

        // Each block: solid at the sound's level, then a short fade-out.
        var pulses = new List<VibrationPulse>();
        var tailSteps = Math.Max(0, tuning.FadeTailMs / step);
        var gapSteps = GapBeforeBlockMs / step;
        for (var r = 0; r < regions.Count; r++)
        {
            var (s0, s1) = regions[r];
            var limit = r + 1 < regions.Count ? regions[r + 1].Start - gapSteps : n + tailSteps;
            s1 = Math.Max(s0 + 1, Math.Min(Math.Min(s1, limit), s0 + MaxBlockMs / step));
            var strong = Loud(sound.Low, s0, s1);
            var weak = Loud(sound.High, s0, s1);
            if (strong <= 0 && weak <= 0)
                continue;

            // A bump clear enough to be found is a hit worth feeling, even in a quiet sound.
            var top = Math.Max(strong, weak);
            if (top < MinBlockLevel)
            {
                strong = (int)MathF.Round(strong * MinBlockLevel / (float)top);
                weak = (int)MathF.Round(weak * MinBlockLevel / (float)top);
            }

            pulses.Add(new VibrationPulse
            {
                DelayMs = s0 * step,
                DurationMs = (s1 - s0) * step,
                Strong = strong,
                Weak = weak,
                Shape = PulseShape.Constant,
            });

            var tail = Math.Min(tailSteps, limit - s1);
            if (tail * step >= 20)
            {
                pulses.Add(new VibrationPulse
                {
                    DelayMs = s1 * step,
                    DurationMs = tail * step,
                    Strong = strong,
                    Weak = weak,
                    Shape = PulseShape.FadeOut,
                });
            }
        }

        Tidy(pulses);
        var marks = pulses.SelectMany(p => new[] { p.DelayMs, p.DelayMs + p.DurationMs }).Distinct().ToList();
        return new Result(pulses, marks);
    }

    /// <summary>
    /// The clear peaks of a skill's sound: local maxima of the smoothed loudness that rise at least
    /// <paramref name="threshold"/> above the dips on both sides (prominence, looking at most
    /// <see cref="ProminenceWindowMs"/> each way). Returns the peaks (10 ms step index, prominence)
    /// and the smoothed loudness.
    /// </summary>
    public static (List<(int Index, float Prominence)> Peaks, float[] Loudness) FindPeaks(SkillSound sound, float threshold)
    {
        const int step = SoundEnvelope.StepMs;
        var n = sound.Low.Length;
        var peaks = new List<(int, float)>();
        var raw = new float[n];
        for (var i = 0; i < n; i++)
            raw[i] = Math.Max(sound.Low[i], sound.High[i]);
        var half = Math.Max(1, SmoothMs / step / 2);
        var e = new float[n];
        for (var i = 0; i < n; i++)
        {
            float sum = 0;
            var c = 0;
            for (var k = Math.Max(0, i - half); k <= Math.Min(n - 1, i + half); k++)
            {
                sum += raw[k];
                c++;
            }

            e[i] = sum / c;
        }

        var w = ProminenceWindowMs / step;
        for (var i = 0; i < n; i++)
        {
            var peak = e[i];
            if (peak < Floor || (i > 0 && e[i - 1] >= peak) || (i + 1 < n && e[i + 1] > peak))
                continue;

            var leftMin = peak;
            for (var k = i - 1; k >= Math.Max(-1, i - w); k--)
            {
                var v = k < 0 ? 0f : e[k];
                if (v > peak)
                    break;
                leftMin = Math.Min(leftMin, v);
            }

            var rightMin = peak;
            for (var k = i + 1; k <= Math.Min(n, i + w); k++)
            {
                var v = k >= n ? 0f : e[k];
                if (v > peak)
                    break;
                rightMin = Math.Min(rightMin, v);
            }

            var prominence = peak - Math.Max(leftMin, rightMin);
            if (prominence >= threshold)
                peaks.Add((i, prominence));
        }

        return (peaks, e);
    }

    /// <summary>The loud part of a band in a block (80th percentile), as 0..100.</summary>
    private static int Loud(float[] band, int a, int b)
    {
        var vals = new List<float>(b - a);
        for (var i = a; i < b && i < band.Length; i++)
            vals.Add(band[i]);
        if (vals.Count == 0)
            return 0;
        vals.Sort();
        return (int)MathF.Round(vals[(int)(0.8f * (vals.Count - 1))] * 100);
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
