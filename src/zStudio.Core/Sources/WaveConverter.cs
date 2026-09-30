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

    public static byte[] Convert(ReadOnlyMemory<byte> wave, WaveFormat declared, CancellationToken token = default)
    {
        var info = WaveDecoder.Read(wave, token);
        if (info.Encoding != 1 || info.BitsPerSample is not (8 or 16)) throw new InvalidDataException("Only 8- or 16-bit PCM WAVs can be converted.");
        if (declared.Bits is not (8 or 16) || declared.Channels is not (1 or 2) || declared.Rate is < 1000 or > 192_000) throw new InvalidDataException($"Unsupported declared format {declared.Rate} Hz, {declared.Bits}-bit, {declared.Channels} channels.");
        WaveFormat source = Format(info), target = Target(source, declared);
        if (target == source) return wave.ToArray();
        // Decode to floating-point frames, mix channels, then resample with a windowed-sinc low-pass when the rate drops.
        int frames = info.DataLength / info.BlockAlign, channels = info.Channels, step = info.BitsPerSample / 8;
        var data = wave.Span.Slice(info.DataOffset, info.DataLength);
        float[][] input = new float[target.Channels][];
        for (int c = 0; c < target.Channels; c++) input[c] = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            if ((f & 65535) == 0) token.ThrowIfCancellationRequested();
            float mix = 0;
            for (int c = 0; c < channels; c++)
            {
                int p = f * info.BlockAlign + c * step;
                float value = step == 1 ? (data[p] - 128) / 128f : BinaryPrimitives.ReadInt16LittleEndian(data[p..]) / 32768f;
                if (target.Channels == channels) input[c][f] = value; else mix += value;
            }
            if (target.Channels != channels) for (int c = 0; c < target.Channels; c++) input[c][f] = mix / channels;
        }
        double ratio = (double)target.Rate / info.SampleRate;
        int outFrames = Math.Max(1, (int)Math.Round(frames * ratio));
        float[][] output = target.Rate == info.SampleRate ? input : input.Select(channel => Resample(channel, ratio, outFrames, token)).ToArray();
        int outStep = target.Bits / 8, align = outStep * target.Channels;
        byte[] pcm = new byte[checked(outFrames * align)];
        for (int f = 0; f < outFrames; f++)
            for (int c = 0; c < target.Channels; c++)
            {
                float value = Math.Clamp(output[c][f], -1f, 1f); int p = f * align + c * outStep;
                if (outStep == 1) pcm[p] = (byte)Math.Clamp((int)MathF.Round(value * 127f + 128f), 0, 255);
                else BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(p), (short)Math.Clamp((int)MathF.Round(value * 32767f), short.MinValue, short.MaxValue));
            }
        var cues = info.Cues.Select(c => (c.Id, Position: (uint)Math.Min(outFrames, Math.Round(c.SampleOffset * ratio)))).ToArray();
        return Write(target, pcm, cues);
    }

    /// <summary>The format a source is converted to: each of rate, sample size and channels is the lower of source and declaration.</summary>
    public static WaveFormat Target(WaveFormat source, WaveFormat declared) =>
        new(Math.Min(source.Rate, declared.Rate), Math.Min(source.Bits, declared.Bits), Math.Min(source.Channels, declared.Channels));

    private static float[] Resample(float[] input, double ratio, int count, CancellationToken token)
    {
        // Lanczos kernel; when downsampling the cutoff follows the new Nyquist frequency.
        const int taps = 16; double scale = Math.Min(1, ratio);
        float[] result = new float[count];
        for (int i = 0; i < count; i++)
        {
            if ((i & 65535) == 0) token.ThrowIfCancellationRequested();
            double center = i / ratio, sum = 0, weights = 0; int first = (int)Math.Floor(center - taps / scale), last = (int)Math.Ceiling(center + taps / scale);
            for (int j = Math.Max(0, first); j <= Math.Min(input.Length - 1, last); j++)
            {
                double x = (j - center) * scale, w = x == 0 ? 1 : Math.Abs(x) >= taps ? 0 : Sinc(x) * Sinc(x / taps);
                sum += input[j] * w; weights += w;
            }
            result[i] = weights == 0 ? 0 : (float)(sum / weights);
        }
        return result;
        static double Sinc(double x) => Math.Sin(Math.PI * x) / (Math.PI * x);
    }

    /// <summary>A canonical RIFF/WAVE: fmt, optional cue, then data, as in the retail banks.</summary>
    public static byte[] Write(WaveFormat format, ReadOnlySpan<byte> pcm, IReadOnlyList<(uint Id, uint Position)> cues)
    {
        int cueBytes = cues.Count == 0 ? 0 : 8 + 4 + cues.Count * 24, pad = pcm.Length & 1;
        using MemoryStream stream = new(); using BinaryWriter w = new(stream);
        w.Write("RIFF"u8); w.Write(4 + 24 + cueBytes + 8 + pcm.Length + pad); w.Write("WAVE"u8);
        int align = format.Bits / 8 * format.Channels;
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)format.Channels); w.Write(format.Rate); w.Write(format.Rate * align); w.Write((short)align); w.Write((short)format.Bits);
        if (cues.Count > 0)
        {
            w.Write("cue "u8); w.Write(4 + cues.Count * 24); w.Write(cues.Count);
            foreach (var (id, position) in cues) { w.Write(id); w.Write(position); w.Write("data"u8); w.Write(0); w.Write(0); w.Write(position); }
        }
        w.Write("data"u8); w.Write(pcm.Length); w.Write(pcm); if (pad != 0) w.Write((byte)0);
        return stream.ToArray();
    }
}
