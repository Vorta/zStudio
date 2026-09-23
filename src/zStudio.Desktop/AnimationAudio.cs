using System.Collections.Concurrent;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Desktop.Audio;

namespace Recoil.Zbd.Desktop;

/// <summary>Prepares audio off-thread; presentation updates only enqueue voices in a warm mixer.</summary>
public sealed class AnimationAudio : IDisposable, IAsyncDisposable
{
    private readonly AnimationMixer mixer = new();
    private readonly Dictionary<long, bool> voices = [];
    private readonly HashSet<long> completed = [];
    private readonly ConcurrentQueue<(int Output, string Message)> errors = new();
    private readonly SemaphoreSlim preparationGate = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Func<IAnimationAudioOutput> createOutput;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, PreparedSound> decode;
    // This cache and the output are owned by the preparation/disposal worker under preparationGate.
    private Dictionary<ReadOnlyMemory<byte>, PreparedSound> cache = [];
    private IAnimationAudioOutput? output;
    private Dictionary<string, Binding> bank = new(StringComparer.Ordinal);
    private int preparationGeneration, outputGeneration;
    private bool outputFaulted, disposed;
    private Task? disposal;
    public float Volume { get => mixer.Volume; set => mixer.Volume = value; }
    public bool Muted { get; set; }
    public bool IsPrepared { get; private set; }
    public event Action<string>? Diagnostic;
    public int VoiceCount => voices.Count;
    internal int OutputInitializations { get; private set; }
    internal int PreparedSoundCount => bank.Values.Select(b => b.Sound).Distinct().Count();

    public AnimationAudio() : this(() => new AnimationAudioOutput(), PreparedSound.Decode) { }
    internal AnimationAudio(Func<IAnimationAudioOutput> createOutput,
        Func<ReadOnlyMemory<byte>, CancellationToken, PreparedSound>? decode = null)
    { this.createOutput = createOutput; this.decode = decode ?? PreparedSound.Decode; }

    /// <param name="snapshot">A caller-owned frozen context; never the mutable edit package.</param>
    public async Task PrepareAsync(AnimationPreviewContext snapshot, int entryIndex, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Stop(); IsPrepared = false;
        int generation = ++preparationGeneration;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        var result = await Task.Run(async () =>
        {
            await preparationGate.WaitAsync(linked.Token).ConfigureAwait(false);
            try { return Prepare(snapshot, entryIndex, linked.Token); }
            finally { preparationGate.Release(); }
        }, linked.Token);
        linked.Token.ThrowIfCancellationRequested();
        if (disposed || generation != preparationGeneration) return;
        bank = result.Bank; IsPrepared = result.Error == null;
        foreach (string message in result.Diagnostics) Diagnostic?.Invoke(message);
        if (result.Error != null) Fail(result.Error);
    }

    private Preparation Prepare(AnimationPreviewContext snapshot, int entryIndex, CancellationToken token)
    {
        var dependencies = AnimationAudioDependencies.Collect(snapshot.Package, entryIndex, token);
        List<string> diagnostics = [.. dependencies.Diagnostics];
        Dictionary<string, Binding> next = new(StringComparer.Ordinal);
        Dictionary<ReadOnlyMemory<byte>, PreparedSound> retained = [];
        HashSet<ReadOnlyMemory<byte>> unavailable = [];
        foreach (string name in dependencies.Names.Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            if (!snapshot.Sounds.TryGetValue(name, out var sound))
            { diagnostics.Add($"Audio preparation: unresolved sound '{name}'."); continue; }
            if (unavailable.Contains(sound.Bytes)) continue;
            try
            {
                if (!retained.TryGetValue(sound.Bytes, out var sample))
                {
                    if (!cache.TryGetValue(sound.Bytes, out sample)) sample = decode(sound.Bytes, token);
                    retained.Add(sound.Bytes, sample);
                }
                next.Add(name, new(sample, sound.Loop));
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
            { unavailable.Add(sound.Bytes); diagnostics.Add($"Audio preparation: '{name}' ({sound.FileName}) is unavailable: {ex.Message}"); }
        }
        token.ThrowIfCancellationRequested();
        cache = retained;
        string? error = null;
        try
        {
            if (Volatile.Read(ref outputFaulted)) RetireOutput();
            if (next.Count > 0 && output == null)
            {
                int current = Interlocked.Increment(ref outputGeneration);
                output = createOutput();
                output.Failed += message =>
                {
                    if (current != Volatile.Read(ref outputGeneration)) return;
                    errors.Enqueue((current, message));
                };
                output.Start(mixer); OutputInitializations++;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        { error = ex.Message; RetireOutput(); }
        token.ThrowIfCancellationRequested();
        return new(next, diagnostics, error);
    }

    public void Update(AnimationFrame frame, AnimationPreviewContext context, bool playing, bool seek = false)
    {
        if (disposed) return;
        while (IsPrepared && errors.TryDequeue(out var error))
            if (error.Output == Volatile.Read(ref outputGeneration))
            { Volatile.Write(ref outputFaulted, true); Fail(error.Message); return; }
        if (!playing || Muted) { if (voices.Count != 0 || completed.Count != 0) Stop(); return; }
        if (!IsPrepared) return;
        if (seek) Stop();
        while (mixer.TryTakeEnded(out var ended))
            if (ended.Generation == mixer.Generation) { voices.Remove(ended.Id); completed.Add(ended.Id); }
        var active = frame.ActiveSounds.Select(s => s.Id).ToHashSet();
        completed.RemoveWhere(id => !active.Contains(id));
        foreach (var (id, persistent) in voices.ToArray()) if (persistent && !active.Contains(id)) Remove(id);
        foreach (var cue in frame.Sounds.Where(c => c.Stop)) Remove(cue.Id);
        foreach (var cue in frame.Sounds.Concat(frame.ActiveSounds).Where(c => !c.Stop).DistinctBy(c => c.Id))
        {
            if (voices.ContainsKey(cue.Id) || completed.Contains(cue.Id) || cue.Persistent && !active.Contains(cue.Id)
                || !bank.TryGetValue(cue.Name, out var binding) || voices.Count >= AnimationMixer.MaxVoices) continue;
            double elapsed = Math.Max(0, frame.Time - cue.StartedAt), duration = binding.Sound.Duration;
            bool loop = cue.Persistent && binding.Loop;
            if (!double.IsFinite(elapsed) || duration <= 0 || elapsed >= duration && !loop) continue;
            int offset = Math.Min(binding.Sound.Frames - 1, (int)((loop ? elapsed % duration : elapsed) * PreparedSound.SampleRate));
            mixer.Start(cue.Id, binding.Sound, offset, loop, cue.Gain);
            voices.Add(cue.Id, cue.Persistent);
        }
    }
    private void Remove(long id) { if (voices.Remove(id)) mixer.Stop(id); }
    private void Fail(string message)
    {
        IsPrepared = false; Muted = true; Stop();
        Diagnostic?.Invoke("Audio preview stopped: " + message + ". Uncheck Mute to retry.");
    }
    public void Stop() { mixer.Reset(); voices.Clear(); completed.Clear(); }
    // Synchronous control disposal only invalidates commands. Native teardown cannot stall asset switching.
    public void Dispose() { _ = ShutdownAsync(); GC.SuppressFinalize(this); }
    public ValueTask DisposeAsync() { GC.SuppressFinalize(this); return new(ShutdownAsync()); }
    private Task ShutdownAsync()
    {
        if (disposal != null) return disposal;
        disposed = true; IsPrepared = false; ++preparationGeneration; Stop(); bank.Clear(); lifetime.Cancel();
        disposal = Task.Run(async () =>
        {
            await preparationGate.WaitAsync().ConfigureAwait(false);
            try { RetireOutput(); cache.Clear(); }
            finally { preparationGate.Release(); lifetime.Dispose(); }
        });
        return disposal;
    }
    private void RetireOutput()
    {
        Interlocked.Increment(ref outputGeneration);
        var previous = output; output = null; Volatile.Write(ref outputFaulted, false);
        previous?.Dispose();
    }
    private sealed record Binding(PreparedSound Sound, bool Loop);
    private sealed record Preparation(Dictionary<string, Binding> Bank, List<string> Diagnostics, string? Error);
}
