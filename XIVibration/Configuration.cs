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

    /// <summary>Fade at the start of the clip (ms, 0 = none).</summary>
    public int FadeInMs { get; set; }

    /// <summary>Fade at the end of the clip (ms, 0 = none).</summary>
    public int FadeOutMs { get; set; }

    /// <summary>Fade curve, -100..100: 0 linear, + fast change then a long thin tail, - holds then changes late.</summary>
    public int FadeInCurve { get; set; }

    public int FadeOutCurve { get; set; }

    public VibrationPulse Clone() => (VibrationPulse)MemberwiseClone();

    /// <summary>
    /// The fades as they fit in a clip of this length: never negative, and when together they're
    /// longer than the clip, both shrink in proportion so they fill it exactly.
    /// </summary>
    public (int In, int Out) FittedFades(int durationMs) => FitFades(this.FadeInMs, this.FadeOutMs, durationMs);

    public static (int In, int Out) FitFades(int fadeIn, int fadeOut, int durationMs)
    {
        var d = Math.Max(0, durationMs);
        var fi = Math.Max(0, fadeIn);
        var fo = Math.Max(0, fadeOut);
        if (fi + fo <= d)
            return (fi, fo);
        var k = d / (float)(fi + fo);
        var newIn = (int)MathF.Round(fi * k);
        return (newIn, d - newIn);
    }

    /// <summary>Makes the fades fit inside the clip (see <see cref="FittedFades"/>).</summary>
    public void FitFades() => (this.FadeInMs, this.FadeOutMs) = this.FittedFades(this.DurationMs);

    /// <summary>Whole-clip FadeOut/FadeIn shapes (older patterns) become real fades with the same feel.</summary>
    public void UpgradeShape()
    {
        if (this.Shape == PulseShape.FadeOut)
        {
            this.Shape = PulseShape.Constant;
            this.FadeOutMs = this.DurationMs;
            this.FadeOutCurve = 0;
        }
        else if (this.Shape == PulseShape.FadeIn)
        {
            this.Shape = PulseShape.Constant;
            this.FadeInMs = this.DurationMs;
            this.FadeInCurve = 0;
        }
    }
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

    public PulseAnchor Anchor { get; set; } = PulseAnchor.AnimationStart;

    public List<VibrationPulse> Pulses { get; set; } = new();
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
                                            | HitSource.BuiltInVibration | HitSource.FlyText | HitSource.CasterVfx
                                            | HitSource.EffectStrike;

    public HitSource Sources { get; set; } = DefaultSources;

    // v4 leftover, only read by the migration: "effect on the target" is now just a cue (off by default).
    public bool IgnoreTargetEffect { get; set; }

    public HitMatch Match { get; set; } = HitMatch.Any;

    public int MinMatches { get; set; } = 2;

    /// <summary>Cues this close (frames) are the same moment.</summary>
    public int WindowFrames { get; set; } = 3;

    [Newtonsoft.Json.JsonIgnore]
    public HitSource EffectiveSources => this.Sources;

    public bool Uses(HitSource s) => (this.EffectiveSources & s) != 0;

    [Newtonsoft.Json.JsonIgnore]
    public string Key => $"{(int)this.EffectiveSources}|{this.Match}|{this.MinMatches}|{this.WindowFrames}";

    public HitDetection Clone() => (HitDetection)this.MemberwiseClone();
}

/// <summary>How a skill's sounds turn into vibration clips.</summary>
[Serializable]
public sealed class SoundTuning
{
    public const float DefaultThreshold = 0.12f;
    public const int DefaultFadeTailMs = 60;
    public const int JoinGapMs = 60;
    public const float DefaultPeakWindow = 0.20f;

    /// <summary>Build patterns from the sound's bumps instead of the animation's hits.</summary>
    public bool Enabled { get; set; }

    /// <summary>How far (0..1) a sound must rise above its own background to vibrate.</summary>
    public float Threshold { get; set; } = DefaultThreshold;

    /// <summary>Length of the fade-out after each block.</summary>
    public int FadeTailMs { get; set; } = DefaultFadeTailMs;

    /// <summary>Peak-based vibration only uses peaks at least (1 - this) as loud as the skill's loudest peak.</summary>
    public float PeakWindow { get; set; } = DefaultPeakWindow;

    [Newtonsoft.Json.JsonIgnore]
    public string Key => $"{this.Enabled}|{this.Threshold:0.###}|{this.FadeTailMs}|{this.PeakWindow:0.###}";
}

/// <summary>Everything one character changed: own patterns and switched-off skills.</summary>
[Serializable]
public sealed class CharacterPreset
{
    public string Name { get; set; } = string.Empty;

    public string World { get; set; } = string.Empty;

    /// <summary>Patterns made or edited by the user, by action id.</summary>
    public Dictionary<uint, ActionRule> Rules { get; set; } = new();

    /// <summary>Skills switched off (their automatic pattern doesn't play).</summary>
    public HashSet<uint> Disabled { get; set; } = new();

    public DateTime Updated { get; set; } = DateTime.Now;

    [Newtonsoft.Json.JsonIgnore]
    public string Key => $"{this.Name}@{this.World}";

    [Newtonsoft.Json.JsonIgnore]
    public int ChangeCount => this.Rules.Count + this.Disabled.Count;
}

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public const int CurrentVersion = 7;

    public int Version { get; set; } = CurrentVersion;

    public bool Enabled { get; set; } = true;

    /// <summary>Master strength multiplier (0-2).</summary>
    public float MasterStrength { get; set; } = 1f;

    /// <summary>Do nothing when the game's own vibration option is off.</summary>
    public bool RespectGameSetting { get; set; } = true;

    public bool OnlyInCombat { get; set; }

    /// <summary>Volume of the Test sound, 0..100 (0 = no sound).</summary>
    public int PreviewVolume { get; set; } = 60;

    /// <summary>Delay of the Test vibration against its sound (ms), to make up for audio output latency.</summary>
    public int PreviewSyncMs { get; set; }

    /// <summary>Test plays the skill's animation on your own character (only you see it).</summary>
    public bool TestOnCharacter { get; set; }

    /// <summary>
    /// Only my character's animations may vibrate. Mods apply to everyone's animations, so
    /// built-in vibration in a mod would otherwise also fire when other players use the skill.
    /// </summary>
    public bool OnlyMyCharacter { get; set; } = true;

    /// <summary>Skills switched off before character presets existed (moved into the first preset).</summary>
    public HashSet<uint> AutoDisabled { get; set; } = new();

    public SoundTuning Sound { get; set; } = new();

    public HitDetection Hits { get; set; } = new();

    /// <summary>Skills with their own hit detection (instead of <see cref="Hits"/>), by action id.</summary>
    public Dictionary<uint, HitDetection> SkillHits { get; set; } = new();

    /// <summary>The hit detection a skill uses: its own, or the general one.</summary>
    public HitDetection HitsFor(uint actionId) => this.SkillHits.TryGetValue(actionId, out var h) ? h : this.Hits;

    /// <summary>Back to a fresh install: every setting at its default, every own pattern and preset gone.</summary>
    public void ResetToDefaults()
    {
        var fresh = new Configuration();
        foreach (var prop in typeof(Configuration).GetProperties())
        {
            if (prop.CanRead && prop.CanWrite && prop.GetIndexParameters().Length == 0)
                prop.SetValue(this, prop.GetValue(fresh));
        }

        this.Version = CurrentVersion;
    }


    /// <summary>
    /// Off by default: skills whose animation already vibrates (built-in C120 entries)
    /// are left alone. When on, the plugin mutes that vibration and plays its own.
    /// </summary>
    public bool ReplaceModVibration { get; set; }

    /// <summary>Generated patterns (shared by all characters; they depend on the installed mods).</summary>
    public Dictionary<uint, ActionRule> Rules { get; set; } = new();

    /// <summary>Each character's own changes, by "Name@World".</summary>
    public Dictionary<string, CharacterPreset> Characters { get; set; } = new();

    /// <summary>User patterns from before presets wait here for the first character that logs in.</summary>
    public bool MoveOldEditsToFirstCharacter { get; set; }

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
        }

        if (this.Version < 5)
        {
            // Whole-clip fades become real fades.
            foreach (var r in this.Rules.Values)
            {
                foreach (var p in r.Pulses)
                    p.UpgradeShape();
            }

            // "Ignore effect on the target" is now just that cue being off.
            if (this.Hits.IgnoreTargetEffect)
                this.Hits.Sources &= ~HitSource.TargetVfx;
            this.Hits.IgnoreTargetEffect = false;

            // Own patterns and switched-off skills go into the first character's preset.
            this.MoveOldEditsToFirstCharacter = this.Rules.Values.Any(r => r.Origin == RuleOrigin.User)
                                                || this.Rules.Values.Any(r => !r.Enabled) || this.AutoDisabled.Count > 0;
        }

        if (this.Version < 6)
        {
            // New cue: strike sounds inside effects, on by default.
            this.Hits.Sources |= HitSource.EffectStrike;
        }

        if (this.Version < 7)
        {
            // Clips shortened after their fades were set could hold fades longer than themselves.
            foreach (var r in this.Rules.Values.Concat(this.Characters.Values.SelectMany(c => c.Rules.Values)))
            {
                foreach (var p in r.Pulses)
                    p.FitFades();
            }
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
