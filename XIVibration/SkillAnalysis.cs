using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using XIVibration.Timeline;

namespace XIVibration;

/// <summary>
/// After start-up, analyses the animation of every skill of every job (the one actually
/// loaded, mods included) and saves a vibration pattern for each skill.
/// - Skills without a pattern get one.
/// - Generated patterns are rebuilt when their animation changed (other mod or option)
///   or the analysis improved. They keep their on/off and strength.
/// - Patterns the user made or edited are never touched.
/// Runs a few skills per frame.
/// </summary>
public sealed class SkillAnalysis
{
    private const double FrameBudgetMs = 3.0;
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PenumbraWait = TimeSpan.FromSeconds(60);

    private readonly Plugin plugin;
    private readonly Queue<uint> queue = new();
    private DateTime loggedInAt = DateTime.MaxValue;
    private bool queued;
    private int created;
    private int refreshed;

    public SkillAnalysis(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public bool Running => this.queue.Count > 0;

    public int Total { get; private set; }

    public int Done => this.Total - this.queue.Count;

    public string Status { get; private set; } = "Waiting for login";

    /// <summary>Check every skill again now (for example after changing mods). The user's own patterns are kept.</summary>
    public void ReanalyseAll()
    {
        this.plugin.Timelines.Clear();
        this.queue.Clear();
        this.queued = false;
        this.loggedInAt = DateTime.UtcNow - StartDelay;
    }

    /// <summary>Re-create the pattern for one skill from its current animation.</summary>
    public ActionRule Regenerate(uint actionId)
    {
        var cfg = this.plugin.Config;
        cfg.Rules.TryGetValue(actionId, out var old);
        cfg.Rules.Remove(actionId);
        var rule = this.Create(actionId, old?.Enabled ?? !cfg.AutoDisabled.Contains(actionId), old?.Origin == RuleOrigin.Generated ? old.Strength : 1f);
        cfg.Save();
        return rule;
    }

    public void Update()
    {
        if (!Plugin.ClientState.IsLoggedIn || Plugin.ObjectTable.LocalPlayer == null)
        {
            this.loggedInAt = DateTime.MaxValue;
            if (!this.queued)
                this.Status = "Waiting for login";
            return;
        }

        if (this.loggedInAt == DateTime.MaxValue)
            this.loggedInAt = DateTime.UtcNow;

        if (!this.queued)
        {
            if (DateTime.UtcNow - this.loggedInAt < StartDelay)
                return;
            if (!PenumbraReady() && DateTime.UtcNow - this.loggedInAt < PenumbraWait)
            {
                this.Status = "Waiting for Penumbra";
                return;
            }

            this.Enqueue();
            return;
        }

        if (this.queue.Count == 0)
            return;

        var sw = Stopwatch.StartNew();
        var cfg = this.plugin.Config;
        while (this.queue.Count > 0 && sw.Elapsed.TotalMilliseconds < FrameBudgetMs)
        {
            var id = this.queue.Dequeue();
            try
            {
                if (!cfg.Rules.TryGetValue(id, out var rule))
                {
                    this.Create(id, !cfg.AutoDisabled.Contains(id), 1f);
                    this.created++;
                }
                else if (rule.Origin == RuleOrigin.Generated && this.IsOutdated(id, rule))
                {
                    this.Create(id, rule.Enabled, rule.Strength);
                    this.refreshed++;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, $"Could not analyse action {id}");
            }
        }

        this.Status = $"Analysing animations... {this.Done}/{this.Total}";
        if (this.queue.Count == 0)
        {
            if (this.created + this.refreshed > 0)
                cfg.Save();
            this.Status = $"Done: {cfg.GeneratedCount} skills analysed ({this.created} new, {this.refreshed} updated), {cfg.UserCount} of your own patterns kept";
            Plugin.Log.Info($"Skill analysis: {this.created} new, {this.refreshed} updated.");
        }
    }

    /// <summary>The fingerprint of everything a generated pattern was built from.</summary>
    public ulong SignatureOf(uint actionId, ActionTimelineInfo tl)
    {
        var mh = this.plugin.MultiHitFor(actionId);
        var mhText = mh == null ? "-" : string.Join(",", mh.Hits.Select(h => $"{h.Frame}:{h.Percent}")) + $"/{mh.FinalDelayFrames}";
        return Configuration.StableHash($"{tl.TimelineId}|{tl.Signature}|{mhText}|{this.plugin.Config.Sound.Key}");
    }

    private bool IsOutdated(uint actionId, ActionRule rule)
    {
        if (rule.AnalyzerVersion != HitAnalyzer.AnalyzerVersion)
            return true;
        var tl = this.plugin.Timelines.ForAction(actionId);
        return rule.TimelineId != tl.TimelineId || rule.SourceSignature != this.SignatureOf(actionId, tl);
    }

    private ActionRule Create(uint actionId, bool enabled, float strength)
    {
        var tl = this.plugin.Timelines.ForAction(actionId);
        var auto = this.plugin.BuildAuto(actionId, tl);
        var rule = new ActionRule
        {
            ActionId = actionId,
            Origin = RuleOrigin.Generated,
            Anchor = PulseAnchor.AnimationStart,
            Enabled = enabled,
            Strength = strength,
            Pulses = Configuration.ClonePulses(auto.Pulses),
            AnalyzerVersion = HitAnalyzer.AnalyzerVersion,
            TimelineId = tl.TimelineId,
            SourceSignature = this.SignatureOf(actionId, tl),
        };
        this.plugin.Config.Rules[actionId] = rule;
        return rule;
    }

    private void Enqueue()
    {
        this.queued = true;
        this.created = 0;
        this.refreshed = 0;
        var catalog = this.plugin.Catalog;
        var cfg = this.plugin.Config;
        var seen = new HashSet<uint>();
        var order = new List<uint>();

        // The current job first, then every other job, then generated patterns of anything else.
        var current = ActionCatalog.CurrentJobId();
        foreach (var job in new[] { current }.Concat(catalog.Jobs.Select(j => j.Id)))
        {
            if (job == 0)
                continue;
            foreach (var a in catalog.ForJob(job))
            {
                if (seen.Add(a.Id) && (!cfg.Rules.TryGetValue(a.Id, out var r) || r.Origin == RuleOrigin.Generated))
                    order.Add(a.Id);
            }
        }

        foreach (var (id, rule) in cfg.Rules)
        {
            if (rule.Origin == RuleOrigin.Generated && seen.Add(id))
                order.Add(id);
        }

        foreach (var id in order)
            this.queue.Enqueue(id);
        this.Total = order.Count;
        this.Status = $"Analysing animations... 0/{order.Count}";
    }

    private static bool PenumbraReady()
    {
        try
        {
            var penumbra = Plugin.PluginInterface.InstalledPlugins.FirstOrDefault(p => p.InternalName == "Penumbra");
            return penumbra == null || penumbra.IsLoaded;
        }
        catch
        {
            return true;
        }
    }
}
