using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace XIVibration.Audio;

/// <summary>A sound an effect plays: which file, which sound in it, and when (frames from the effect's start).</summary>
public sealed record EffectSound(string Path, int Index, int StartFrame);

/// <summary>
/// Finds the sounds a .avfx effect plays. Effects (mods especially) often carry a skill's strike sounds.
/// <code>
///   blocks: 4-char tag (reversed), u32 size, payload, padded to 4 bytes
///   AVFX
///     Schd: scheduler; Item { bEna, StTm, TlNo }   timeline TlNo starts at frame StTm
///     TmLn: timeline;  Item { bEna, StTm, EdTm, BdNo, EfNo, EmNo, Plfm }...   emitter EmNo starts at StTm
///     Emit: emitter;   SdNm (sound path), SdNo (sound index)
/// </code>
/// A timeline's last Item block holds all of its items (earlier blocks repeat the first ones).
/// </summary>
public static class AvfxSounds
{
    private readonly record struct Block(string Tag, int Offset, int Size);

    public static List<EffectSound> Parse(byte[] data)
    {
        var result = new List<EffectSound>();
        if (data.Length < 8)
            return result;

        var root = Blocks(data, 0, data.Length).FirstOrDefault();
        if (root.Tag != "AVFX")
            return result;

        var schedules = new List<Dictionary<string, int>>();
        var timelines = new List<List<Dictionary<string, int>>>();
        var emitters = new List<(string Sound, int Index)>();

        foreach (var b in Blocks(data, root.Offset, root.Offset + root.Size))
        {
            switch (b.Tag)
            {
                case "Schd":
                    schedules.AddRange(Items(data, b, "ItCn"));
                    break;
                case "TmLn":
                    timelines.Add(Items(data, b, "TICn"));
                    break;
                case "Emit":
                    string sound = string.Empty;
                    var index = 0;
                    foreach (var f in Blocks(data, b.Offset, b.Offset + b.Size))
                    {
                        if (f.Tag == "SdNm")
                            sound = ReadString(data, f);
                        else if (f.Tag == "SdNo" && f.Size == 4)
                            index = BitConverter.ToInt32(data, f.Offset);
                    }

                    emitters.Add((sound, Math.Max(0, index)));
                    break;
            }
        }

        foreach (var s in schedules)
        {
            if (s.GetValueOrDefault("bEna", 1) == 0)
                continue;
            var tl = s.GetValueOrDefault("TlNo", -1);
            if (tl < 0 || tl >= timelines.Count)
                continue;
            var start = s.GetValueOrDefault("StTm", 0);

            foreach (var item in timelines[tl])
            {
                if (item.GetValueOrDefault("bEna", 1) == 0)
                    continue;
                var em = item.GetValueOrDefault("EmNo", -1);
                if (em < 0 || em >= emitters.Count || emitters[em].Sound.Length == 0)
                    continue;
                result.Add(new EffectSound(emitters[em].Sound, emitters[em].Index, start + item.GetValueOrDefault("StTm", 0)));
            }
        }

        return result.Distinct().OrderBy(r => r.StartFrame).ToList();
    }

    /// <summary>
    /// The items of a scheduler or timeline. If the last Item block already holds all of them
    /// (the usual layout), only that one is used; otherwise every block is read.
    /// </summary>
    private static List<Dictionary<string, int>> Items(byte[] data, Block parent, string countTag)
    {
        var count = -1;
        var blocks = new List<Block>();
        foreach (var b in Blocks(data, parent.Offset, parent.Offset + parent.Size))
        {
            if (b.Tag == countTag && b.Size == 4)
                count = BitConverter.ToInt32(data, b.Offset);
            else if (b.Tag == "Item")
                blocks.Add(b);
        }

        if (blocks.Count == 0)
            return new();

        var last = SplitItems(data, blocks[^1]);
        if (count >= 0 && last.Count >= count)
            return last.Take(count).ToList();

        return blocks.SelectMany(b => SplitItems(data, b)).ToList();
    }

    /// <summary>One Item block can hold several items in a row; each starts with "bEna".</summary>
    private static List<Dictionary<string, int>> SplitItems(byte[] data, Block item)
    {
        var list = new List<Dictionary<string, int>>();
        Dictionary<string, int>? cur = null;
        foreach (var f in Blocks(data, item.Offset, item.Offset + item.Size))
        {
            if (f.Tag == "bEna" || cur == null)
            {
                cur = new Dictionary<string, int>();
                list.Add(cur);
            }

            cur[f.Tag] = f.Size switch
            {
                1 => data[f.Offset],
                2 => BitConverter.ToInt16(data, f.Offset),
                4 => BitConverter.ToInt32(data, f.Offset),
                _ => 0,
            };
        }

        return list;
    }

    private static IEnumerable<Block> Blocks(byte[] d, int start, int end)
    {
        var o = start;
        end = Math.Min(end, d.Length);
        while (o + 8 <= end)
        {
            var tag = new string(new[] { (char)d[o + 3], (char)d[o + 2], (char)d[o + 1], (char)d[o] }).Trim('\0', ' ');
            var size = BitConverter.ToInt32(d, o + 4);
            if (size < 0 || o + 8 + size > d.Length)
                yield break;
            yield return new Block(tag, o + 8, size);
            o += 8 + ((size + 3) & ~3);
        }
    }

    private static string ReadString(byte[] d, Block b)
    {
        var end = Array.IndexOf(d, (byte)0, b.Offset, b.Size);
        var len = (end < 0 ? b.Offset + b.Size : end) - b.Offset;
        return len <= 0 ? string.Empty : Encoding.ASCII.GetString(d, b.Offset, len);
    }
}
