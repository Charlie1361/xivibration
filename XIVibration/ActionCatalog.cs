using System;
using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;

namespace XIVibration;

public sealed record ActionInfo(uint Id, string Name, uint Icon, uint ClassJob, string JobAbbr, byte Level, bool IsPvP, string Timeline);

/// <summary>Cached lookup of player actions from the Action sheet.</summary>
public sealed class ActionCatalog
{
    private readonly Dictionary<uint, ActionInfo> byId = new();
    private List<ActionInfo>? playerActions;

    public IReadOnlyList<ActionInfo> PlayerActions => this.playerActions ??= this.Build();

    public ActionInfo? Get(uint id)
    {
        if (this.byId.TryGetValue(id, out var info))
            return info;

        var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
        if (!sheet.TryGetRow(id, out var row))
            return null;

        info = ToInfo(row);
        this.byId[id] = info;
        return info;
    }

    public string NameOf(uint id) => this.Get(id)?.Name is { Length: > 0 } n ? n : $"Action #{id}";

    private List<ActionInfo> Build()
    {
        var list = new List<ActionInfo>();
        foreach (var row in Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>())
        {
            if (!row.IsPlayerAction && row.ClassJobLevel == 0)
                continue;
            var info = ToInfo(row);
            if (info.Name.Length == 0)
                continue;
            this.byId[row.RowId] = info;
            list.Add(info);
        }

        return list.OrderBy(a => a.JobAbbr).ThenBy(a => a.Level).ThenBy(a => a.Name).ToList();
    }

    private static ActionInfo ToInfo(Lumina.Excel.Sheets.Action row)
    {
        string abbr = string.Empty;
        uint job = 0;
        if (row.ClassJob.IsValid)
        {
            job = row.ClassJob.RowId;
            abbr = row.ClassJob.Value.Abbreviation.ExtractText();
        }

        var timeline = string.Empty;
        if (row.AnimationEnd.IsValid && row.AnimationEnd.RowId != 0)
        {
            var key = row.AnimationEnd.Value.Key.ExtractText();
            if (key.Length > 0)
                timeline = $"chara/action/{key}.tmb";
        }

        return new ActionInfo(row.RowId, row.Name.ExtractText(), row.Icon, job, abbr, row.ClassJobLevel, row.IsPvP, timeline);
    }

    /// <summary>Current job and its base class (e.g. DRG + LNC).</summary>
    public static (uint Job, uint Parent) CurrentJob()
    {
        var local = Plugin.ObjectTable.LocalPlayer;
        if (local == null || !local.ClassJob.IsValid)
            return (0, 0);
        var job = local.ClassJob.Value;
        return (job.RowId, job.ClassJobParent.RowId);
    }

    public static bool MatchesJob(ActionInfo a, (uint Job, uint Parent) current)
    {
        if (current.Job == 0)
            return true;
        if (a.ClassJob == 0)
            return true; // role and shared actions
        return a.ClassJob == current.Job || a.ClassJob == current.Parent;
    }

    public static bool MatchesSearch(ActionInfo a, string search)
        => search.Length == 0
           || a.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
           || a.JobAbbr.Equals(search, StringComparison.OrdinalIgnoreCase)
           || a.Id.ToString() == search;
}
