using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace XIVibration.Timeline;

/// <summary>
/// Minimal reader for .tmb animation timelines. Only the timed entries are needed:
/// <code>
///   0x00 char[4] magic ("C011", "C120", ...)
///   0x04 int32   entry size
///   0x08 uint16  entry id
///   0x0A uint16  start time, frames at 30 fps
///   0x0C ...     entry fields (int32); string fields are offsets from 0x08
/// </code>
/// Entries used:
///   C002 TMB (plays a sub-timeline)   C011 Fly Text (damage number)
///   C012 VFX                          C063 Sound
///   C067 Flinch (target hit reaction) C120 Controller Vibration
///   C131 Animation canceled by movement
/// </summary>
public static class TmbParser
{
    public const float FramesPerSecond = 30f;
    public const float MsPerFrame = 1000f / FramesPerSecond;

    public sealed record Entry(string Tag, ushort Id, int TimeFrames, int[] Fields, string Path);

    public static List<Entry> Parse(byte[] data, int timeOffset = 0)
    {
        if (data.Length < 12 || data[0] != 'T' || data[1] != 'M' || data[2] != 'L' || data[3] != 'B')
            throw new InvalidDataException("Not a TMB file (missing TMLB header).");

        var result = new List<Entry>();
        for (var o = 12; o + 12 <= data.Length; o += 4)
        {
            if (data[o] != 'C' || !IsDigit(data[o + 1]) || !IsDigit(data[o + 2]) || !IsDigit(data[o + 3]))
                continue;

            var size = BitConverter.ToInt32(data, o + 4);
            if (size < 12 || size > 0x400 || (size & 3) != 0 || o + size > data.Length)
                continue;

            var fields = new int[(size - 12) / 4];
            for (var i = 0; i < fields.Length; i++)
                fields[i] = BitConverter.ToInt32(data, o + 12 + i * 4);

            result.Add(new Entry(
                Encoding.ASCII.GetString(data, o, 4),
                BitConverter.ToUInt16(data, o + 8),
                BitConverter.ToUInt16(data, o + 10) + timeOffset,
                fields,
                FindPath(data, o, fields)));
        }

        return result;
    }

    /// <summary>
    /// Parses a timeline and the sub-timelines it plays (C002), with their times shifted
    /// to when they start. <paramref name="load"/> returns a file for a game path, or null.
    /// </summary>
    public static List<Entry> ParseWithSubTimelines(byte[] data, Func<string, byte[]?> load, int timeOffset = 0, int depth = 0)
    {
        var entries = Parse(data, timeOffset);
        if (depth >= 3)
            return entries;

        foreach (var c in entries.Where(e => e.Tag == "C002" && e.Path.StartsWith("chara/action/", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            try
            {
                var sub = load(c.Path.EndsWith(".tmb", StringComparison.OrdinalIgnoreCase) ? c.Path : c.Path + ".tmb");
                if (sub != null)
                    entries.AddRange(ParseWithSubTimelines(sub, load, c.TimeFrames, depth + 1));
            }
            catch
            {
                // a broken sub-timeline shouldn't lose the main one
            }
        }

        return entries;
    }

    private static readonly string[] KnownRoots = { "chara/", "vfx/", "sound/", "soundx/", "bgcommon/", "bg/", "common/" };

    /// <summary>The entry's file path. Prefers a field that starts with a real game folder.</summary>
    private static string FindPath(byte[] data, int entry, int[] fields)
    {
        string? fallback = null;
        foreach (var v in fields)
        {
            var start = entry + 8 + v;
            if (v <= 0 || start >= data.Length)
                continue;
            var end = Array.IndexOf(data, (byte)0, start);
            if (end <= start + 3 || end - start > 256)
                continue;

            var ok = true;
            for (var i = start; i < end && ok; i++)
                ok = data[i] >= 32 && data[i] < 127;
            if (!ok)
                continue;

            var s = Encoding.ASCII.GetString(data, start, end - start);
            if (!s.Contains('/'))
                continue;
            if (KnownRoots.Any(r => s.StartsWith(r, StringComparison.OrdinalIgnoreCase)))
                return s;
            fallback ??= s;
        }

        return fallback ?? string.Empty;
    }

    private static bool IsDigit(byte b) => b >= '0' && b <= '9';
}
