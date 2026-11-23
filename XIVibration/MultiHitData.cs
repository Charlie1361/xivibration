using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace XIVibration;

/// <summary>
/// Reads per-hit timings from the MultiHit plugin's config, if that plugin is installed.
/// Each hit's time is in frames (30 fps) counted from the skill's first hit frame,
/// and its percent is that hit's share of the damage.
/// </summary>
public sealed class MultiHitData
{
    public readonly record struct Hit(int Frame, int Percent);

    private readonly string path;
    private Dictionary<uint, List<Hit>> hits = new();
    private DateTime loadedWrite = DateTime.MinValue;
    private DateTime nextCheck = DateTime.MinValue;

    public MultiHitData(string configDirectory)
    {
        this.path = Path.Combine(configDirectory, "MultiHit.json");
    }

    public bool Available { get; private set; }

    public int Count => this.hits.Count;

    public string? Error { get; private set; }

    /// <summary>Raised after the file was (re)loaded with different content.</summary>
    public event System.Action? Changed;

    public bool TryGet(uint actionId, out IReadOnlyList<Hit> result)
    {
        this.Refresh();
        if (this.hits.TryGetValue(actionId, out var list) && list.Count > 0)
        {
            result = list;
            return true;
        }

        result = Array.Empty<Hit>();
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
                    this.hits = new();
                    this.Available = false;
                    this.Changed?.Invoke();
                }

                return;
            }

            var write = File.GetLastWriteTimeUtc(this.path);
            if (!force && write == this.loadedWrite)
                return;
            this.loadedWrite = write;
            this.hits = Parse(File.ReadAllText(this.path));
            this.Available = true;
            this.Error = null;
            this.Changed?.Invoke();
        }
        catch (Exception ex)
        {
            this.Error = ex.Message;
        }
    }

    private static Dictionary<uint, List<Hit>> Parse(string json)
    {
        var result = new Dictionary<uint, List<Hit>>();
        var root = JObject.Parse(json);
        if (root.Value<bool?>("Enabled") == false)
            return result;

        if (root["actionGroups"] is not JArray groups)
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

                var list = new List<Hit>();
                foreach (var h in hitList)
                {
                    var frame = h.Value<int?>("time") ?? 0;
                    var percent = h.Value<int?>("percent") ?? 0;
                    if (frame >= 0 && percent > 0)
                        list.Add(new Hit(frame, percent));
                }

                list.Sort((a, b) => a.Frame.CompareTo(b.Frame));
                if (list.Count > 0)
                    result[id] = list;
            }
        }

        return result;
    }
}
