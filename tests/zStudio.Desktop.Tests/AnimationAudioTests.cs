using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using NAudio.Wave;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Desktop.Audio;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

public sealed class AnimationAudioTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ImpactGainIsAppliedPerVoiceAndSurvivesPauseAndMute()
    {
        var context = Context(); FakeOutput output = new();
        await using AnimationAudio audio = new(() => output, (_, _) => new(Enumerable.Repeat(.5f, 96000).ToArray())) { Volume = 1 };
        await audio.PrepareAsync(context, 0, Token);
        var cue = Cue(1) with { Gain = .2f };
        audio.Update(Frame(.1, cue), context, true); Assert.InRange(output.Read(2)[0], .0999f, .1001f);
        audio.Update(Frame(.2, cue), context, false); Assert.Equal(0, output.Read(2)[0]);
        audio.Update(Frame(.3, cue), context, true); Assert.InRange(output.Read(2)[0], .0999f, .1001f);
        audio.Muted = true; audio.Update(Frame(.4, cue), context, true); Assert.Equal(0, output.Read(2)[0]);
        audio.Muted = false; audio.Update(Frame(.5, cue), context, true); Assert.InRange(output.Read(2)[0], .0999f, .1001f);
        Assert.Equal(1, output.Starts);
    }

    [Theory]
    [InlineData(8, 22050, 1)]
    [InlineData(16, 48000, 2)]
    [InlineData(24, 44100, 2)]
    [InlineData(32, 11025, 1)]
    public void PredecodeNormalizesPcmWithoutChangingWaveBytes(int bits, int rate, int channels)
    {
        byte[] wav = Wave(bits, rate, channels), before = (byte[])wav.Clone();
        var sound = PreparedSound.Decode(wav, Token);
        Assert.InRange(sound.Frames, 47990, 48010);
        Assert.InRange(sound.Samples.Span[4800], .249f, .251f);
        Assert.Equal(sound.Samples.Span[4800], sound.Samples.Span[4801]);
        Assert.Equal(before, wav);
    }

    [Fact]
    public void InvalidAndCanceledDecodeDoesNotCreatePlaybackResources()
    {
        Assert.ThrowsAny<Exception>(() => PreparedSound.Decode(new byte[44], Token));
        Assert.Throws<OperationCanceledException>(() => PreparedSound.Decode(Wave(), new CancellationToken(true)));
        byte[] wav = Wave(); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), 4);
        Assert.Throws<InvalidDataException>(() => PreparedSound.Decode(wav, Token));
    }

    [Fact]
    public void MixerWrapsLoopsAndUsesIndependentCursorsForOverlaps()
    {
        AnimationMixer mixer = new() { Volume = 1 };
        PreparedSound sound = new([.1f, .1f, .2f, .2f, .3f, .3f]);
        mixer.Start(1, sound, 2, true); mixer.Start(2, sound, 0, false);
        float[] buffer = new float[10]; mixer.Read(buffer);
        Assert.Equal(new[] { .4f, .4f, .3f, .3f, .5f, .5f, .3f, .3f, .1f, .1f }, buffer, new FloatComparer());
        Assert.True(mixer.TryTakeEnded(out var ended)); Assert.Equal(2, ended.Id);
        mixer.Stop(1); mixer.Read(buffer); Assert.All(buffer, v => Assert.Equal(0, v));
    }

    [Fact]
    public void MixerDiscardsQueuedVoicesFromOldSeekAndReusesVoiceIds()
    {
        AnimationMixer mixer = new() { Volume = 1 };
        PreparedSound sound = new([.2f, .2f]);
        mixer.Start(1, sound, 0, true); mixer.Reset();
        float[] buffer = new float[8]; mixer.Read(buffer); Assert.All(buffer, v => Assert.Equal(0, v));
        mixer.Start(1, sound, 0, true); mixer.Read(buffer); Assert.All(buffer, v => Assert.Equal(.2f, v));
        mixer.Reset(); mixer.Read(buffer); Assert.All(buffer, v => Assert.Equal(0, v));
    }

    [Fact]
    public async Task PreparationCachesAliasesAndVoicesNeverReinitializeOrDecode()
    {
        var context = Context(); context.Sounds["alias"] = context.Sounds["tone"] with { Name = "alias" };
        context.Package.Entries[0].Primary.Events.Add(SoundEvent("alias"));
        int decoded = 0; FakeOutput output = new();
        await using AnimationAudio audio = new(() => output, (bytes, token) => { decoded++; return PreparedSound.Decode(bytes, token); });
        await audio.PrepareAsync(context, 0, Token);
        Assert.True(audio.IsPrepared); Assert.Equal(1, decoded); Assert.Equal(1, audio.PreparedSoundCount);
        for (int i = 0; i < 100; i++)
        {
            audio.Update(Frame(0, Cue(i + 1)), context, true); output.Read(20); audio.Stop();
        }
        await audio.PrepareAsync(context, 0, Token);
        Assert.Equal(1, decoded); Assert.Equal(1, output.Starts); Assert.Equal(0, output.Disposals);
        Assert.Equal(1, audio.OutputInitializations);
    }

    [Fact]
    public async Task ConcurrentSeekAndAudioReadsKeepTheFinalGenerationAudible()
    {
        AnimationMixer mixer = new() { Volume = 1 }; PreparedSound sound = new([.2f, .2f]);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Token);
        int reads = 0, afterReset = 0;
        var render = Task.Run(() =>
        {
            float[] buffer = new float[128];
            while (!stop.IsCancellationRequested)
            {
                mixer.Read(buffer); Interlocked.Increment(ref reads);
                if (Volatile.Read(ref afterReset) == 1 && buffer[0] != 0) return true;
            }
            return false;
        }, Token);
        try
        {
            for (int i = 0; i < 10000; i++) { mixer.Reset(); mixer.Start(1, sound, 0, true); }
            Volatile.Write(ref afterReset, 1);
            Assert.True(await render.WaitAsync(TimeSpan.FromSeconds(10), Token));
            Assert.True(reads > 0);
        }
        finally { stop.Cancel(); await render; }
    }

    [Fact]
    public async Task CompletedOneShotsDoNotRestartUntilSeekAndMuteResumeAtCurrentOffset()
    {
        var context = Context(); FakeOutput output = new();
        float[] ramp = Enumerable.Range(0, 48000).SelectMany(i => new[] { i / 48000f, i / 48000f }).ToArray();
        await using AnimationAudio audio = new(() => output, (_, _) => new(ramp)) { Volume = 1 };
        await audio.PrepareAsync(context, 0, Token);
        var cue = Cue(10);
        audio.Update(Frame(.25, cue), context, true);
        Assert.InRange(output.Read(2)[0], .2499f, .2501f);
        audio.Stop(); audio.Update(Frame(.5, cue), context, true);
        Assert.InRange(output.Read(2)[0], .4999f, .5001f);
        audio.Muted = true; audio.Update(Frame(.5, cue), context, true);
        Assert.All(output.Read(10), v => Assert.Equal(0, v));
        audio.Muted = false; audio.Update(Frame(.75, cue), context, true);
        Assert.InRange(output.Read(2)[0], .7499f, .7501f);
        output.Read(48000); // Hardware finishes while slowed simulation still reports an active one-shot.
        audio.Update(Frame(.8, cue), context, true);
        Assert.Equal(0, audio.VoiceCount); Assert.All(output.Read(10), v => Assert.Equal(0, v));
        audio.Update(Frame(.1, cue), context, true, seek: true);
        Assert.InRange(output.Read(2)[0], .0999f, .1001f);
        audio.Update(Frame(.1, cue), context, false);
        Assert.All(output.Read(10), v => Assert.Equal(0, v));
    }

    [Fact]
    public async Task VoiceLimitAndPersistentStopLeaveOutputRunning()
    {
        var context = Context(loop: true); FakeOutput output = new();
        await using AnimationAudio audio = new(() => output);
        await audio.PrepareAsync(context, 0, Token);
        var cues = Enumerable.Range(1, 40).Select(i => Cue(i) with { Persistent = true }).ToArray();
        audio.Update(Frame(0, cues), context, true); Assert.Equal(32, audio.VoiceCount);
        Assert.Contains(output.Read(20), v => v != 0);
        audio.Update(Frame(.1), context, true);
        Assert.Equal(0, audio.VoiceCount); Assert.All(output.Read(20), v => Assert.Equal(0, v));
        Assert.Equal(1, output.Starts); Assert.Equal(0, output.Disposals);
    }

    [Fact]
    public async Task SlowDeviceStartupAndTeardownCannotBlockTheCaller()
    {
        using ManualResetEventSlim startRelease = new(), disposeRelease = new();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeOutput output = new()
        {
            Starting = () => { started.SetResult(); Assert.True(startRelease.Wait(TimeSpan.FromSeconds(10), Token)); },
            Disposing = () => { disposing.SetResult(); Assert.True(disposeRelease.Wait(TimeSpan.FromSeconds(10), Token)); }
        };
        AnimationAudio audio = new(() => output);
        try
        {
            var context = Context(); Task preparation = audio.PrepareAsync(context, 0, Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.False(preparation.IsCompleted);
            audio.Update(Frame(0, Cue(1)), context, true); audio.Stop(); Assert.Equal(0, audio.VoiceCount);
            startRelease.Set(); await preparation;
            audio.Dispose();
            await disposing.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.False(audio.DisposeAsync().IsCompleted);
        }
        finally { startRelease.Set(); disposeRelease.Set(); await audio.DisposeAsync(); }
        Assert.Equal(1, output.Disposals);
    }

    [Fact]
    public async Task CanceledPreparationCannotPublishOrStartStaleSounds()
    {
        using ManualResetEventSlim release = new();
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeOutput output = new() { Starting = () => { started.SetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(10), Token)); } };
        await using AnimationAudio audio = new(() => output);
        var context = Context(); Task preparation = audio.PrepareAsync(context, 0, canceled.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        canceled.Cancel(); release.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparation);
        Assert.False(audio.IsPrepared); audio.Update(Frame(0, Cue(1)), context, true);
        Assert.All(output.Read(20), v => Assert.Equal(0, v));
        await audio.PrepareAsync(context, 0, Token);
        audio.Update(Frame(0, Cue(2)), context, true); Assert.Contains(output.Read(20), v => v != 0);
        Assert.Equal(1, output.Starts);
    }

    [Fact]
    public async Task DeviceFailureMutesAndRetryIgnoresObsoleteOutputCallbacks()
    {
        var context = Context(); List<FakeOutput> outputs = [];
        await using AnimationAudio audio = new(() => { var output = new FakeOutput(); outputs.Add(output); return output; });
        List<string> diagnostics = []; audio.Diagnostic += diagnostics.Add;
        await audio.PrepareAsync(context, 0, Token);
        outputs[0].Fail("test device removed"); audio.Update(Frame(0), context, true);
        Assert.True(audio.Muted); Assert.False(audio.IsPrepared); Assert.Single(diagnostics);
        audio.Muted = false; await audio.PrepareAsync(context, 0, Token);
        Assert.Equal(2, outputs.Count); Assert.Equal(1, outputs[0].Disposals);
        outputs[0].Fail("late old callback"); audio.Update(Frame(0, Cue(1)), context, true);
        Assert.False(audio.Muted); Assert.Equal(1, audio.VoiceCount);
        Assert.Contains(outputs[1].Read(20), v => v != 0);
    }

    [Fact]
    public async Task OneBadSampleDoesNotDisableValidPreparedSounds()
    {
        var context = Context(); context.Sounds["bad"] = new("bad", "bad.wav", false, Wave(16, 48000, 3));
        context.Package.Entries[0].Primary.Events.Add(SoundEvent("bad"));
        FakeOutput output = new(); await using AnimationAudio audio = new(() => output);
        List<string> diagnostics = []; audio.Diagnostic += diagnostics.Add;
        await audio.PrepareAsync(context, 0, Token);
        Assert.True(audio.IsPrepared); Assert.False(audio.Muted); Assert.Single(diagnostics);
        audio.Update(Frame(0, Cue(1)), context, true); Assert.Contains(output.Read(20), v => v != 0);
    }

    [Fact]
    public async Task EditedSoundReferencesReprepareWithoutReopeningTheOutput()
    {
        var context = Context(); FakeOutput output = new(); int decoded = 0;
        await using AnimationAudio audio = new(() => output, (bytes, token) => { decoded++; return PreparedSound.Decode(bytes, token); });
        await audio.PrepareAsync(context.Snapshot(), 0, Token);
        var ev = context.Package.Entries[0].Primary.Events[0]; ev.SetText(12, "replacement");
        context.Sounds["replacement"] = new("replacement", "replacement.wav", false, Wave(8, 22050, 1));
        await audio.PrepareAsync(context.Snapshot(), 0, Token);
        audio.Update(Frame(0, Cue(1)), context, true); Assert.Equal(0, audio.VoiceCount);
        audio.Update(Frame(0, Cue(2) with { Name = "replacement" }), context, true);
        Assert.Equal(1, audio.VoiceCount); Assert.Contains(output.Read(20), v => v != 0);
        Assert.Equal(2, decoded); Assert.Equal(1, output.Starts); Assert.Equal(1, audio.PreparedSoundCount);
    }

    [Fact]
    public async Task InitializationFailureRemainsSilentAndCanRetry()
    {
        FakeOutput failed = new() { Starting = () => throw new IOException("No endpoint") }, working = new();
        int attempts = 0; var context = Context();
        await using AnimationAudio audio = new(() => ++attempts == 1 ? failed : working);
        List<string> diagnostics = []; audio.Diagnostic += diagnostics.Add;
        await audio.PrepareAsync(context, 0, Token);
        Assert.True(audio.Muted); Assert.False(audio.IsPrepared); Assert.Equal(1, failed.Disposals);
        Assert.Contains("No endpoint", Assert.Single(diagnostics));
        audio.Update(Frame(0, Cue(1)), context, true); Assert.Equal(0, audio.VoiceCount);
        audio.Muted = false; await audio.PrepareAsync(context, 0, Token);
        audio.Update(Frame(0, Cue(2)), context, true);
        Assert.True(audio.IsPrepared); Assert.False(audio.Muted); Assert.Contains(working.Read(20), v => v != 0);
    }

    [Fact]
    public async Task DisposingDuringPreparationCancelsPublishingAndReleasesTheDevice()
    {
        using ManualResetEventSlim release = new();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeOutput output = new() { Starting = () => { started.SetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(10), Token)); } };
        AnimationAudio audio = new(() => output);
        try
        {
            var preparation = audio.PrepareAsync(Context(), 0, Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            audio.Dispose(); release.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparation);
            await audio.DisposeAsync(); Assert.Equal(1, output.Disposals); Assert.False(audio.IsPrepared);
            Assert.Equal(0, audio.VoiceCount);
        }
        finally { release.Set(); await audio.DisposeAsync(); }
    }

    private static AnimationPreviewContext Context(bool loop = false)
    {
        var package = new AnimationPackage { Prefix = [], Tail = [] };
        var entry = new AnimationEntry(new byte[308], 0, 0); entry.SetText(0, "test"); entry.Primary.Events.Add(SoundEvent("tone")); package.Entries.Add(entry);
        var world = new ZbdDocument("fixture", new(0, DateTime.MinValue), new(FormatFamily.GameZ, 15, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Scene = new() };
        AnimationPreviewContext context = new() { Package = package, World = world };
        context.Sounds["tone"] = new("tone", "tone.wav", loop, Wave()); return context;
    }
    private static AnimationEvent SoundEvent(string name)
    { var ev = AnimationCatalog.Create(2); ev.SetText(12, name); ev.SetInt(52, 1); return ev; }
    private static AnimationSoundCue Cue(long id) => new(id, "tone", false, false, 0, Vector3.Zero);
    private static AnimationFrame Frame(double time, params AnimationSoundCue[] cues) => new(time, [], [], cues, cues, null, null, default, default, [], [], []);

    private static byte[] Wave(int bits = 16, int rate = 48000, int channels = 2)
    {
        byte[] bytes = new byte[44 + rate * channels * bits / 8];
        "RIFF"u8.CopyTo(bytes); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length - 8);
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8)); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(20), 1); BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(22), (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), rate); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), rate * channels * bits / 8);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(32), (short)(channels * bits / 8)); BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(34), (short)bits);
        "data"u8.CopyTo(bytes.AsSpan(36)); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), bytes.Length - 44);
        for (int i = 44; i < bytes.Length; i += bits / 8)
            if (bits == 8) bytes[i] = 160;
            else bytes[i + bits / 8 - 1] = 32;
        return bytes;
    }
    private sealed class FakeOutput : IAnimationAudioOutput
    {
        private ISampleProvider? source;
        public int Starts, Disposals;
        public Action? Starting, Disposing;
        public event Action<string>? Failed;
        public void Start(ISampleProvider input) { Starts++; Starting?.Invoke(); source = input; }
        public float[] Read(int count) { float[] buffer = new float[count]; source!.Read(buffer); return buffer; }
        public void Fail(string error) => Failed?.Invoke(error);
        public void Dispose() { Disposals++; Disposing?.Invoke(); }
    }
    private sealed class FloatComparer : IEqualityComparer<float>
    { public bool Equals(float x, float y) => Math.Abs(x - y) < .00001f; public int GetHashCode(float obj) => 0; }
}
