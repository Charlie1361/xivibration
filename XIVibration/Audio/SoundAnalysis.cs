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
/// <param name="EffectPath">The .avfx effect that plays it, or null when the animation timeline plays it directly.</param>
/// <param name="Exists">The file was found (in the game or a mod); false for paths that lead nowhere.</param>
public sealed record SkillSoundRef(string GamePath, string Resolved, bool FromMod, int StartFrame, bool Decoded, int SoundIndex = 0, string? EffectPath = null, bool Exists = true)
{
    /// <summary>Identifies this sound in this skill (for muting it in Test).</summary>
    public string Key => $"{this.GamePath}@{this.StartFrame}#{this.SoundIndex}";

    public bool FromEffect => this.EffectPath != null;
}

/// <summary>One sound file of a skill on the timeline: where it starts and its own loudness (null if unreadable).</summary>
public sealed record SoundTrack(SkillSoundRef Ref, int StartMs, SoundEnvelope? Envelope);

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

    /// <summary>Every sound file on its own, for the editor's audio lanes.</summary>
    public IReadOnlyList<SoundTrack> Tracks { get; init; } = Array.Empty<SoundTrack>();

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

    /// <summary>Peaks closer than this are one hit (the loudest one).</summary>
    public const int PeakGroupMs = 120;

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
        var (peaks, e) = FindPeaks(sound, tuning.Threshold, tuning.PeakWindow);
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
        var joinSteps = Math.Max(0, SoundTuning.JoinGapMs / step);
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
    /// <param name="peakWindow">
    /// When set, only peaks within this share of the loudest peak are kept (0.2: at least 80% as loud),
    /// judged over the whole sound of the skill.
    /// </param>
    public static (List<(int Index, float Prominence)> Peaks, float[] Loudness) FindPeaks(SkillSound sound, float threshold, float? peakWindow = null)
    {
        const int step = SoundEnvelope.StepMs;
        var n = sound.Low.Length;
        var peaks = new List<(int Index, float Prominence)>();
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
            if (prominence < threshold)
                continue;

            // The hit goes on the loudest moment of the bump's top, not its first ripple.
            var level = peak - prominence * TopShare;
            var a = i;
            while (a > 0 && e[a - 1] >= level)
                a--;
            var b = i;
            while (b + 1 < n && e[b + 1] >= level)
                b++;
            var loudest = i;
            for (var k = a; k <= b; k++)
            {
                if (raw[k] > raw[loudest])
                    loudest = k;
            }

            // Climb to the raw maximum next to it (the smoothed edge can cut it off).
            for (var guard = 0; guard < 5; guard++)
            {
                if (loudest > 0 && raw[loudest - 1] > raw[loudest])
                    loudest--;
                else if (loudest + 1 < n && raw[loudest + 1] > raw[loudest])
                    loudest++;
                else
                    break;
            }

            peaks.Add((loudest, prominence));
        }

        // Close peaks are one hit: keep the loudest of each group.
        var group = Math.Max(1, PeakGroupMs / step);
        var kept = new List<(int Index, float Prominence)>();
        foreach (var p in peaks.OrderBy(p => p.Index))
        {
            if (kept.Count > 0 && p.Index - kept[^1].Index < group)
            {
                var last = kept[^1];
                if (raw[p.Index] > raw[last.Index] || (raw[p.Index] == raw[last.Index] && p.Prominence > last.Prominence))
                    kept[^1] = p;
                continue;
            }

            if (kept.Count == 0 || kept[^1].Index != p.Index)
                kept.Add(p);
        }

        // Only peaks about as loud as the loudest one: no quiet echoes or side ripples.
        if (peakWindow is { } wnd && kept.Count > 0)
        {
            var loudest = kept.Max(p => raw[p.Index]);
            var floor = loudest * (1f - Math.Clamp(wnd, 0f, 1f));
            kept = kept.Where(p => raw[p.Index] >= floor).ToList();
        }

        return (kept, e);
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
    /// <summary>Most effects are spawned at most a few times per skill; no need to follow deeper.</summary>
    private const int MaxEffects = 40;

    /// <param name="read">Reads a game path the way the game would (mods included): data, resolved path, from a mod.</param>
    /// <param name="cache">Decoded sounds by resolved path, size and index (null = could not decode).</param>
    /// <param name="signature">Gets each sound's and effect's resolved path and size appended.</param>
    /// <param name="effectCache">Sounds found in effect files, by resolved path and size.</param>
    public static SkillSound? Build(
        IReadOnlyList<Timeline.TmbParser.Entry> entries,
        Func<string, (byte[]? Data, string Resolved, bool FromMod)> read,
        Dictionary<string, SoundEnvelope?> cache,
        System.Text.StringBuilder? signature = null,
        Dictionary<string, List<EffectSound>>? effectCache = null)
    {
        var placed = new List<(int StartMs, SoundEnvelope Envelope)>();
        var refs = new List<SkillSoundRef>();
        var tracks = new List<SoundTrack>();
        var seen = new HashSet<string>();

        void AddSound(string path, int index, int startFrame, string? effect)
        {
            // The same sound at the same moment (an effect listed twice) plays once.
            if (!seen.Add($"{path.ToLowerInvariant()}@{startFrame}#{index}"))
                return;

            var (data, resolved, fromMod) = read(path);
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

            var sref = new SkillSoundRef(path, resolved, fromMod, startFrame, env != null, index, effect, data != null);
            var startMs = (int)MathF.Round(startFrame * Timeline.TmbParser.MsPerFrame);
            refs.Add(sref);
            tracks.Add(new SoundTrack(sref, startMs, env));
            if (env != null)
                placed.Add((startMs, env));
        }

        // Sounds the animation timeline plays itself.
        foreach (var e in entries)
        {
            if (e.Tag == "C063" && e.Path.EndsWith(".scd", StringComparison.OrdinalIgnoreCase))
                AddSound(e.Path, e.Fields.Length > 3 ? Math.Max(0, e.Fields[3]) : 0, e.TimeFrames, null);
        }

        // Sounds played by the effects (.avfx) the animation spawns: mods often put the strikes here.
        var effects = 0;
        foreach (var e in entries)
        {
            if (e.Tag != "C012" || !e.Path.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase) || effects++ >= MaxEffects)
                continue;

            // The shared start flash every skill spawns: never the skill's own sound.
            if (Timeline.HitAnalyzer.IsGenericVfx(e.Path))
                continue;

            var (data, resolved, _) = read(e.Path);
            signature?.Append($"|{resolved}:{data?.Length ?? -1}");
            if (data == null)
                continue;

            List<EffectSound> sounds;
            var key = $"{resolved}:{data.Length}";
            if (effectCache == null || !effectCache.TryGetValue(key, out sounds!))
            {
                try
                {
                    sounds = AvfxSounds.Parse(data);
                }
                catch
                {
                    sounds = new List<EffectSound>();
                }

                if (effectCache != null)
                    effectCache[key] = sounds;
            }

            foreach (var snd in sounds)
                AddSound(snd.Path, snd.Index, e.TimeFrames + snd.StartFrame, e.Path);
        }

        tracks = tracks.OrderBy(t => t.StartMs).ToList();
        if (placed.Count == 0)
            return refs.Count == 0 ? null : new SkillSound { Low = Array.Empty<float>(), High = Array.Empty<float>(), Sounds = refs, Tracks = tracks };

        var mix = SkillSound.Mix(placed, refs)!;
        return new SkillSound { Low = mix.Low, High = mix.High, Sounds = refs, Tracks = tracks };
    }

    /// <summary>
    /// Where each separate burst of a sound file starts, in ms from the file's start. A file can hold
    /// several strikes; a new one begins only after a real pause (at least <see cref="BurstGapMs"/> below
    /// 15% of the file's peak) followed by a rise to half the peak or more. Tails and ripples never split.
    /// </summary>
    public static List<int> BurstOnsetsMs(SoundEnvelope env)
    {
        var level = new float[env.Low.Length];
        var peak = 0f;
        for (var i = 0; i < level.Length; i++)
        {
            level[i] = Math.Max(SkillSound.Level(env.Low[i], SkillSound.BassBoostDb), SkillSound.Level(env.High[i]));
            peak = Math.Max(peak, level[i]);
        }

        var onsets = new List<int>();
        if (peak <= 0f)
            return onsets;

        var gapSteps = BurstGapMs / SoundEnvelope.StepMs;
        var inBurst = false;
        var quiet = 0;
        for (var i = 0; i < level.Length; i++)
        {
            if (!inBurst)
            {
                if (level[i] >= peak * 0.5f)
                {
                    onsets.Add(i * SoundEnvelope.StepMs);
                    inBurst = true;
                    quiet = 0;
                }
            }
            else if (level[i] < peak * 0.15f)
            {
                if (++quiet >= gapSteps)
                    inBurst = false;
            }
            else
            {
                quiet = 0;
            }
        }

        return onsets;
    }

    /// <summary>How long a pause inside a sound file must be to start a new strike.</summary>
    public const int BurstGapMs = 60;

    /// <summary>
    /// When a sound really starts: the first 10 ms step at half of its peak loudness or more
    /// (files often begin with a little silence). Milliseconds from the file's start.
    /// </summary>
    public static int OnsetMs(SoundEnvelope env)
    {
        var level = new float[env.Low.Length];
        var peak = 0f;
        for (var i = 0; i < level.Length; i++)
        {
            level[i] = Math.Max(SkillSound.Level(env.Low[i], SkillSound.BassBoostDb), SkillSound.Level(env.High[i]));
            peak = Math.Max(peak, level[i]);
        }

        for (var i = 0; i < level.Length; i++)
        {
            if (level[i] >= peak * 0.5f)
                return i * SoundEnvelope.StepMs;
        }

        return 0;
    }
}
