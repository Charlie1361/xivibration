using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace XIVibration;

/// <summary>
/// After start-up, analyses the animation of every skill of every job (the one actually
/// loaded, mods included) and saves a vibration pattern for each skill that doesn't have
/// one yet. Existing patterns are never touched. Runs a few skills per frame.
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

    public SkillAnalysis(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public bool Running => this.queue.Count > 0;

    public int Total { get; private set; }

    public int Done => this.Total - this.queue.Count;

    public int CreatedLastRun => this.created;

    public string Status { get; private set; } = "Waiting for login";

    /// <summary>Drop every generated pattern and analyse again. The user's own patterns are kept.</summary>
    public void ReanalyseAll()
    {
        var cfg = this.plugin.Config;
        foreach (var rule in cfg.Rules.Values.Where(r => r.Origin == RuleOrigin.Generated).ToList())
        {
            // Remember skills the user switched off.
            if (!rule.Enabled)
                cfg.AutoDisabled.Add(rule.ActionId);
            else
                cfg.AutoDisabled.Remove(rule.ActionId);
            cfg.Rules.Remove(rule.ActionId);
        }

        this.plugin.Timelines.Clear();
        this.queue.Clear();
        this.queued = false;
        this.loggedInAt = DateTime.UtcNow - StartDelay;
    }

    /// <summary>Re-create the pattern for one skill from its current animation.</summary>
    public ActionRule Regenerate(uint actionId)
    {
        var cfg = this.plugin.Config;
        var enabled = !cfg.Rules.TryGetValue(actionId, out var old) || old.Enabled;
        cfg.Rules.Remove(actionId);
        var rule = this.Create(actionId, enabled);
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
            if (!this.PenumbraReady() && DateTime.UtcNow - this.loggedInAt < PenumbraWait)
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
            if (cfg.Rules.ContainsKey(id))
                continue;
            try
            {
                this.Create(id, !cfg.AutoDisabled.Contains(id));
                this.created++;
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, $"Could not analyse action {id}");
            }
        }

        this.Status = $"Analysing animations... {this.Done}/{this.Total}";
        if (this.queue.Count == 0)
        {
            cfg.Save();
            this.Status = $"Done: {cfg.GeneratedCount} skills analysed, {cfg.UserCount} of your own patterns kept";
            Plugin.Log.Info($"Analysed {this.created} skills.");
        }
    }

    private ActionRule Create(uint actionId, bool enabled)
    {
        var tl = this.plugin.Timelines.ForAction(actionId);
        var auto = this.plugin.BuildAuto(actionId, tl);
        var rule = new ActionRule
        {
            ActionId = actionId,
            Origin = RuleOrigin.Generated,
            Anchor = PulseAnchor.AnimationStart,
            Enabled = enabled,
            Pulses = auto.Pulses,
        };
        this.plugin.Config.Rules[actionId] = rule;
        return rule;
    }

    private void Enqueue()
    {
        this.queued = true;
        this.created = 0;
        var catalog = this.plugin.Catalog;
        var cfg = this.plugin.Config;
        var seen = new HashSet<uint>();
        var order = new List<uint>();

        // The current job first, then every other job.
        var current = ActionCatalog.CurrentJobId();
        foreach (var job in new[] { current }.Concat(catalog.Jobs.Select(j => j.Id)))
        {
            if (job == 0)
                continue;
            foreach (var a in catalog.ForJob(job))
            {
                if (seen.Add(a.Id) && !cfg.Rules.ContainsKey(a.Id))
                    order.Add(a.Id);
            }
        }

        foreach (var id in order)
            this.queue.Enqueue(id);
        this.Total = order.Count;
        this.Status = order.Count == 0
            ? $"Up to date: {cfg.GeneratedCount} skills analysed, {cfg.UserCount} of your own patterns"
            : $"Analysing animations... 0/{order.Count}";
    }

    private bool PenumbraReady()
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
