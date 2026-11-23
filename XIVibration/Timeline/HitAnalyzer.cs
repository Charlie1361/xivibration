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
}

/// <summary>One moment the controller should hit. Weight 0..1 is how hard.</summary>
public sealed record HitPoint(int Frame, float Weight, HitSource Sources);

/// <summary>
/// Finds every hit in an animation timeline. A game timeline marks the damage number
/// (C011) only once, so the visible hits come from the events around them:
/// - C067 Flinch: the target's hit reaction
/// - VFX spawned on the target (file names like ..._t0p.avfx, ..._t1t.avfx)
/// - sounds played on the target (..._t.scd, ..._mt.scd)
/// - C120 vibration entries an animation already has
/// - per-hit tables from the MultiHit plugin, counted from the damage number
/// Skills that don't hit anything (buffs, heals) use the caster's VFX beats instead.
/// </summary>
public static partial class HitAnalyzer
{
    /// <summary>Bump when the analysis changes, so saved patterns get rebuilt.</summary>
    public const int AnalyzerVersion = 5;

    /// <summary>Events closer than this (frames) are the same hit.</summary>
    public const int MergeFrames = 3;

    /// <summary>A MultiHit table: hits counted from the damage number, plus the optional final hit.</summary>
    public sealed record MultiHitTable(IReadOnlyList<(int Frame, int Percent)> Hits, int FinalDelayFrames);

    public static List<HitPoint> Analyze(IReadOnlyList<TmbParser.Entry> entries, bool hostile, MultiHitTable? multiHit, int lengthFrames = 0)
    {
        var flyText = entries.Where(e => e.Tag == "C011").Select(e => e.TimeFrames).DefaultIfEmpty(-1).Min();

        // Hits from the timeline itself.
        var events = new List<(int Frame, float Weight, HitSource Source)>();
        foreach (var e in entries)
        {
            switch (e.Tag)
            {
                case "C011":
                    events.Add((e.TimeFrames, 1f, HitSource.FlyText));
                    break;
                case "C067":
                    events.Add((e.TimeFrames, 0.9f, HitSource.Flinch));
                    break;
                case "C012" when IsTargetVfx(e.Path):
                    events.Add((e.TimeFrames, 0.7f, HitSource.TargetVfx));
                    break;
                case "C063" when IsTargetSound(e.Path):
                    events.Add((e.TimeFrames, 0.7f, HitSource.TargetSound));
                    break;
                case "C063" when IsExtraStrikeSound(e.Path):
                    // A follow-up strike: often the only marker of a skill's last hit.
                    events.Add((e.TimeFrames, 0.8f, HitSource.ExtraStrike));
                    break;
                case "C120" when e.Fields.Length >= 3:
                    events.Add((e.TimeFrames, e.Fields[2] switch { 1 => 1f, 3 => 0.7f, _ => 0.45f }, HitSource.BuiltInVibration));
                    break;
            }
        }

        if (!hostile)
        {
            // Nothing lands on a target: follow the caster's effect beats (e.g. a shield raised, then slammed).
            foreach (var e in entries)
            {
                if (e.Tag == "C012" && e.Path.Length > 0 && !IsTargetVfx(e.Path) && !IsGenericVfx(e.Path))
                    events.Add((e.TimeFrames, 0.45f, HitSource.CasterVfx));
            }
        }

        // MultiHit hits are dense (2-4 frames apart) and placed by hand: keep each one.
        var hits = new List<(int Frame, float Weight, HitSource Sources)>();
        if (multiHit is { Hits.Count: > 0 })
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

        // Timeline events: join the nearest hit, or start a new one.
        foreach (var e in events.OrderBy(e => e.Frame))
        {
            var near = -1;
            for (var i = 0; i < hits.Count; i++)
            {
                if (Math.Abs(hits[i].Frame - e.Frame) <= MergeFrames && (near < 0 || Math.Abs(hits[i].Frame - e.Frame) < Math.Abs(hits[near].Frame - e.Frame)))
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

        // The damage number pops when the server applies the damage, often a few frames off the
        // visible impact. When the animation shows its impacts, a damage number on its own isn't a hit.
        var impacts = HitSource.Flinch | HitSource.TargetVfx | HitSource.TargetSound | HitSource.BuiltInVibration | HitSource.MultiHit | HitSource.ExtraStrike;
        if (hits.Any(h => (h.Sources & impacts) != 0))
            hits.RemoveAll(h => h.Sources == HitSource.FlyText);

        if (hits.Count == 0)
            hits.Add((10, 1f, HitSource.None));

        return hits.Select(h => new HitPoint(h.Frame, Math.Clamp(h.Weight, 0f, 1f), h.Sources)).ToList();
    }

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
