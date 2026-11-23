using System;
using System.Collections.Generic;
using System.Linq;
using Lumina.Excel.Sheets;

namespace XIVibration;

public enum SkillGroup
{
    Weaponskill,
    Spell,
    Ability,
    Role,
    LimitBreak,
    Other,
}

public sealed record ActionInfo(
    uint Id,
    string Name,
    uint Icon,
    uint ClassJob,
    string JobAbbr,
    byte Level,
    bool IsPvP,
    uint Category,
    ushort Recast100ms,
    bool CanTargetHostile,
    bool IsRoleAction,
    byte EffectRange,
    byte CastType,
    uint ClassJobCategory)
{
    public SkillGroup Group => this.Category switch
    {
        9 or 15 => SkillGroup.LimitBreak,
        _ when this.IsRoleAction => SkillGroup.Role,
        3 => SkillGroup.Weaponskill,
        2 => SkillGroup.Spell,
        4 => SkillGroup.Ability,
        _ => SkillGroup.Other,
    };

    /// <summary>Hits more than one target (cone, circle, line).</summary>
    public bool IsAoE => this.CastType > 1 && this.EffectRange > 0;
}

public sealed record JobInfo(uint Id, uint Parent, string Abbr, string Name, byte Role);

/// <summary>Cached lookup of player actions and jobs from the game sheets.</summary>
public sealed class ActionCatalog
{
    private readonly Dictionary<uint, ActionInfo> byId = new();
    private readonly Dictionary<uint, List<ActionInfo>> byJob = new();
    private readonly Dictionary<(uint Category, string Abbr), bool> categoryCache = new();
    private List<ActionInfo>? playerActions;
    private List<JobInfo>? jobs;

    public IReadOnlyList<ActionInfo> PlayerActions => this.playerActions ??= this.BuildActions();

    public IReadOnlyList<JobInfo> Jobs => this.jobs ??= BuildJobs();

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

    public JobInfo? GetJob(uint id)
    {
        var job = this.Jobs.FirstOrDefault(j => j.Id == id);
        if (job != null)
            return job;

        var sheet = Plugin.DataManager.GetExcelSheet<ClassJob>();
        return sheet.TryGetRow(id, out var row) ? ToJob(row) : null;
    }

    /// <summary>The job's own skills, its base class's skills, and the role/shared skills it can use.</summary>
    public IReadOnlyList<ActionInfo> ForJob(uint jobId)
    {
        if (this.byJob.TryGetValue(jobId, out var list))
            return list;

        list = new List<ActionInfo>();
        var job = this.GetJob(jobId);
        if (job != null)
        {
            foreach (var a in this.PlayerActions)
            {
                if (a.IsPvP)
                    continue;
                var own = a.ClassJob != 0 && (a.ClassJob == job.Id || a.ClassJob == job.Parent);
                var shared = a.ClassJob == 0 && a.ClassJobCategory != 0 && this.CategoryIncludes(a.ClassJobCategory, job.Abbr);
                if (own || shared)
                    list.Add(a);
            }
        }

        list = list.OrderBy(a => a.Level).ThenBy(a => a.Name).ToList();
        this.byJob[jobId] = list;
        return list;
    }

    private bool CategoryIncludes(uint categoryId, string abbr)
    {
        if (abbr.Length == 0)
            return false;
        if (this.categoryCache.TryGetValue((categoryId, abbr), out var yes))
            return yes;

        yes = false;
        var sheet = Plugin.DataManager.GetExcelSheet<ClassJobCategory>();
        if (sheet.TryGetRow(categoryId, out var row))
        {
            var prop = typeof(ClassJobCategory).GetProperty(abbr);
            if (prop?.PropertyType == typeof(bool))
                yes = (bool)prop.GetValue(row)!;
        }

        this.categoryCache[(categoryId, abbr)] = yes;
        return yes;
    }

    private List<ActionInfo> BuildActions()
    {
        var list = new List<ActionInfo>();
        foreach (var row in Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>())
        {
            if (!row.IsPlayerAction && row.ClassJobLevel == 0)
                continue;
            var info = ToInfo(row);
            if (info.Name.Length == 0 || info.Icon == 0)
                continue;
            this.byId[row.RowId] = info;
            list.Add(info);
        }

        return list;
    }

    private static List<JobInfo> BuildJobs()
        => Plugin.DataManager.GetExcelSheet<ClassJob>()
            .Where(r => r.JobIndex > 0)
            .Select(ToJob)
            .Where(j => j.Abbr.Length > 0)
            .OrderBy(j => j.Role == 1 ? 0 : j.Role == 4 ? 1 : 2)
            .ThenBy(j => j.Id)
            .ToList();

    private static JobInfo ToJob(ClassJob row)
        => new(row.RowId, row.ClassJobParent.RowId, row.Abbreviation.ExtractText(), row.Name.ExtractText(), row.Role);

    private static ActionInfo ToInfo(Lumina.Excel.Sheets.Action row)
    {
        string abbr = string.Empty;
        uint job = 0;
        if (row.ClassJob.IsValid && row.ClassJob.RowId != 0)
        {
            job = row.ClassJob.RowId;
            abbr = row.ClassJob.Value.Abbreviation.ExtractText();
        }

        return new ActionInfo(
            row.RowId,
            row.Name.ExtractText(),
            row.Icon,
            job,
            abbr,
            row.ClassJobLevel,
            row.IsPvP,
            row.ActionCategory.RowId,
            row.Recast100ms,
            row.CanTargetHostile,
            row.IsRoleAction,
            row.EffectRange,
            row.CastType,
            row.ClassJobCategory.RowId);
    }

    /// <summary>Current job id, or 0 when not logged in.</summary>
    public static uint CurrentJobId()
    {
        var local = Plugin.ObjectTable.LocalPlayer;
        return local != null && local.ClassJob.IsValid ? local.ClassJob.RowId : 0;
    }

    public static bool MatchesSearch(ActionInfo a, string search)
        => search.Length == 0
           || a.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
           || a.Id.ToString() == search;
}
