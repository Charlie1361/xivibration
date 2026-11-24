using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace XIVibration.Timeline;

[Flags]
public enum HitSource
{
    None = 0,
    FlyText = 1,
    Flinch = 2,
    TargetVfx = 4,
    TargetSound = 8,
    BuiltInVibration = 16,
    MultiHit = 32,
    CasterVfx = 64,
    ExtraStrike = 128,

    /// <summary>A peak in the skill's sound, used when the animation has no hit markers.</summary>
    SoundPeak = 256,

    /// <summary>A strike sound played by an effect (.avfx) the animation spawns.</summary>
    EffectStrike = 512,
}

/// <summary>One moment the controller should hit. Weight 0..1 is how hard.</summary>
/// <param name="ExactMs">Exact time when known finer than a frame (sound peaks).</param>
public sealed record HitPoint(int Frame, float Weight, HitSource Sources, int? ExactMs = null)
{
    public int Ms => this.ExactMs ?? (int)MathF.Round(this.Frame * TmbParser.MsPerFrame);
}

/// <summary>What the analysis found.</summary>
/// <param name="Hits">Moments that passed the rules.</param>
/// <param name="Rejected">Moments that had cues but didn't pass the rules (shown faintly in the editor).</param>
/// <param name="HasMarkers">At least one kept hit has a real marker (not only a damage number or effect beat).</param>
/// <param name="FlyTextFrame">First damage-number frame, or -1.</param>
public sealed record HitAnalysis(List<HitPoint> Hits, List<HitPoint> Rejected, bool HasMarkers, int FlyTextFrame);

/// <summary>
/// Finds every hit in an animation timeline. A game timeline marks the damage number
/// (C011) only once, so the visible hits come from the events around them:
/// - C067 Flinch: the target's hit reaction
/// - VFX spawned on the target (file names like ..._t0p.avfx, ..._t1t.avfx)
/// - sounds played on the target (..._t.scd, ..._mt.scd)
/// - C120 vibration entries an animation already has
/// - per-hit tables from the MultiHit plugin, counted from the damage number
/// - strike sounds played by the animation's effects (.avfx)
/// Skills that don't hit anything (buffs, heals) use the caster's VFX beats instead.
/// </summary>
public static partial class HitAnalyzer
{
    /// <summary>Bump when the analysis changes, so saved patterns get rebuilt.</summary>
    public const int AnalyzerVersion = 13;

    /// <summary>Real hit markers. A damage number or an effect beat alone doesn't make a skill "have markers".</summary>
    public const HitSource Markers = HitSource.Flinch | HitSource.TargetVfx | HitSource.TargetSound | HitSource.ExtraStrike
                                     | HitSource.BuiltInVibration | HitSource.MultiHit | HitSource.EffectStrike;

    /// <summary>A MultiHit table: hits counted from the damage number, plus the optional final hit.</summary>
    public sealed record MultiHitTable(IReadOnlyList<(int Frame, int Percent)> Hits, int FinalDelayFrames);

    public static HitAnalysis Analyze(IReadOnlyList<TmbParser.Entry> entries, bool hostile, MultiHitTable? multiHit, int lengthFrames, HitDetection rules, IReadOnlyList<(int Frame, HitSource Source)>? soundCues = null)
    {
        var chosen = rules.EffectiveSources;
        var flyText = entries.Where(e => e.Tag == "C011").Select(e => e.TimeFrames).DefaultIfEmpty(-1).Min();

        // Only the chosen cues take part, so cues that are switched off can't shift the others.
        var events = new List<(int Frame, float Weight, HitSource Source)>();
        void Add(HitSource src, int frame, float weight)
        {
            if ((chosen & src) != 0)
                events.Add((frame, weight, src));
        }

        foreach (var e in entries)
        {
            switch (e.Tag)
            {
                case "C011":
                    Add(HitSource.FlyText, e.TimeFrames, 1f);
                    break;
                case "C067":
                    Add(HitSource.Flinch, e.TimeFrames, 0.9f);
                    break;
                case "C012" when IsTargetVfx(e.Path):
                    Add(HitSource.TargetVfx, e.TimeFrames, 0.7f);
                    break;
                case "C063" when IsTargetSound(e.Path):
                    Add(HitSource.TargetSound, e.TimeFrames, 0.7f);
                    break;
                case "C063" when IsExtraStrikeSound(e.Path):
                    // A follow-up strike: often the only marker of a skill's last hit.
                    Add(HitSource.ExtraStrike, e.TimeFrames, 0.8f);
                    break;
                case "C120" when e.Fields.Length >= 3:
                    Add(HitSource.BuiltInVibration, e.TimeFrames, e.Fields[2] switch { 1 => 1f, 3 => 0.7f, _ => 0.45f });
                    break;
            }
        }

        // Cues found in the sound files themselves: strikes played by effects, and the later
        // strikes of a hit-sound file that holds several.
        foreach (var (f, src) in soundCues ?? Array.Empty<(int, HitSource)>())
            Add(src, f, src == HitSource.EffectStrike ? 0.8f : 0.7f);

        if (!hostile)
        {
            // Nothing lands on a target: follow the caster's effect beats (e.g. a shield raised, then slammed).
            foreach (var e in entries)
            {
                if (e.Tag == "C012" && e.Path.Length > 0 && !IsTargetVfx(e.Path) && !IsGenericVfx(e.Path))
                    Add(HitSource.CasterVfx, e.TimeFrames, 0.45f);
            }
        }

        // MultiHit hits are dense (2-4 frames apart) and placed by hand: keep each one.
        var hits = new List<(int Frame, float Weight, HitSource Sources)>();
        if ((chosen & HitSource.MultiHit) != 0 && multiHit is { Hits.Count: > 0 })
        {
            var start = Math.Max(0, flyText);
            var total = multiHit.Hits.Sum(h => h.Percent);
            var list = multiHit.Hits.Select(h => (Frame: start + h.Frame, (float)h.Percent)).ToList();

            // When the table doesn't add up to 100%, the rest lands as the final hit.
            var rest = 100 - total;
            if (rest >= 5)
                list.Add((start + multiHit.Hits.Max(h => h.Frame) + Math.Max(1, multiHit.FinalDelayFrames), rest));

            // A table made for a different version of the animation can run past its end.
            if (lengthFrames > 0)
                list = list.Where(h => h.Frame <= lengthFrames + 5).ToList();

            // Two entries on the same frame are one hit.
            list = list.GroupBy(h => h.Frame).Select(g => (Frame: g.Key, g.Sum(h => h.Item2))).ToList();

            var max = list.Count == 0 ? 1f : Math.Max(1f, list.Max(h => h.Item2));
            foreach (var (frame, pct) in list.OrderBy(h => h.Frame))
                hits.Add((frame, 0.3f + 0.7f * pct / max, HitSource.MultiHit));
        }

        // Timeline events: join the nearest moment within the window, or start a new one.
        var window = Math.Max(1, rules.WindowFrames);
        foreach (var e in events.OrderBy(e => e.Frame))
        {
            var near = -1;
            for (var i = 0; i < hits.Count; i++)
            {
                if (Math.Abs(hits[i].Frame - e.Frame) <= window && (near < 0 || Math.Abs(hits[i].Frame - e.Frame) < Math.Abs(hits[near].Frame - e.Frame)))
                    near = i;
            }

            if (near >= 0)
            {
                var h = hits[near];
                // A MultiHit hit keeps its own weight; timeline-only hits take the strongest event.
                var weight = (h.Sources & HitSource.MultiHit) != 0 ? h.Weight : Math.Max(h.Weight, e.Weight);
                hits[near] = (h.Frame, weight, h.Sources | e.Source);
            }
            else
            {
                hits.Add((e.Frame, e.Weight, e.Source));
            }
        }

        hits = hits.OrderBy(h => h.Frame).ToList();

        // Rules: how many of the chosen cues must meet at a moment. Cue types this animation
        // doesn't have at all don't count, or a strict rule would silence most skills.
        var present = hits.Aggregate(HitSource.None, (acc, h) => acc | h.Sources) & chosen;
        var presentCount = BitCount(present);
        var kept = new List<(int Frame, float Weight, HitSource Sources)>();
        var rejected = new List<HitPoint>();
        foreach (var h in hits)
        {
            var matches = BitCount(h.Sources & chosen);
            var pass = rules.Match switch
            {
                HitMatch.AtLeast => matches >= Math.Min(Math.Max(1, rules.MinMatches), Math.Max(1, presentCount)),
                HitMatch.All => (h.Sources & present) == present,
                _ => matches >= 1,
            };

            if (pass)
                kept.Add(h);
            else
                rejected.Add(new HitPoint(h.Frame, Math.Clamp(h.Weight, 0f, 1f), h.Sources));
        }

        // The damage number pops when the server applies the damage, often a few frames off the
        // visible impact: with real markers around, a damage number on its own isn't a hit.
        var hasMarkers = kept.Any(h => (h.Sources & Markers) != 0);
        if (hasMarkers && rules.Match == HitMatch.Any)
            kept.RemoveAll(h => h.Sources == HitSource.FlyText);

        return new HitAnalysis(
            kept.Select(h => new HitPoint(h.Frame, Math.Clamp(h.Weight, 0f, 1f), h.Sources)).ToList(),
            rejected,
            hasMarkers,
            flyText);
    }

    private static int BitCount(HitSource s) => System.Numerics.BitOperations.PopCount((uint)s);

    // ..._t0p.avfx, ..._t1t.avfx, ...t0h.avfx, ..._hit....avfx
    public static bool IsTargetVfx(string path)
    {
        if (path.Length == 0)
            return false;
        var name = Path.GetFileName(path).ToLowerInvariant();
        if (IsGenericVfx(path))
            return false;
        return TargetVfxRegex().IsMatch(name) || name.Contains("hit");
    }

    // ..._t.scd, ..._mt.scd, ..._t2.scd, ..._hit.scd
    public static bool IsTargetSound(string path)
        => path.Length > 0 && TargetSoundRegex().IsMatch(Path.GetFileName(path).ToLowerInvariant());

    // ..._add.scd, ..._c_add.scd, ..._add2.scd
    public static bool IsExtraStrikeSound(string path)
        => path.Length > 0 && ExtraStrikeRegex().IsMatch(Path.GetFileName(path).ToLowerInvariant());

    /// <summary>
    /// A strike sound inside an effect: ..._t.scd, ..._mt.scd, ..._t2.scd, a name with hit, cut,
    /// counter, slash, impact, stab or blow, or any sound of an effect on the target.
    /// Swings (swish, swing, whoosh, swoosh) are never strikes.
    /// </summary>
    public static bool IsEffectStrikeSound(string path, string? effectPath)
    {
        if (path.Length == 0)
            return false;
        var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        if (SwingRegex().IsMatch(name))
            return false;
        return EffectStrikeSuffixRegex().IsMatch(name) || StrikeWordRegex().IsMatch(name)
               || (effectPath != null && IsTargetVfx(effectPath));
    }

    [GeneratedRegex(@"swish|swing|whoosh|swoosh")]
    private static partial Regex SwingRegex();

    [GeneratedRegex(@"_m?t\d?$")]
    private static partial Regex EffectStrikeSuffixRegex();

    [GeneratedRegex(@"hit|cut|counter|slash|impact|stab|blow")]
    private static partial Regex StrikeWordRegex();

    // Shared muzzle/charge effects every skill starts with.
    private static bool IsGenericVfx(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        return name.StartsWith("cmws_") || name.StartsWith("cmab_") || name.StartsWith("cmmg_") || name.Contains("shoot");
    }

    [GeneratedRegex(@"t\d+[a-z]?\.avfx$")]
    private static partial Regex TargetVfxRegex();

    [GeneratedRegex(@"_(m?t\d?|hit\d?)\.scd$")]
    private static partial Regex TargetSoundRegex();

    [GeneratedRegex(@"_(c_)?add\d*\.scd$")]
    private static partial Regex ExtraStrikeRegex();
}
