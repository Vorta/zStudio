using System.Buffers.Binary;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core.Sources;

/// <summary>A PCM format declared for a sound bank (sounds.zrd HIGH/MED/LOW: rate, bits, channels).</summary>
public readonly record struct WaveFormat(int Rate, int Bits, int Channels)
{
    public long Quality => (long)Rate * Bits * Channels;
}

/// <summary>
/// Converts PCM WAVs to a sound bank's declared format. The declaration is a ceiling: like the retail banks, rate, sample size
/// and channel count each keep the source's value when it is already lower. Cue markers, which the engine turns into playback
/// times, are rescaled with the samples.
/// </summary>
public static class WaveConverter
{
    public static WaveFormat Format(ReadOnlyMemory<byte> wave) => Format(WaveDecoder.Read(wave));
    private static WaveFormat Format(WaveInfo info) => info.SampleRate <= int.MaxValue ? new((int)info.SampleRate, info.BitsPerSample, info.Channels)
        : throw new InvalidDataException($"A sample rate of {info.SampleRate:N0} Hz is not supported.");

    /// <summary>Lanczos kernel half-width in output samples.</summary>
    private const int Taps = 16;
    /// <summary>Source frames decoded together; the converted samples of each block go straight into the result.</summary>
    private const int BlockFrames = 1 << 15;
    /// <summary>
    /// The most source frames a block may hold for one output block: the kernel spans 32 source frames per output frame
    /// ratio, so only a rate drop of more than 32,768 times (a source of tens of megahertz) on a long sound needs more.
    /// </summary>
    internal const int MaximumBlockFrames = 1 << 20;

    public static byte[] Convert(ReadOnlyMemory<byte> wave, WaveFormat declared, CancellationToken token = default)
    {
        var info = WaveDecoder.Read(wave, token);
        if (info.Encoding != 1 || info.BitsPerSample is not (8 or 16)) throw new InvalidDataException("Only 8- or 16-bit PCM WAVs can be converted.");
        if (declared.Bits is not (8 or 16) || declared.Channels is not (1 or 2) || declared.Rate is < 1000 or > 192_000) throw new InvalidDataException($"Unsupported declared format {declared.Rate} Hz, {declared.Bits}-bit, {declared.Channels} channels.");
        WaveFormat source = Format(info), target = Target(source, declared);
        if (target == source) return wave.ToArray();
        // Decode to floating-point frames, mix channels, then resample with a windowed-sinc low-pass when the rate drops. Each
        // output frame depends only on the source frames its kernel covers, so the sound is converted a block at a time into
        // the result: a long sound is held as its source and its result, never as floating-point copies of the whole sound.
        int frames = info.DataLength / info.BlockAlign;
        double ratio = (double)target.Rate / info.SampleRate, scale = Math.Min(1, ratio);
        bool resample = target.Rate != info.SampleRate;
        int outFrames = Math.Max(1, (int)Math.Round(frames * ratio));
        // The source frames one output block needs: the block's span of the source plus the kernel's reach on both sides.
        int outBlock = resample ? Math.Max(1, (int)(BlockFrames * ratio)) : BlockFrames;
        double reach = resample ? outBlock / ratio + 2 * (Taps / scale) + 3 : outBlock;
        if (Math.Min(frames, reach) > MaximumBlockFrames)
            throw new InvalidDataException($"Converting {info.SampleRate:N0} Hz to {target.Rate:N0} Hz is not supported: the rate drops too far for a sound of {frames:N0} frames.");
        int capacity = (int)Math.Min(frames, Math.Ceiling(reach));
        float[][] block = new float[target.Channels][];
        for (int c = 0; c < target.Channels; c++) block[c] = new float[capacity];

        var cues = info.Cues.Select(c => (c.Id, Position: (uint)Math.Min(outFrames, Math.Round(c.SampleOffset * ratio)))).ToArray();
        int outStep = target.Bits / 8, align = outStep * target.Channels;
        byte[] result = Allocate(target, checked(outFrames * align), cues, out int pcmOffset);
        var data = wave.Span.Slice(info.DataOffset, info.DataLength);
        var pcm = result.AsSpan(pcmOffset, outFrames * align);
        Span<float> values = stackalloc float[2];
        for (int o0 = 0; o0 < outFrames; o0 += outBlock)
        {
            token.ThrowIfCancellationRequested();
            int o1 = Math.Min(outFrames, o0 + outBlock);
            // The source frames this block reads: for a resampled block, from the first frame of its first kernel to the
            // last of its last (both move forward with the output frame).
            int lo = resample ? Math.Max(0, First(o0)) : o0, hi = resample ? Math.Min(frames - 1, Last(o1 - 1)) : Math.Min(frames, o1) - 1;
            Decode(data, info, target.Channels, lo, hi - lo + 1, block);
            for (int o = o0; o < o1; o++)
            {
                if (resample)
                {
                    double center = o / ratio, weights = 0, sum0 = 0, sum1 = 0;
                    for (int j = Math.Max(0, First(o)); j <= Math.Min(frames - 1, Last(o)); j++)
                    {
                        double x = (j - center) * scale, w = x == 0 ? 1 : Math.Abs(x) >= Taps ? 0 : Sinc(x) * Sinc(x / Taps);
                        sum0 += block[0][j - lo] * w; if (target.Channels == 2) sum1 += block[1][j - lo] * w; weights += w;
                    }
                    values[0] = weights == 0 ? 0 : (float)(sum0 / weights); values[1] = weights == 0 ? 0 : (float)(sum1 / weights);
                }
                // An empty sound still becomes one silent frame.
                else for (int c = 0; c < target.Channels; c++) values[c] = o < frames ? block[c][o - lo] : 0;
                for (int c = 0; c < target.Channels; c++)
                {
                    float value = Math.Clamp(values[c], -1f, 1f); int p = o * align + c * outStep;
                    if (outStep == 1) pcm[p] = (byte)Math.Clamp((int)MathF.Round(value * 127f + 128f), 0, 255);
                    else BinaryPrimitives.WriteInt16LittleEndian(pcm[p..], (short)Math.Clamp((int)MathF.Round(value * 32767f), short.MinValue, short.MaxValue));
                }
            }
        }
        return result;

        // The kernel of output frame i covers source frames First(i)..Last(i); when downsampling its cutoff follows the new
        // Nyquist frequency.
        int First(int i) => (int)Math.Floor(i / ratio - Taps / scale);
        int Last(int i) => (int)Math.Ceiling(i / ratio + Taps / scale);
        static double Sinc(double x) => Math.Sin(Math.PI * x) / (Math.PI * x);
    }

    /// <summary>Decodes <paramref name="count"/> source frames from <paramref name="first"/> into <paramref name="into"/>, mixed down to <paramref name="channels"/>.</summary>
    private static void Decode(ReadOnlySpan<byte> data, WaveInfo info, int channels, int first, int count, float[][] into)
    {
        int sourceChannels = info.Channels, step = info.BitsPerSample / 8;
        for (int k = 0; k < count; k++)
        {
            int f = first + k; float mix = 0;
            for (int c = 0; c < sourceChannels; c++)
            {
                int p = f * info.BlockAlign + c * step;
                float value = step == 1 ? (data[p] - 128) / 128f : BinaryPrimitives.ReadInt16LittleEndian(data[p..]) / 32768f;
                if (channels == sourceChannels) into[c][k] = value; else mix += value;
            }
            if (channels != sourceChannels) for (int c = 0; c < channels; c++) into[c][k] = mix / sourceChannels;
        }
    }

    /// <summary>The format a source is converted to: each of rate, sample size and channels is the lower of source and declaration.</summary>
    public static WaveFormat Target(WaveFormat source, WaveFormat declared) =>
        new(Math.Min(source.Rate, declared.Rate), Math.Min(source.Bits, declared.Bits), Math.Min(source.Channels, declared.Channels));

    /// <summary>A canonical RIFF/WAVE: fmt, optional cue, then data, as in the retail banks.</summary>
    public static byte[] Write(WaveFormat format, ReadOnlySpan<byte> pcm, IReadOnlyList<(uint Id, uint Position)> cues)
    {
        byte[] result = Allocate(format, pcm.Length, cues, out int pcmOffset);
        pcm.CopyTo(result.AsSpan(pcmOffset));
        return result;
    }

    /// <summary>A RIFF/WAVE of exactly its size with every chunk but the samples written; returns where the samples go.</summary>
    private static byte[] Allocate(WaveFormat format, int pcmLength, IReadOnlyList<(uint Id, uint Position)> cues, out int pcmOffset)
    {
        int cueBytes = cues.Count == 0 ? 0 : 8 + 4 + cues.Count * 24, pad = pcmLength & 1;
        long length = 12L + 24 + cueBytes + 8 + pcmLength + pad;
        if (length > Array.MaxLength) throw new InvalidDataException("The converted sound would be larger than 2 GiB.");
        byte[] result = new byte[length];
        int at = 0;
        void Tag(ReadOnlySpan<byte> tag) { tag.CopyTo(result.AsSpan(at)); at += 4; }
        void Int(int value) { BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(at), value); at += 4; }
        void Short(int value) { BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(at), (short)value); at += 2; }
        Tag("RIFF"u8); Int(4 + 24 + cueBytes + 8 + pcmLength + pad); Tag("WAVE"u8);
        int align = format.Bits / 8 * format.Channels;
        Tag("fmt "u8); Int(16); Short(1); Short(format.Channels); Int(format.Rate); Int(format.Rate * align); Short(align); Short(format.Bits);
        if (cues.Count > 0)
        {
            Tag("cue "u8); Int(4 + cues.Count * 24); Int(cues.Count);
            foreach (var (id, position) in cues) { Int((int)id); Int((int)position); Tag("data"u8); Int(0); Int(0); Int((int)position); }
        }
        Tag("data"u8); Int(pcmLength);
        // The samples follow, then the pad byte (already zero).
        pcmOffset = at;
        return result;
    }
}
