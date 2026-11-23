using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Configuration;

namespace XIVibration;

public enum PulseShape
{
    Constant,
    FadeOut,
    FadeIn,
    Bump,
    Throb,
}

/// <summary>What a custom rule's pulse delays count from.</summary>
public enum PulseAnchor
{
    /// <summary>From the moment the skill goes off (animation start).</summary>
    AnimationStart,

    /// <summary>From the skill's first hit frame in its animation.</summary>
    FirstHit,
}

[Serializable]
public sealed class VibrationPulse
{
    public int DelayMs { get; set; }

    public int DurationMs { get; set; } = 200;

    /// <summary>Left, low-frequency motor: heavy rumble (0-100).</summary>
    public int Strong { get; set; } = 60;

    /// <summary>Right, high-frequency motor: light buzz (0-100).</summary>
    public int Weak { get; set; } = 60;

    public PulseShape Shape { get; set; } = PulseShape.Constant;

    public VibrationPulse Clone() => (VibrationPulse)MemberwiseClone();
}

[Serializable]
public sealed class ActionRule
{
    public uint ActionId { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Multiplier for this rule only, on top of the master strength.</summary>
    public float Strength { get; set; } = 1f;

    public PulseAnchor Anchor { get; set; } = PulseAnchor.FirstHit;

    public List<VibrationPulse> Pulses { get; set; } = new();
}

[Serializable]
public sealed class AutoStrengths
{
    public float Weaponskill { get; set; } = 1f;

    public float Spell { get; set; } = 1f;

    public float Ability { get; set; } = 1f;

    public float Support { get; set; } = 1f;

    public float LimitBreak { get; set; } = 1f;
}

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;

    public bool Enabled { get; set; } = true;

    /// <summary>Master strength multiplier (0-2).</summary>
    public float MasterStrength { get; set; } = 1f;

    /// <summary>Do nothing when the game's own vibration option is off.</summary>
    public bool RespectGameSetting { get; set; } = true;

    public bool OnlyInCombat { get; set; }

    /// <summary>When a new skill starts, cut pulses still playing from an earlier one.</summary>
    public bool CancelPreviousOnNewAction { get; set; }

    /// <summary>Vibrate every skill automatically, timed to its hits.</summary>
    public bool AutoEnabled { get; set; } = true;

    /// <summary>Skills the user switched off in auto mode.</summary>
    public HashSet<uint> AutoDisabled { get; set; } = new();

    public AutoStrengths AutoStrength { get; set; } = new();

    /// <summary>Use per-hit timings from the MultiHit plugin's config when present.</summary>
    public bool UseMultiHit { get; set; } = true;

    /// <summary>
    /// Off by default: skills whose animation already vibrates (built-in C120 entries)
    /// are left alone. When on, the plugin mutes that vibration and plays its own.
    /// </summary>
    public bool ReplaceModVibration { get; set; }

    public Dictionary<uint, ActionRule> Rules { get; set; } = new();

    // v1 leftovers, kept so old config files still load. No longer used.
    public bool UseFallback { get; set; }

    public ActionRule? Fallback { get; set; }

    public void Migrate()
    {
        if (this.Version < 2)
        {
            // v1 rules counted delays from the animation start.
            foreach (var r in this.Rules.Values)
                r.Anchor = PulseAnchor.AnimationStart;
            this.UseFallback = false;
            this.Fallback = null;
        }

        this.Version = CurrentVersion;
    }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);

    public static List<VibrationPulse> ClonePulses(IEnumerable<VibrationPulse> pulses) => pulses.Select(p => p.Clone()).ToList();
}
