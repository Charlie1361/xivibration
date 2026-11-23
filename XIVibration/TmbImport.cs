using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace XIVibration;

/// <summary>
/// Reads C120 "Controller Vibration" entries out of a .tmb animation timeline.
///
/// C120 layout (0x18 bytes):
///   0x00 char[4] "C120"
///   0x04 int32   entry size (0x18)
///   0x08 uint16  entry id
///   0x0A uint16  start time (frames, 30 fps)
///   0x0C int32   duration (frames)
///   0x10 int32   unknown (always 0 so far)
///   0x14 int32   wave type (1, 2 or 3 seen)
/// </summary>
public static class TmbImport
{
    public const float FramesPerSecond = 30f;

    public sealed record C120Entry(ushort Id, ushort TimeFrames, int DurationFrames, int Unknown, int WaveType);

    public static List<C120Entry> ReadC120(byte[] data)
    {
        var result = new List<C120Entry>();
        if (data.Length < 12 || data[0] != 'T' || data[1] != 'M' || data[2] != 'L' || data[3] != 'B')
            throw new InvalidDataException("Not a TMB file (missing TMLB header).");

        for (var o = 0; o + 0x18 <= data.Length; o += 4)
        {
            if (data[o] != 'C' || data[o + 1] != '1' || data[o + 2] != '2' || data[o + 3] != '0')
                continue;
            if (BitConverter.ToInt32(data, o + 4) != 0x18)
                continue;
            result.Add(new C120Entry(
                BitConverter.ToUInt16(data, o + 8),
                BitConverter.ToUInt16(data, o + 10),
                BitConverter.ToInt32(data, o + 12),
                BitConverter.ToInt32(data, o + 16),
                BitConverter.ToInt32(data, o + 20)));
        }

        return result.OrderBy(e => e.TimeFrames).ToList();
    }

    /// <summary>Load from a file on disk, or else from the game data (e.g. chara/action/...tmb).</summary>
    public static byte[] Load(string path)
    {
        path = path.Trim().Trim('"');
        if (File.Exists(path))
            return File.ReadAllBytes(path);

        var gamePath = path.Replace('\\', '/');
        var file = Plugin.DataManager.GetFile(gamePath);
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
                DelayMs = (int)MathF.Round(e.TimeFrames * 1000f / FramesPerSecond),
                DurationMs = Math.Max(16, (int)MathF.Round(e.DurationFrames * 1000f / FramesPerSecond)),
                Strong = strong,
                Weak = weak,
                Shape = shape,
            };
        }).ToList();
}
