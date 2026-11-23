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

[Serializable]
public sealed class VibrationPulse
{
    /// <summary>Time after the action fires before this pulse starts.</summary>
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

    public List<VibrationPulse> Pulses { get; set; } = new();

    public ActionRule Clone() => new()
    {
        ActionId = ActionId,
        Enabled = Enabled,
        Strength = Strength,
        Pulses = Pulses.Select(p => p.Clone()).ToList(),
    };
}

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool Enabled { get; set; } = true;

    /// <summary>Master strength multiplier (0-2).</summary>
    public float MasterStrength { get; set; } = 1f;

    /// <summary>Do nothing when the game's own vibration option is off.</summary>
    public bool RespectGameSetting { get; set; } = true;

    /// <summary>Only vibrate while in combat.</summary>
    public bool OnlyInCombat { get; set; }

    /// <summary>When a new action starts, cut any pulses still playing from an earlier one.</summary>
    public bool CancelPreviousOnNewAction { get; set; }

    /// <summary>Play the fallback pattern for actions that have no rule.</summary>
    public bool UseFallback { get; set; }

    public ActionRule Fallback { get; set; } = new()
    {
        Pulses = { new VibrationPulse { DurationMs = 120, Strong = 20, Weak = 45 } },
    };

    public Dictionary<uint, ActionRule> Rules { get; set; } = new();

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
