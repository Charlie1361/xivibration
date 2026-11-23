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
/// Cuts a skill's sound into vibration clips: one per attack. A clip starts where a sound hits,
/// rises with it to its peak and stops shortly after: when the sound drops by
/// <see cref="SoundTuning.CutDrop"/> or after <see cref="SoundTuning.TailMs"/>, whichever comes first.
/// The sustained part of a sound (reverb, a held tone) doesn't vibrate: only the excess over a slow
/// background follower can start a clip.
/// </summary>
public static class SoundSegmenter
{
    /// <summary>Below this level nothing vibrates.</summary>
    public const float Floor = 0.08f;

    /// <summary>An attack is a jump of <see cref="SoundTuning.AttackThreshold"/> within this window.</summary>
    public const int RiseWindowMs = 60;

    public const int MinAttackSpacingMs = 90;

    /// <summary>How long after an attack the level may keep rising to its peak.</summary>
    public const int MaxRiseMs = 120;

    /// <summary>A rise longer than this is a swell: it gets its own fade-in clip.</summary>
    public const int SwellMs = 40;

    /// <summary>The background follows the sound slowly up and quickly down.</summary>
    public const int BackgroundRiseMs = 250;
    public const int BackgroundFallMs = 150;

    public const int MinClipMs = 60;
    public const int MergeBelowMs = 40;
    public const int MinGapMs = 25;

    /// <summary>Quiet before each attack, so back-to-back attacks are felt one by one.</summary>
    public const int GapBeforeAttackMs = 30;

    public sealed record Result(List<VibrationPulse> Clips, List<int> Marks);

    public static Result Segment(SkillSound sound, SoundTuning tuning)
    {
        const int step = SoundEnvelope.StepMs;
        var n = sound.Low.Length;
        var e = new float[n];
        for (var i = 0; i < n; i++)
            e[i] = Math.Max(sound.Low[i], sound.High[i]);

        // Excess over the background: what stands out from the sustained sound.
        var up = 1f - MathF.Exp(-step / (float)BackgroundRiseMs);
        var down = 1f - MathF.Exp(-step / (float)BackgroundFallMs);
        var x = new float[n];
        var bg = 0f;
        for (var t = 0; t < n; t++)
        {
            x[t] = Math.Max(0f, e[t] - bg);
            bg += (e[t] - bg) * (e[t] > bg ? up : down);
        }

        // Attacks: the excess jumps by the threshold within the rise window.
        var window = Math.Max(1, RiseWindowMs / step);
        var attacks = new List<int>();
        for (var t = 0; t < n; t++)
        {
            var min = t == 0 ? 0f : float.MaxValue;
            for (var k = Math.Max(0, t - window); k < t; k++)
                min = Math.Min(min, x[k]);
            if (e[t] >= Floor && x[t] - min >= tuning.AttackThreshold
                && (attacks.Count == 0 || (t - attacks[^1]) * step >= MinAttackSpacingMs))
                attacks.Add(t);
        }

        var pulses = new List<VibrationPulse>();
        for (var i = 0; i < attacks.Count; i++)
        {
            var t0 = attacks[i];
            var next = i + 1 < attacks.Count ? attacks[i + 1] : n;
            var stop = i + 1 < attacks.Count ? Math.Max(t0 + MinClipMs / step, next - GapBeforeAttackMs / step) : n;

            // Peak: follow the rise for a little while.
            var tp = t0;
            for (var k = t0; k < Math.Min(next, t0 + MaxRiseMs / step + 1); k++)
            {
                if (e[k] >= e[tp])
                    tp = k;
                else if (e[k] < 0.9f * e[tp])
                    break;
            }

            // Cut-off: the sound dropped enough, the tail is over, or the next attack comes.
            var peak = e[tp];
            var end = Math.Min(stop, tp + Math.Max(1, tuning.TailMs / step) + 1);
            for (var k = tp + 1; k < end; k++)
            {
                if (e[k] < peak * (1f - tuning.CutDrop))
                {
                    end = k;
                    break;
                }
            }

            end = Math.Max(end, t0 + 1);
            var strong = 0f;
            var weak = 0f;
            for (var k = t0; k < end; k++)
            {
                strong = Math.Max(strong, sound.Low[k]);
                weak = Math.Max(weak, sound.High[k]);
            }

            var s = (int)MathF.Round(strong * 100);
            var w = (int)MathF.Round(weak * 100);
            if ((tp - t0) * step > SwellMs && tp < end)
            {
                pulses.Add(new VibrationPulse { DelayMs = t0 * step, DurationMs = (tp - t0) * step, Strong = s, Weak = w, Shape = PulseShape.FadeIn });
                pulses.Add(new VibrationPulse { DelayMs = tp * step, DurationMs = Math.Max(step, (end - tp) * step), Strong = s, Weak = w, Shape = PulseShape.FadeOut });
            }
            else
            {
                pulses.Add(new VibrationPulse { DelayMs = t0 * step, DurationMs = (end - t0) * step, Strong = s, Weak = w, Shape = PulseShape.FadeOut });
            }
        }

        Tidy(pulses);
        var marks = pulses.SelectMany(p => new[] { p.DelayMs, p.DelayMs + p.DurationMs }).Distinct().ToList();
        return new Result(pulses, marks);
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
