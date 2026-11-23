using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace XIVibration.Timeline;

/// <summary>
/// Minimal reader for .tmb animation timelines. Only the timed entries are needed:
/// <code>
///   0x00 char[4] magic ("C011", "C120", ...)
///   0x04 int32   entry size
///   0x08 uint16  entry id
///   0x0A uint16  start time, frames at 30 fps
///   0x0C ...     entry fields (int32)
/// </code>
/// Entries of interest:
///   C011 - the hit frame: the moment the skill's effect lands on the target.
///   C120 - controller vibration: duration (frames), unknown, wave type.
///   C131 - end of the action's animation.
/// </summary>
public static class TmbParser
{
    public const float FramesPerSecond = 30f;
    public const float MsPerFrame = 1000f / FramesPerSecond;

    public sealed record Entry(string Tag, ushort Id, ushort TimeFrames, int[] Fields);

    public static List<Entry> Parse(byte[] data)
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
                System.Text.Encoding.ASCII.GetString(data, o, 4),
                BitConverter.ToUInt16(data, o + 8),
                BitConverter.ToUInt16(data, o + 10),
                fields));
        }

        return result.OrderBy(e => e.TimeFrames).ToList();
    }

    private static bool IsDigit(byte b) => b >= '0' && b <= '9';
}
