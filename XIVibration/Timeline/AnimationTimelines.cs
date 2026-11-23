using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lumina.Excel.Sheets;

namespace XIVibration.Timeline;

/// <summary>What the plugin knows about one animation timeline.</summary>
public sealed class ActionTimelineInfo
{
    /// <summary>Hit frame used when a timeline can't be read or has no C011 entry.</summary>
    public const int DefaultHitFrame = 10;

    public uint TimelineId { get; init; }

    public string GamePath { get; init; } = string.Empty;

    public string? ResolvedPath { get; init; }

    public bool FromMod { get; init; }

    public bool Found { get; init; }

    public string? Error { get; init; }

    /// <summary>C011 frames, sorted.</summary>
    public IReadOnlyList<int> HitFrames { get; init; } = Array.Empty<int>();

    public IReadOnlyList<TmbImport.C120Entry> C120 { get; init; } = Array.Empty<TmbImport.C120Entry>();

    public int LengthFrames { get; init; }

    public bool HasBuiltInVibration => this.C120.Count > 0;

    public int FirstHitFrame => this.HitFrames.Count > 0 ? this.HitFrames[0] : DefaultHitFrame;

    public float FirstHitMs => this.FirstHitFrame * TmbParser.MsPerFrame;

    public float LengthMs => Math.Max(this.LengthFrames, this.FirstHitFrame + 15) * TmbParser.MsPerFrame;

    public static readonly ActionTimelineInfo Missing = new() { Error = "No animation timeline" };
}

/// <summary>
/// Finds and reads the .tmb a skill plays. Goes through Penumbra when it's installed,
/// so a mod's replacement animation (and its hit timing) is what gets read.
/// Use from the framework / UI thread only.
/// </summary>
public sealed class AnimationTimelines
{
    private readonly Dictionary<uint, ActionTimelineInfo> byTimeline = new();
    private readonly Dictionary<uint, uint> actionToTimeline = new();

    /// <summary>null = not tried yet.</summary>
    public bool? PenumbraConnected { get; private set; }

    public void Clear()
    {
        this.byTimeline.Clear();
        this.actionToTimeline.Clear();
    }

    /// <summary>ActionTimeline row the skill uses when it goes off (Action.AnimationEnd).</summary>
    public uint TimelineOf(uint actionId)
    {
        if (this.actionToTimeline.TryGetValue(actionId, out var id))
            return id;

        id = 0;
        var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
        if (sheet.TryGetRow(actionId, out var row) && row.AnimationEnd.IsValid)
            id = row.AnimationEnd.RowId;
        this.actionToTimeline[actionId] = id;
        return id;
    }

    public bool IsCached(uint actionId)
    {
        var t = this.TimelineOf(actionId);
        return t == 0 || this.byTimeline.ContainsKey(t);
    }

    public ActionTimelineInfo ForAction(uint actionId) => this.ForTimeline(this.TimelineOf(actionId));

    public ActionTimelineInfo ForTimeline(uint timelineId)
    {
        if (timelineId == 0)
            return ActionTimelineInfo.Missing;
        if (this.byTimeline.TryGetValue(timelineId, out var info))
            return info;

        info = this.Load(timelineId);
        this.byTimeline[timelineId] = info;
        return info;
    }

    private ActionTimelineInfo Load(uint timelineId)
    {
        var sheet = Plugin.DataManager.GetExcelSheet<ActionTimeline>();
        if (!sheet.TryGetRow(timelineId, out var row))
            return new ActionTimelineInfo { TimelineId = timelineId, Error = "Unknown timeline" };

        var key = row.Key.ExtractText();
        if (key.Length == 0 || key.Contains('['))
            return new ActionTimelineInfo { TimelineId = timelineId, GamePath = key, Error = "Timeline path depends on the weapon" };

        var gamePath = $"chara/action/{key}.tmb";
        var resolved = this.Resolve(gamePath);
        var fromMod = !string.Equals(Normalize(resolved), gamePath, StringComparison.OrdinalIgnoreCase);

        try
        {
            byte[]? data = null;
            if (fromMod && File.Exists(resolved))
                data = File.ReadAllBytes(resolved);
            data ??= Plugin.DataManager.GetFile(fromMod ? Normalize(resolved) : gamePath)?.Data;
            if (data == null && fromMod)
                data = Plugin.DataManager.GetFile(gamePath)?.Data;
            if (data == null)
                return new ActionTimelineInfo { TimelineId = timelineId, GamePath = gamePath, ResolvedPath = resolved, FromMod = fromMod, Error = "File not found" };

            var entries = TmbParser.Parse(data);
            var hits = entries.Where(e => e.Tag == "C011").Select(e => (int)e.TimeFrames).Distinct().OrderBy(f => f).ToList();
            var end = entries.Where(e => e.Tag == "C131").Select(e => (int)e.TimeFrames).DefaultIfEmpty(0).Max();
            if (end == 0)
                end = entries.Select(e => (int)e.TimeFrames).DefaultIfEmpty(0).Max();

            return new ActionTimelineInfo
            {
                TimelineId = timelineId,
                GamePath = gamePath,
                ResolvedPath = resolved,
                FromMod = fromMod,
                Found = true,
                HitFrames = hits,
                C120 = TmbImport.FromEntries(entries),
                LengthFrames = end,
            };
        }
        catch (Exception ex)
        {
            return new ActionTimelineInfo { TimelineId = timelineId, GamePath = gamePath, ResolvedPath = resolved, FromMod = fromMod, Error = ex.Message };
        }
    }

    private string Resolve(string gamePath)
    {
        try
        {
            var resolved = Plugin.PluginInterface
                .GetIpcSubscriber<string, string>("Penumbra.ResolvePlayerPath")
                .InvokeFunc(gamePath);
            this.PenumbraConnected = true;
            return string.IsNullOrEmpty(resolved) ? gamePath : resolved;
        }
        catch
        {
            this.PenumbraConnected = false;
            return gamePath;
        }
    }

    private static string Normalize(string path) => path.Replace('\\', '/');
}
