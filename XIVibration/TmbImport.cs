using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using XIVibration.Timeline;

namespace XIVibration;

/// <summary>Reads C120 "Controller Vibration" entries out of a .tmb animation timeline.</summary>
public static class TmbImport
{
    /// <summary>C120 fields: duration (frames), unknown (0), wave type (1, 2 or 3).</summary>
    public sealed record C120Entry(ushort Id, ushort TimeFrames, int DurationFrames, int Unknown, int WaveType);

    public static List<C120Entry> ReadC120(byte[] data) => FromEntries(TmbParser.Parse(data));

    public static List<C120Entry> FromEntries(IEnumerable<TmbParser.Entry> entries)
        => entries
            .Where(e => e.Tag == "C120" && e.Fields.Length >= 3)
            .Select(e => new C120Entry(e.Id, e.TimeFrames, e.Fields[0], e.Fields[1], e.Fields[2]))
            .OrderBy(e => e.TimeFrames)
            .ToList();

    /// <summary>Load from a file on disk, or else from the game data (e.g. chara/action/...tmb).</summary>
    public static byte[] Load(string path)
    {
        path = path.Trim().Trim('"');
        if (File.Exists(path))
            return File.ReadAllBytes(path);

        var file = Plugin.DataManager.GetFile(path.Replace('\\', '/'));
        if (file != null)
            return file.Data;

        throw new FileNotFoundException($"Not found on disk or in game data: {path}");
    }

    public static List<VibrationPulse> ToPulses(IEnumerable<C120Entry> entries)
        => entries.Select(e =>
        {
            var (strong, weak, shape) = Presets.ForWaveType(e.WaveType);
            return new VibrationPulse
            {
                DelayMs = (int)MathF.Round(e.TimeFrames * TmbParser.MsPerFrame),
                DurationMs = Math.Max(16, (int)MathF.Round(e.DurationFrames * TmbParser.MsPerFrame)),
                Strong = strong,
                Weak = weak,
                Shape = shape,
            };
        }).ToList();
}
