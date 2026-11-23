using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace XIVibration;

/// <summary>
/// Character presets to and from text: a readable .json file, or a short code
/// ("XIVIB1:" + base64 of the gzipped JSON) to paste in chat.
/// </summary>
public static class PresetTransfer
{
    public const string CodePrefix = "XIVIB1:";
    public const string FormatName = "xivibration-preset";

    private sealed class Document
    {
        public string Format { get; set; } = FormatName;

        public int Version { get; set; } = 1;

        public string Character { get; set; } = string.Empty;

        public string World { get; set; } = string.Empty;

        public List<ActionRule> Rules { get; set; } = new();

        public List<uint> Disabled { get; set; } = new();
    }

    private static readonly JsonSerializerSettings Settings = new()
    {
        TypeNameHandling = TypeNameHandling.None,
        MissingMemberHandling = MissingMemberHandling.Ignore,
        NullValueHandling = NullValueHandling.Ignore,
    };

    public static string ToJson(CharacterPreset preset, bool indented = true)
        => JsonConvert.SerializeObject(new Document
        {
            Character = preset.Name,
            World = preset.World,
            Rules = preset.Rules.Values.OrderBy(r => r.ActionId).ToList(),
            Disabled = preset.Disabled.OrderBy(x => x).ToList(),
        }, indented ? Formatting.Indented : Formatting.None, Settings);

    public static string ToCode(CharacterPreset preset)
    {
        var bytes = Encoding.UTF8.GetBytes(ToJson(preset, false));
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize, true))
            gz.Write(bytes, 0, bytes.Length);
        return CodePrefix + Convert.ToBase64String(ms.ToArray());
    }

    /// <summary>Reads a preset from a code or JSON text. Throws <see cref="FormatException"/> with a readable message.</summary>
    public static CharacterPreset Parse(string text)
    {
        text = (text ?? string.Empty).Trim();
        if (text.Length == 0)
            throw new FormatException("Nothing to import (the clipboard or file is empty).");

        string json;
        if (text.StartsWith(CodePrefix, StringComparison.Ordinal))
        {
            try
            {
                var data = Convert.FromBase64String(text[CodePrefix.Length..].Trim());
                using var gz = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
                using var reader = new StreamReader(gz, Encoding.UTF8);
                json = reader.ReadToEnd();
            }
            catch (Exception)
            {
                throw new FormatException("The preset code is damaged or incomplete.");
            }
        }
        else if (text.StartsWith('{'))
        {
            json = text;
        }
        else
        {
            throw new FormatException("This isn't an XIVibration preset (expected a code starting with XIVIB1: or a .json preset).");
        }

        Document? doc;
        try
        {
            doc = JsonConvert.DeserializeObject<Document>(json, Settings);
        }
        catch (Exception ex)
        {
            throw new FormatException("The preset can't be read: " + ex.Message);
        }

        if (doc == null || doc.Format != FormatName)
            throw new FormatException("This isn't an XIVibration preset.");
        if (doc.Version > 1)
            throw new FormatException("This preset was made by a newer version of XIVibration. Update the plugin first.");

        var preset = new CharacterPreset { Name = doc.Character ?? string.Empty, World = doc.World ?? string.Empty };
        foreach (var r in doc.Rules ?? new())
        {
            if (r == null || r.ActionId == 0)
                continue;
            r.Origin = RuleOrigin.User;
            r.Anchor = PulseAnchor.AnimationStart;
            r.Strength = Math.Clamp(r.Strength, 0f, 2f);
            r.Pulses = (r.Pulses ?? new()).Where(p => p != null).Select(Sanitize).ToList();
            preset.Rules[r.ActionId] = r;
        }

        foreach (var id in doc.Disabled ?? new())
            preset.Disabled.Add(id);
        return preset;
    }

    /// <summary>Merge: imported skills replace mine, the rest stays. Replace: the preset becomes the import.</summary>
    public static void Apply(CharacterPreset target, CharacterPreset imported, bool replace)
    {
        if (replace)
        {
            target.Rules.Clear();
            target.Disabled.Clear();
        }

        foreach (var (id, rule) in imported.Rules)
        {
            target.Rules[id] = Copy(rule);
            target.Disabled.Remove(id);
        }

        foreach (var id in imported.Disabled)
            target.Disabled.Add(id);
        target.Updated = DateTime.Now;
    }

    public static ActionRule Copy(ActionRule r) => new()
    {
        ActionId = r.ActionId,
        Origin = RuleOrigin.User,
        Enabled = r.Enabled,
        Strength = r.Strength,
        Anchor = PulseAnchor.AnimationStart,
        Pulses = Configuration.ClonePulses(r.Pulses),
    };

    private static VibrationPulse Sanitize(VibrationPulse p)
    {
        p.UpgradeShape();
        p.DelayMs = Math.Clamp(p.DelayMs, 0, 10000);
        p.DurationMs = Math.Clamp(p.DurationMs, 16, 5000);
        p.Strong = Math.Clamp(p.Strong, 0, 100);
        p.Weak = Math.Clamp(p.Weak, 0, 100);
        p.FadeInMs = Math.Clamp(p.FadeInMs, 0, p.DurationMs);
        p.FadeOutMs = Math.Clamp(p.FadeOutMs, 0, p.DurationMs - p.FadeInMs);
        p.FadeInCurve = Math.Clamp(p.FadeInCurve, -100, 100);
        p.FadeOutCurve = Math.Clamp(p.FadeOutCurve, -100, 100);
        if (!Enum.IsDefined(p.Shape))
            p.Shape = PulseShape.Constant;
        return p;
    }
}
