using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lumina.Excel.Sheets;

namespace XIVibration.Timeline;

/// <summary>What the plugin knows about one animation timeline (with its sub-timelines).</summary>
public sealed class ActionTimelineInfo
{
    public uint TimelineId { get; init; }

    public string GamePath { get; init; } = string.Empty;

    public string? ResolvedPath { get; init; }

    public bool FromMod { get; init; }

    public bool Found { get; init; }

    public string? Error { get; init; }

    /// <summary>All entries, sub-timelines included, times in frames from the animation start.</summary>
    public IReadOnlyList<TmbParser.Entry> Entries { get; init; } = Array.Empty<TmbParser.Entry>();

    /// <summary>Damage number (C011) frames.</summary>
    public IReadOnlyList<int> FlyTextFrames { get; init; } = Array.Empty<int>();

    public IReadOnlyList<TmbImport.C120Entry> C120 { get; init; } = Array.Empty<TmbImport.C120Entry>();

    public int LengthFrames { get; init; }

    /// <summary>Which files (and sizes) the timeline was read from, sub-timelines included. Stable across sessions.</summary>
    public string Signature { get; init; } = string.Empty;

    /// <summary>The skill's sounds mixed into motor levels, or null when it has none.</summary>
    public Audio.SkillSound? Sound { get; init; }

    public bool HasBuiltInVibration => this.C120.Count > 0;

    public int FlyTextFrame => this.FlyTextFrames.Count > 0 ? this.FlyTextFrames[0] : 10;

    public float LengthMs => Math.Max(this.LengthFrames, 30) * TmbParser.MsPerFrame;

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
    private readonly Dictionary<string, Audio.SoundEnvelope?> soundCache = new();

    /// <summary>null = not tried yet.</summary>
    public bool? PenumbraConnected { get; private set; }

    /// <summary>Bumped when the cache is cleared, so callers can tell their data is stale.</summary>
    public int Generation { get; private set; }

    public void Clear()
    {
        this.byTimeline.Clear();
        this.Generation++;
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
        var (data, resolved, fromMod) = this.Read(gamePath);
        var signature = new System.Text.StringBuilder($"{resolved}:{data?.Length ?? -1}");
        if (data == null)
            return new ActionTimelineInfo { TimelineId = timelineId, GamePath = gamePath, ResolvedPath = resolved, FromMod = fromMod, Error = "File not found" };

        try
        {
            var entries = TmbParser.ParseWithSubTimelines(data, p =>
            {
                var sub = this.Read(p);
                signature.Append($"|{sub.Resolved}:{sub.Data?.Length ?? -1}");
                return sub.Data;
            });
            var sound = Audio.SkillSoundBuilder.Build(entries, this.Read, this.soundCache, signature);
            var flyText = entries.Where(e => e.Tag == "C011").Select(e => e.TimeFrames).Distinct().OrderBy(f => f).ToList();
            var end = entries.Where(e => e.Tag is "C010" && e.Fields.Length > 0).Select(e => e.TimeFrames + e.Fields[0]).DefaultIfEmpty(0).Max();
            end = Math.Max(end, entries.Select(e => e.TimeFrames).DefaultIfEmpty(0).Max());

            return new ActionTimelineInfo
            {
                TimelineId = timelineId,
                GamePath = gamePath,
                ResolvedPath = resolved,
                FromMod = fromMod,
                Found = true,
                Entries = entries,
                FlyTextFrames = flyText,
                C120 = TmbImport.FromEntries(entries),
                LengthFrames = end,
                Signature = signature.ToString(),
                Sound = sound,
            };
        }
        catch (Exception ex)
        {
            return new ActionTimelineInfo { TimelineId = timelineId, GamePath = gamePath, ResolvedPath = resolved, FromMod = fromMod, Error = ex.Message };
        }
    }

    /// <summary>Reads a game file the way the game would load it for the player (mods included).</summary>
    public (byte[]? Data, string Resolved, bool FromMod) ReadFile(string gamePath) => this.Read(gamePath);

    private (byte[]? Data, string Resolved, bool FromMod) Read(string gamePath)
    {
        var resolved = this.Resolve(gamePath);
        var fromMod = !string.Equals(Normalize(resolved), gamePath, StringComparison.OrdinalIgnoreCase);
        try
        {
            byte[]? data = null;
            if (fromMod && File.Exists(resolved))
                data = File.ReadAllBytes(resolved);
            if (data == null && fromMod)
                data = Plugin.DataManager.GetFile(Normalize(resolved))?.Data;
            data ??= Plugin.DataManager.GetFile(gamePath)?.Data;
            return (data, resolved, fromMod);
        }
        catch
        {
            return (null, resolved, fromMod);
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
