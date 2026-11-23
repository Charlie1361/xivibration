using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using XIVibration.Timeline;

namespace XIVibration;

/// <summary>
/// Reads per-hit timings from the MultiHit plugin's config, if that plugin is installed.
/// Hit times are in frames (30 fps) counted from the skill's damage number, and each
/// hit's percent is its share of the damage. When the hits add up to less than 100%,
/// the rest lands as a final hit, "final" frames after the last one.
/// </summary>
public sealed class MultiHitData
{
    private readonly string path;
    private readonly Func<uint, uint> timelineOf;
    private Dictionary<uint, HitAnalyzer.MultiHitTable> byAction = new();
    private Dictionary<uint, HitAnalyzer.MultiHitTable>? byTimeline;
    private DateTime loadedWrite = DateTime.MinValue;
    private DateTime nextCheck = DateTime.MinValue;

    /// <param name="timelineOf">Action id to its animation timeline id.</param>
    public MultiHitData(string configDirectory, Func<uint, uint> timelineOf)
    {
        this.path = Path.Combine(configDirectory, "MultiHit.json");
        this.timelineOf = timelineOf;
    }

    public bool Available { get; private set; }

    public int Count => this.byAction.Count;

    /// <summary>Bumped on every reload with new content.</summary>
    public int Version { get; private set; }

    /// <summary>
    /// Finds the table for an action. Actions that share an animation share a table
    /// (the same skill often exists under more than one action id).
    /// </summary>
    public bool TryGet(uint actionId, out HitAnalyzer.MultiHitTable? table)
    {
        this.Refresh();
        if (this.byAction.TryGetValue(actionId, out table))
            return true;

        var timeline = this.timelineOf(actionId);
        if (timeline != 0)
        {
            this.byTimeline ??= this.BuildTimelineIndex();
            if (this.byTimeline.TryGetValue(timeline, out table))
                return true;
        }

        table = null;
        return false;
    }

    public void Refresh(bool force = false)
    {
        var now = DateTime.UtcNow;
        if (!force && now < this.nextCheck)
            return;
        this.nextCheck = now.AddSeconds(2);

        try
        {
            if (!File.Exists(this.path))
            {
                if (this.Available)
                {
                    this.byAction = new();
                    this.byTimeline = null;
                    this.Available = false;
                    this.Version++;
                }

                return;
            }

            var write = File.GetLastWriteTimeUtc(this.path);
            if (!force && write == this.loadedWrite)
                return;
            this.loadedWrite = write;
            this.byAction = Parse(File.ReadAllText(this.path));
            this.byTimeline = null;
            this.Available = true;
            this.Version++;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Could not read MultiHit config");
        }
    }

    private Dictionary<uint, HitAnalyzer.MultiHitTable> BuildTimelineIndex()
    {
        var index = new Dictionary<uint, HitAnalyzer.MultiHitTable>();
        foreach (var (action, table) in this.byAction)
        {
            var t = this.timelineOf(action);
            if (t != 0)
                index.TryAdd(t, table);
        }

        return index;
    }

    private static Dictionary<uint, HitAnalyzer.MultiHitTable> Parse(string json)
    {
        var result = new Dictionary<uint, HitAnalyzer.MultiHitTable>();
        var root = JObject.Parse(json);
        if (root.Value<bool?>("Enabled") == false || root["actionGroups"] is not JArray groups)
            return result;

        foreach (var group in groups)
        {
            if (group.Value<bool?>("enabled") == false || group["actionList"] is not JArray actions)
                continue;

            foreach (var action in actions)
            {
                if (action.Value<bool?>("enabled") == false)
                    continue;
                var id = action.Value<uint?>("actionKey") ?? 0;
                if (id == 0 || result.ContainsKey(id) || action["hitList"] is not JArray hitList)
                    continue;

                var hits = new List<(int Frame, int Percent)>();
                foreach (var h in hitList)
                {
                    var frame = h.Value<int?>("time") ?? 0;
                    var percent = h.Value<int?>("percent") ?? 0;
                    if (frame >= 0 && percent > 0)
                        hits.Add((frame, percent));
                }

                if (hits.Count == 0)
                    continue;
                hits.Sort((a, b) => a.Frame.CompareTo(b.Frame));
                var final = action["finalHit"]?.Value<int?>("time") ?? 0;
                result[id] = new HitAnalyzer.MultiHitTable(hits, final);
            }
        }

        return result;
    }
}
