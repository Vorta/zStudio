using System.Collections.Concurrent;
using NAudio.Wave;

namespace Recoil.Zbd.Desktop.Audio;

/// <summary>Only the audio callback owns voice cursors. UI operations enqueue small commands, never wait for it.</summary>
internal sealed class AnimationMixer : ISampleProvider
{
    public const int MaxVoices = 32;
    private readonly ConcurrentQueue<Command> commands = new();
    private readonly ConcurrentQueue<(int Generation, long Id)> ended = new();
    private readonly Voice?[] voices = new Voice[MaxVoices];
    private int generation, renderGeneration = -1;
    private float volume = .5f;
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(PreparedSound.SampleRate, 2);
    public float Volume { get => Volatile.Read(ref volume); set => Volatile.Write(ref volume, Math.Clamp(value, 0, 1)); }
    public int Generation => Volatile.Read(ref generation);
    public void Reset() => Interlocked.Increment(ref generation);
    public void Start(long id, PreparedSound sound, int offsetFrames, bool loop, float gain = 1) =>
        commands.Enqueue(new(Generation, id, new(id, sound, offsetFrames * 2, loop, float.IsFinite(gain) ? Math.Clamp(gain, 0, 1) : 0)));
    public void Stop(long id) => commands.Enqueue(new(Generation, id, null));
    public bool TryTakeEnded(out (int Generation, long Id) completion) => ended.TryDequeue(out completion);

    public int Read(Span<float> buffer)
    {
        buffer.Clear();
        int current = SynchronizeGeneration();
        while (commands.TryDequeue(out var command))
        {
            // Reset can race this read: a command enqueued after Reset belongs to the NEW generation.
            // Re-read after dequeuing so it is not discarded against a stale callback-local generation.
            current = SynchronizeGeneration();
            if (command.Generation != current) continue;
            int slot = -1;
            for (int i = 0; i < voices.Length; i++) if (voices[i]?.Id == command.Id) { slot = i; break; }
            if (slot < 0 && command.Voice != null) slot = Array.IndexOf(voices, null);
            if (slot >= 0) voices[slot] = command.Voice;
        }
        int length = buffer.Length - buffer.Length % 2;
        for (int slot = 0; slot < voices.Length; slot++)
        {
            if (voices[slot] is not { } voice) continue;
            var samples = voice.Sound.Samples.Span;
            int written = 0;
            while (written < length)
            {
                if (voice.Cursor >= samples.Length)
                {
                    if (voice.Loop) voice.Cursor = 0;
                    else break;
                }
                int count = Math.Min(length - written, samples.Length - voice.Cursor);
                for (int i = 0; i < count; i++) buffer[written + i] += samples[voice.Cursor + i] * voice.Gain;
                voice.Cursor += count; written += count;
            }
            if (!voice.Loop && voice.Cursor >= samples.Length)
            { voices[slot] = null; ended.Enqueue((current, voice.Id)); }
        }
        float gain = Volume;
        for (int i = 0; i < length; i++) buffer[i] = Math.Clamp(buffer[i] * gain, -1, 1);
        if (current != Generation) buffer.Clear();
        return buffer.Length; // Silence keeps the shared output primed between cues.
    }
    private int SynchronizeGeneration()
    {
        int current = Generation;
        if (renderGeneration != current) { Array.Clear(voices); renderGeneration = current; }
        return current;
    }
    private sealed record Command(int Generation, long Id, Voice? Voice);
    private sealed class Voice(long id, PreparedSound sound, int cursor, bool loop, float gain)
    {
        public long Id { get; } = id;
        public PreparedSound Sound { get; } = sound;
        public bool Loop { get; } = loop;
        public float Gain { get; } = gain;
        public int Cursor = cursor;
    }
}
