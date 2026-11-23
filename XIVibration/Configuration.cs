using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Configuration;
using XIVibration.Timeline;

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

public enum RuleOrigin
{
    /// <summary>Made or edited by the user. Never overwritten.</summary>
    User,

    /// <summary>Created by analysing the skill's animation. Re-analysis may replace it.</summary>
    Generated,
}

[Serializable]
public sealed class ActionRule
{
    public RuleOrigin Origin { get; set; } = RuleOrigin.User;

    // For generated rules: what they were built from, so they can be rebuilt when that changes.
    public int AnalyzerVersion { get; set; }

    public uint TimelineId { get; set; }

    public ulong SourceSignature { get; set; }

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

/// <summary>How many of the chosen cues must meet at a moment for it to be a hit.</summary>
public enum HitMatch
{
    Any,
    AtLeast,
    All,
}

/// <summary>Which animation cues make a hit, and how many must coincide.</summary>
[Serializable]
public sealed class HitDetection
{
    public const HitSource DefaultSources = HitSource.Flinch | HitSource.TargetSound | HitSource.ExtraStrike
                                            | HitSource.BuiltInVibration | HitSource.FlyText | HitSource.CasterVfx;

    public HitSource Sources { get; set; } = DefaultSources;

    /// <summary>Target effects often start before or after the real impact: left out by default.</summary>
    public bool IgnoreTargetEffect { get; set; } = true;

    public HitMatch Match { get; set; } = HitMatch.Any;

    public int MinMatches { get; set; } = 2;

    /// <summary>Cues this close (frames) are the same moment.</summary>
    public int WindowFrames { get; set; } = 3;

    /// <summary>When nothing else is found, vibrate on the damage number.</summary>
    public bool FallbackToDamageNumber { get; set; } = true;

    [Newtonsoft.Json.JsonIgnore]
    public HitSource EffectiveSources => this.IgnoreTargetEffect ? this.Sources & ~HitSource.TargetVfx : this.Sources;

    public bool Uses(HitSource s) => (this.EffectiveSources & s) != 0;

    [Newtonsoft.Json.JsonIgnore]
    public string Key => $"{(int)this.EffectiveSources}|{this.Match}|{this.MinMatches}|{this.WindowFrames}|{this.FallbackToDamageNumber}";
}

/// <summary>How a skill's sounds turn into vibration clips.</summary>
[Serializable]
public sealed class SoundTuning
{
    public const float DefaultThreshold = 0.12f;
    public const int DefaultFadeTailMs = 60;
    public const int DefaultJoinGapMs = 60;

    /// <summary>Build patterns from the sound's bumps instead of the animation's hits.</summary>
    public bool Enabled { get; set; }

    /// <summary>How far (0..1) a sound must rise above its own background to vibrate.</summary>
    public float Threshold { get; set; } = DefaultThreshold;

    /// <summary>Length of the fade-out after each block.</summary>
    public int FadeTailMs { get; set; } = DefaultFadeTailMs;

    /// <summary>Loud parts closer together than this become one block.</summary>
    public int JoinGapMs { get; set; } = DefaultJoinGapMs;

    /// <summary>Also pulse on animation hits that have no sound of their own.</summary>
    public bool PulseSilentHits { get; set; }

    [Newtonsoft.Json.JsonIgnore]
    public string Key => $"{this.Enabled}|{this.Threshold:0.###}|{this.FadeTailMs}|{this.JoinGapMs}|{this.PulseSilentHits}";
}

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public const int CurrentVersion = 4;

    public int Version { get; set; } = CurrentVersion;

    public bool Enabled { get; set; } = true;

    /// <summary>Master strength multiplier (0-2).</summary>
    public float MasterStrength { get; set; } = 1f;

    /// <summary>Do nothing when the game's own vibration option is off.</summary>
    public bool RespectGameSetting { get; set; } = true;

    public bool OnlyInCombat { get; set; }

    /// <summary>Test also plays the skill's sounds.</summary>
    public bool PreviewSound { get; set; } = true;

    /// <summary>Volume of the Test sound, 0..100.</summary>
    public int PreviewVolume { get; set; } = 60;

    /// <summary>Delay of the Test vibration against its sound (ms), to make up for audio output latency.</summary>
    public int PreviewSyncMs { get; set; }

    /// <summary>
    /// Only my character's animations may vibrate. Mods apply to everyone's animations, so
    /// built-in vibration in a mod would otherwise also fire when other players use the skill.
    /// </summary>
    public bool OnlyMyCharacter { get; set; } = true;

    /// <summary>When a new skill starts, cut pulses still playing from an earlier one.</summary>
    public bool CancelPreviousOnNewAction { get; set; }

    /// <summary>Vibrate every skill automatically, timed to its hits.</summary>
    public bool AutoEnabled { get; set; } = true;

    /// <summary>Skills the user switched off in auto mode.</summary>
    public HashSet<uint> AutoDisabled { get; set; } = new();

    public AutoStrengths AutoStrength { get; set; } = new();

    public SoundTuning Sound { get; set; } = new();

    public HitDetection Hits { get; set; } = new();

    // v3 leftover: MultiHit is now chosen in Hits.Sources (off by default). Kept so old files load.
    public bool UseMultiHit { get; set; }

    /// <summary>
    /// Off by default: skills whose animation already vibrates (built-in C120 entries)
    /// are left alone. When on, the plugin mutes that vibration and plays its own.
    /// </summary>
    public bool ReplaceModVibration { get; set; }

    public Dictionary<uint, ActionRule> Rules { get; set; } = new();

    // v1 leftovers, kept so old config files still load. No longer used.
    public bool UseFallback { get; set; }

    public ActionRule? Fallback { get; set; }

    [Newtonsoft.Json.JsonIgnore]
    public int GeneratedCount => this.Rules.Values.Count(r => r.Origin == RuleOrigin.Generated);

    [Newtonsoft.Json.JsonIgnore]
    public int UserCount => this.Rules.Values.Count(r => r.Origin == RuleOrigin.User);

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

    /// <summary>FNV-1a: the same value in every session (string.GetHashCode is not).</summary>
    public static ulong StableHash(string text)
    {
        var h = 14695981039346656037UL;
        foreach (var c in text)
        {
            h ^= c;
            h *= 1099511628211UL;
        }

        return h;
    }

    public static List<VibrationPulse> ClonePulses(IEnumerable<VibrationPulse> pulses) => pulses.Select(p => p.Clone()).ToList();
}
