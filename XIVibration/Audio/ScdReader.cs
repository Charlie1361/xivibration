using System;
using System.IO;
using System.Text;

namespace XIVibration.Audio;

/// <summary>A decoded sound: mono samples in -1..1.</summary>
public sealed record DecodedSound(float[] Samples, int SampleRate, int Format);

/// <summary>
/// Reads the audio out of a game .scd sound file.
/// <code>
///   "SEDBSSCF" header; u16 @0x0E = size of that header
///   at the header end: u16 sound, track, audio counts; u32 @+0x0C = audio offset table
///   audio entry: dataSize, channels, sampleRate, format, loopStart, loopEnd, subInfoSize, flags  (0x20 bytes)
///                [MARK chunk if flags &amp; 1] sub-info ... data
///   format 0x0C = MS-ADPCM (sub-info is a WAVEFORMATEX with the ADPCM coefficients)
///   format 0x06 = Ogg Vorbis (sub-info: encode mode/byte, seek table, header; then the data),
///                 optionally XOR-scrambled
/// </code>
/// Layout as documented by VFXEditor's SCD code.
/// </summary>
public static class ScdReader
{
    public const int FormatVorbis = 0x06;
    public const int FormatMsAdpcm = 0x0C;

    /// <summary>Longer sounds are cut: nothing in a skill animation needs more.</summary>
    public const float MaxSeconds = 4f;

    public static DecodedSound? Decode(byte[] scd, int soundIndex)
    {
        if (scd.Length < 0x40 || Encoding.ASCII.GetString(scd, 0, 8) != "SEDBSSCF")
            return null;

        var headerSize = BitConverter.ToUInt16(scd, 0x0E);
        var audioCount = BitConverter.ToUInt16(scd, headerSize + 4);
        var audioTable = (int)BitConverter.ToUInt32(scd, headerSize + 0x0C);
        if (audioCount == 0)
            return null;

        // Sound i normally plays audio i; fall back to the nearest one that has data.
        var first = Math.Clamp(soundIndex, 0, audioCount - 1);
        for (var k = 0; k < audioCount; k++)
        {
            var i = (first + k) % audioCount;
            var entry = (int)BitConverter.ToUInt32(scd, audioTable + i * 4);
            if (entry <= 0 || entry + 0x20 > scd.Length)
                continue;
            var decoded = DecodeEntry(scd, entry);
            if (decoded != null)
                return decoded;
        }

        return null;
    }

    private static DecodedSound? DecodeEntry(byte[] scd, int entry)
    {
        var dataSize = BitConverter.ToInt32(scd, entry);
        var channels = BitConverter.ToInt32(scd, entry + 4);
        var rate = BitConverter.ToInt32(scd, entry + 8);
        var format = BitConverter.ToInt32(scd, entry + 12);
        var subInfoSize = BitConverter.ToInt32(scd, entry + 0x18);
        var flags = BitConverter.ToUInt32(scd, entry + 0x1C);
        if (dataSize <= 0 || channels <= 0 || channels > 8 || rate < 4000 || rate > 192000)
            return null;

        var sub = entry + 0x20;
        var markerSize = (flags & 1) != 0 ? BitConverter.ToInt32(scd, sub + 4) : 0;
        if (markerSize < 0 || sub + subInfoSize > scd.Length)
            return null;

        return format switch
        {
            FormatMsAdpcm => DecodeAdpcm(scd, sub + markerSize, subInfoSize - markerSize, sub + subInfoSize, dataSize, channels, rate),
            FormatVorbis => DecodeVorbis(scd, sub + markerSize, dataSize),
            _ => null,
        };
    }

    // ---------------------------------------------------------------- MS-ADPCM

    private static readonly int[] AdaptationTable = { 230, 230, 230, 230, 307, 409, 512, 614, 768, 614, 512, 409, 307, 230, 230, 230 };

    private static DecodedSound? DecodeAdpcm(byte[] scd, int fmt, int fmtSize, int data, int dataSize, int channels, int rate)
    {
        if (fmtSize < 20 || data + dataSize > scd.Length)
            return null;

        var blockAlign = BitConverter.ToUInt16(scd, fmt + 12);
        var numCoef = BitConverter.ToUInt16(scd, fmt + 20);
        if (blockAlign < 7 * channels || numCoef == 0 || numCoef > 32 || fmt + 22 + numCoef * 4 > scd.Length)
            return null;

        var coef1 = new int[numCoef];
        var coef2 = new int[numCoef];
        for (var i = 0; i < numCoef; i++)
        {
            coef1[i] = BitConverter.ToInt16(scd, fmt + 22 + i * 4);
            coef2[i] = BitConverter.ToInt16(scd, fmt + 24 + i * 4);
        }

        var samplesPerBlock = (blockAlign - 7 * channels) * 2 / channels + 2;
        var maxFrames = (int)(MaxSeconds * rate);
        var blocks = dataSize / blockAlign;
        var outFrames = Math.Min(blocks * samplesPerBlock, maxFrames);
        var mono = new float[outFrames];
        var written = 0;

        var pred = new int[channels];
        var delta = new int[channels];
        var s1 = new int[channels];
        var s2 = new int[channels];
        var frame = new float[channels];

        for (var b = 0; b < blocks && written < outFrames; b++)
        {
            var p = data + b * blockAlign;
            for (var c = 0; c < channels; c++) pred[c] = Math.Min(scd[p++], numCoef - 1);
            for (var c = 0; c < channels; c++) { delta[c] = BitConverter.ToInt16(scd, p); p += 2; }
            for (var c = 0; c < channels; c++) { s1[c] = BitConverter.ToInt16(scd, p); p += 2; }
            for (var c = 0; c < channels; c++) { s2[c] = BitConverter.ToInt16(scd, p); p += 2; }

            // The two header samples come first, older one first.
            Emit(s2);
            Emit(s1);

            var end = data + (b + 1) * blockAlign;
            var ch = 0;
            for (; p < end && written < outFrames; p++)
            {
                Nibble(scd[p] >> 4);
                Nibble(scd[p] & 0x0F);
            }

            void Nibble(int n)
            {
                var signed = n >= 8 ? n - 16 : n;
                var predictor = (s1[ch] * coef1[pred[ch]] + s2[ch] * coef2[pred[ch]]) >> 8;
                var sample = Math.Clamp(predictor + signed * delta[ch], short.MinValue, short.MaxValue);
                s2[ch] = s1[ch];
                s1[ch] = sample;
                delta[ch] = Math.Max(16, (AdaptationTable[n] * delta[ch]) >> 8);
                frame[ch] = sample / 32768f;
                if (++ch == channels)
                {
                    ch = 0;
                    Push();
                }
            }
        }

        if (written < mono.Length)
            Array.Resize(ref mono, written);
        return new DecodedSound(mono, rate, FormatMsAdpcm);

        void Emit(int[] samples)
        {
            for (var c = 0; c < channels; c++)
                frame[c] = samples[c] / 32768f;
            Push();
        }

        void Push()
        {
            if (written >= outFrames)
                return;
            var sum = 0f;
            for (var c = 0; c < channels; c++)
                sum += frame[c];
            mono[written++] = sum / channels;
        }
    }

    // ---------------------------------------------------------------- Vorbis

    private static DecodedSound? DecodeVorbis(byte[] scd, int info, int dataSize)
    {
        if (info + 0x20 > scd.Length)
            return null;

        var encodeMode = BitConverter.ToInt16(scd, info);
        var encodeByte = (byte)BitConverter.ToInt16(scd, info + 2);
        var seekTableSize = BitConverter.ToInt32(scd, info + 0x14);
        var headerSize = BitConverter.ToInt32(scd, info + 0x18);

        // Very old files carry a different header; not worth supporting.
        if (Encoding.ASCII.GetString(BitConverter.GetBytes(seekTableSize)).EndsWith("vor", StringComparison.Ordinal))
            return null;

        var headerStart = info + 0x20 + seekTableSize;
        if (seekTableSize < 0 || headerSize < 0 || headerStart > scd.Length)
            return null;

        // Unscrambled streams with the header inside the data: some files have a few extra bytes
        // before the first Ogg page, so start at the first "OggS" near where it should be.
        if (encodeMode == 0 && headerSize == 0)
        {
            var found = FindOggS(scd, headerStart, Math.Min(scd.Length - 4, headerStart + 256));
            if (found < 0)
                return null;
            headerStart = found;
            dataSize = Math.Min(dataSize, scd.Length - found);
        }

        var dataStart = headerStart + headerSize;
        if (dataStart + dataSize > scd.Length)
            return null;

        var ogg = new byte[headerSize + dataSize];
        Buffer.BlockCopy(scd, headerStart, ogg, 0, headerSize);
        Buffer.BlockCopy(scd, dataStart, ogg, headerSize, dataSize);

        if (encodeMode == 0x2002 && encodeByte != 0)
        {
            for (var i = 0; i < headerSize; i++)
                ogg[i] ^= encodeByte;
        }
        else if (encodeMode == 0x2003)
        {
            var byte1 = dataSize & 0x7F;
            var byte2 = byte1 & 0x3F;
            for (var i = 0; i < ogg.Length; i++)
                ogg[i] = (byte)(XorTable[(byte2 + i) & 0xFF] ^ ogg[i] ^ byte1);
        }

        using var stream = new MemoryStream(ogg, false);
        using var vorbis = new NVorbis.VorbisReader(stream, false);
        var channels = Math.Max(1, vorbis.Channels);
        var rate = vorbis.SampleRate;
        var maxFrames = (int)(MaxSeconds * rate);
        var mono = new float[maxFrames];
        var buffer = new float[4096 * channels];
        var written = 0;
        int read;
        while (written < maxFrames && (read = vorbis.ReadSamples(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i + channels <= read && written < maxFrames; i += channels)
            {
                var sum = 0f;
                for (var c = 0; c < channels; c++)
                    sum += buffer[i + c];
                mono[written++] = sum / channels;
            }
        }

        Array.Resize(ref mono, written);
        return new DecodedSound(mono, rate, FormatVorbis);
    }

    private static int FindOggS(byte[] d, int from, int to)
    {
        for (var i = Math.Max(0, from); i <= to; i++)
        {
            if (d[i] == 'O' && d[i + 1] == 'g' && d[i + 2] == 'g' && d[i + 3] == 'S')
                return i;
        }

        return -1;
    }

    // Scramble table used by the game for Vorbis sound data (encode mode 0x2003).
    private static readonly byte[] XorTable =
    {
        0x3A, 0x32, 0x32, 0x32, 0x03, 0x7E, 0x12, 0xF7, 0xB2, 0xE2, 0xA2, 0x67, 0x32, 0x32, 0x22, 0x32,
        0x32, 0x52, 0x16, 0x1B, 0x3C, 0xA1, 0x54, 0x7B, 0x1B, 0x97, 0xA6, 0x93, 0x1A, 0x4B, 0xAA, 0xA6,
        0x7A, 0x7B, 0x1B, 0x97, 0xA6, 0xF7, 0x02, 0xBB, 0xAA, 0xA6, 0xBB, 0xF7, 0x2A, 0x51, 0xBE, 0x03,
        0xF4, 0x2A, 0x51, 0xBE, 0x03, 0xF4, 0x2A, 0x51, 0xBE, 0x12, 0x06, 0x56, 0x27, 0x32, 0x32, 0x36,
        0x32, 0xB2, 0x1A, 0x3B, 0xBC, 0x91, 0xD4, 0x7B, 0x58, 0xFC, 0x0B, 0x55, 0x2A, 0x15, 0xBC, 0x40,
        0x92, 0x0B, 0x5B, 0x7C, 0x0A, 0x95, 0x12, 0x35, 0xB8, 0x63, 0xD2, 0x0B, 0x3B, 0xF0, 0xC7, 0x14,
        0x51, 0x5C, 0x94, 0x86, 0x94, 0x59, 0x5C, 0xFC, 0x1B, 0x17, 0x3A, 0x3F, 0x6B, 0x37, 0x32, 0x32,
        0x30, 0x32, 0x72, 0x7A, 0x13, 0xB7, 0x26, 0x60, 0x7A, 0x13, 0xB7, 0x26, 0x50, 0xBA, 0x13, 0xB4,
        0x2A, 0x50, 0xBA, 0x13, 0xB5, 0x2E, 0x40, 0xFA, 0x13, 0x95, 0xAE, 0x40, 0x38, 0x18, 0x9A, 0x92,
        0xB0, 0x38, 0x00, 0xFA, 0x12, 0xB1, 0x7E, 0x00, 0xDB, 0x96, 0xA1, 0x7C, 0x08, 0xDB, 0x9A, 0x91,
        0xBC, 0x08, 0xD8, 0x1A, 0x86, 0xE2, 0x70, 0x39, 0x1F, 0x86, 0xE0, 0x78, 0x7E, 0x03, 0xE7, 0x64,
        0x51, 0x9C, 0x8F, 0x34, 0x6F, 0x4E, 0x41, 0xFC, 0x0B, 0xD5, 0xAE, 0x41, 0xFC, 0x0B, 0xD5, 0xAE,
        0x41, 0xFC, 0x3B, 0x70, 0x71, 0x64, 0x33, 0x32, 0x12, 0x32, 0x32, 0x36, 0x70, 0x34, 0x2B, 0x56,
        0x22, 0x70, 0x3A, 0x13, 0xB7, 0x26, 0x60, 0xBA, 0x1B, 0x94, 0xAA, 0x40, 0x38, 0x00, 0xFA, 0xB2,
        0xE2, 0xA2, 0x67, 0x32, 0x32, 0x12, 0x32, 0xB2, 0x32, 0x32, 0x32, 0x32, 0x75, 0xA3, 0x26, 0x7B,
        0x83, 0x26, 0xF9, 0x83, 0x2E, 0xFF, 0xE3, 0x16, 0x7D, 0xC0, 0x1E, 0x63, 0x21, 0x07, 0xE3, 0x01,
    };
}
