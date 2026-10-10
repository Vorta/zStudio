using System.IO;
using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Recoil.Zbd.Desktop.Audio;

/// <summary>Immutable interleaved stereo PCM. All conversion happens before playback.</summary>
internal sealed class PreparedSound
{
    public const int SampleRate = 48000;
    public ReadOnlyMemory<float> Samples { get; }
    // Keep the decoder's capacity instead of making a second PCM array merely to trim its tail.
    public long RetainedBytes { get; }
    public PreparedSound(float[] samples) : this(samples, samples.Length) { }
    private PreparedSound(float[] samples, int count)
    { Samples = samples.AsMemory(0, count); RetainedBytes = (long)samples.Length * sizeof(float); }
    public int Frames => Samples.Length / 2;
    public double Duration => (double)Frames / SampleRate;

    public static PreparedSound Decode(ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var stream = Open(bytes);
        using var reader = new WaveFileReader(stream);
        int capacity = SampleCapacity(reader);
        ISampleProvider source = reader.ToSampleProvider();
        if (source.WaveFormat.Channels == 1) source = new MonoToStereoSampleProvider(source);
        if (source.WaveFormat.Channels != 2) throw new InvalidDataException("Only mono and stereo preview sounds are supported.");
        // A fixed output request can demand a huge input workspace at an extreme downsampling ratio.
        // Keep ordinary reads unchanged, and shrink high-rate reads towards 4,096 input frames.
        // NAudio 3.1's output-driven WDL resampler asks for floor(rate/48000 * frames) + 4 input
        // frames: at most 44,743 for a positive Int32 rate. Its EOF padding, simultaneous old/new
        // resize arrays and mono/64-bit PCM conversion buffers together stay below 4 MiB.
        int readSamples = 2 * (int)Math.Clamp(4096L * SampleRate / source.WaveFormat.SampleRate, 1, 4096);
        if (source.WaveFormat.SampleRate != SampleRate) source = new WdlResamplingSampleProvider(source, SampleRate);
        float[] buffer = new float[capacity];
        int count = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (count == buffer.Length) throw new InvalidDataException("Decoded sound exceeds its declared duration.");
            int read = source.Read(buffer.AsSpan(count, Math.Min(readSamples, buffer.Length - count)));
            if (read == 0) break;
            count += read;
        }
        if (count == 0 || count % 2 != 0) throw new InvalidDataException("Decoded sound has no complete stereo frames.");
        for (int i = 0; i < count; i++)
        {
            if ((i & 8191) == 0) token.ThrowIfCancellationRequested();
            if (!float.IsFinite(buffer[i])) throw new InvalidDataException("Sound contains non-finite samples.");
        }
        return new(buffer, count);
    }

    /// <summary>The complete retained stereo float buffer, including resampler tail room, before decoding any PCM.</summary>
    internal static long RequiredBytes(ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var stream = Open(bytes);
        using var reader = new WaveFileReader(stream);
        return (long)SampleCapacity(reader) * sizeof(float);
    }
    private static int SampleCapacity(WaveFileReader reader)
    {
        if (reader.WaveFormat.Channels is not (1 or 2)) throw new InvalidDataException("Only mono and stereo preview sounds are supported.");
        if (reader.WaveFormat.SampleRate <= 0) throw new InvalidDataException("The preview sound needs a positive sample rate.");
        double frames = Math.Ceiling(reader.TotalTime.TotalSeconds * SampleRate);
        // Bound expansion of malformed sample-rate/duration metadata before allocating PCM.
        if (!double.IsFinite(frames) || frames <= 0 || frames > 16 * 1024 * 1024)
            throw new InvalidDataException("Decoded sound is empty or exceeds the 128 MiB preview sample limit.");
        return checked(((int)frames + 1024) * 2);
    }
    private static MemoryStream Open(ReadOnlyMemory<byte> bytes) => MemoryMarshal.TryGetArray(bytes, out var segment)
        ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, false)
        : new MemoryStream(bytes.ToArray(), false);
}
