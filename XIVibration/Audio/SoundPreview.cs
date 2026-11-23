using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using XIVibration.Timeline;

namespace XIVibration.Audio;

/// <summary>Mixes all sounds (C063) of a skill's animation at their times into one WAV.</summary>
public sealed class SoundPreview
{
    public const int OutputRate = 48000;
    public const float MaxSeconds = 8f;
    private const int CacheSize = 48;

    // Decoded sounds, most recently used last.
    private readonly List<(string Key, DecodedSound? Sound)> cache = new();

    /// <param name="read">Reads a game path the way the game would (mods included).</param>
    /// <returns>16-bit mono PCM WAV, or null when the skill has no readable sound.</returns>
    public byte[]? Mix(IReadOnlyList<TmbParser.Entry> entries, Func<string, (byte[]? Data, string Resolved, bool FromMod)> read, float volume)
    {
        var placed = new List<(int StartSample, DecodedSound Sound)>();
        foreach (var e in entries)
        {
            if (e.Tag != "C063" || !e.Path.EndsWith(".scd", StringComparison.OrdinalIgnoreCase))
                continue;
            var sound = this.Decode(e.Path, e.Fields.Length > 3 ? Math.Max(0, e.Fields[3]) : 0, read);
            if (sound != null)
                placed.Add(((int)(e.TimeFrames * TmbParser.MsPerFrame * OutputRate / 1000f), sound));
        }

        if (placed.Count == 0)
            return null;

        var length = placed.Max(p => p.StartSample + (int)((long)p.Sound.Samples.Length * OutputRate / p.Sound.SampleRate));
        length = Math.Min(length, (int)(MaxSeconds * OutputRate));
        var mix = new float[length];
        foreach (var (start, sound) in placed)
        {
            // Linear resampling to the output rate.
            var ratio = sound.SampleRate / (double)OutputRate;
            var src = sound.Samples;
            for (var i = 0; start + i < length; i++)
            {
                var pos = i * ratio;
                var k = (int)pos;
                if (k + 1 >= src.Length)
                    break;
                var f = (float)(pos - k);
                mix[start + i] += src[k] + (src[k + 1] - src[k]) * f;
            }
        }

        // Never clip, then apply the preview volume.
        var peak = mix.Length == 0 ? 0f : mix.Max(Math.Abs);
        var gain = (peak > 0.98f ? 0.98f / peak : 1f) * Math.Clamp(volume, 0f, 1f);
        return ToWav(mix, gain);
    }

    private DecodedSound? Decode(string gamePath, int index, Func<string, (byte[]? Data, string Resolved, bool FromMod)> read)
    {
        var (data, resolved, _) = read(gamePath);
        if (data == null)
            return null;

        var key = $"{resolved}:{data.Length}:{index}";
        var hit = this.cache.FindIndex(c => c.Key == key);
        if (hit >= 0)
        {
            var c = this.cache[hit];
            this.cache.RemoveAt(hit);
            this.cache.Add(c);
            return c.Sound;
        }

        DecodedSound? sound;
        try
        {
            sound = ScdReader.Decode(data, index);
        }
        catch
        {
            sound = null;
        }

        this.cache.Add((key, sound));
        if (this.cache.Count > CacheSize)
            this.cache.RemoveAt(0);
        return sound;
    }

    private static byte[] ToWav(float[] samples, float gain)
    {
        using var ms = new MemoryStream(44 + samples.Length * 2);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + samples.Length * 2);
        w.Write("WAVEfmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1); // PCM
        w.Write((short)1); // mono
        w.Write(OutputRate);
        w.Write(OutputRate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8.ToArray());
        w.Write(samples.Length * 2);
        foreach (var s in samples)
            w.Write((short)Math.Clamp(s * gain * 32767f, short.MinValue, short.MaxValue));
        return ms.ToArray();
    }
}

/// <summary>Plays a WAV from memory through winmm (works under Wine too).</summary>
public sealed class PreviewPlayer : IDisposable
{
    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;
    private const uint SndMemory = 0x0004;

    private GCHandle pinned;

    public string? LastError { get; private set; }

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);

    public bool Play(byte[] wav)
    {
        try
        {
            this.Stop();
            this.pinned = GCHandle.Alloc(wav, GCHandleType.Pinned);
            if (!PlaySound(this.pinned.AddrOfPinnedObject(), IntPtr.Zero, SndMemory | SndAsync | SndNoDefault))
            {
                this.LastError = $"PlaySound failed ({Marshal.GetLastWin32Error()})";
                return false;
            }

            this.LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            this.LastError = ex.Message;
            return false;
        }
    }

    public void Stop()
    {
        try
        {
            PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
        }
        catch (Exception ex)
        {
            this.LastError = ex.Message;
        }

        if (this.pinned.IsAllocated)
            this.pinned.Free();
    }

    public void Dispose() => this.Stop();
}
