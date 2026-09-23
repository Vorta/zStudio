using System.IO;
using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Recoil.Zbd.Desktop.Audio;

/// <summary>Immutable interleaved stereo PCM. All conversion happens before playback.</summary>
internal sealed class PreparedSound(float[] samples)
{
    public const int SampleRate = 48000;
    public ReadOnlyMemory<float> Samples { get; } = samples;
    public int Frames => Samples.Length / 2;
    public double Duration => (double)Frames / SampleRate;

    public static PreparedSound Decode(ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var stream = MemoryMarshal.TryGetArray(bytes, out var segment)
            ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, false)
            : new MemoryStream(bytes.ToArray(), false);
        using var reader = new WaveFileReader(stream);
        ISampleProvider source = reader.ToSampleProvider();
        if (source.WaveFormat.Channels == 1) source = new MonoToStereoSampleProvider(source);
        if (source.WaveFormat.Channels != 2) throw new InvalidDataException("Only mono and stereo preview sounds are supported.");
        if (source.WaveFormat.SampleRate != SampleRate) source = new WdlResamplingSampleProvider(source, SampleRate);
        double frames = Math.Ceiling(reader.TotalTime.TotalSeconds * SampleRate);
        // Bound expansion of malformed sample-rate/duration metadata before allocating PCM.
        if (!double.IsFinite(frames) || frames <= 0 || frames > 16 * 1024 * 1024)
            throw new InvalidDataException("Decoded sound is empty or exceeds the 128 MiB preview sample limit.");
        float[] buffer = new float[checked(((int)frames + 1024) * 2)];
        int count = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (count == buffer.Length) throw new InvalidDataException("Decoded sound exceeds its declared duration.");
            int read = source.Read(buffer.AsSpan(count, Math.Min(8192, buffer.Length - count)));
            if (read == 0) break;
            count += read;
        }
        if (count == 0 || count % 2 != 0) throw new InvalidDataException("Decoded sound has no complete stereo frames.");
        for (int i = 0; i < count; i++) if (!float.IsFinite(buffer[i])) throw new InvalidDataException("Sound contains non-finite samples.");
        Array.Resize(ref buffer, count);
        return new(buffer);
    }
}
