using System.Buffers.Binary;
using System.Numerics;
using NAudio.Wave;
using zStudio.Core;
using zStudio.Core.Animation;
using zStudio.Desktop.Audio;
using Xunit;

namespace zStudio.Desktop.Tests;

public sealed class WorldAnimationAudioTests
{
    [Fact]
    public async Task FinitePolicyExcludesChildDecodeAndDeviceWaitsForForwardPlay()
    {
        var token = TestContext.Current.CancellationToken;
        AnimationPackage package = new() { Prefix = [], Tail = [] };
        AnimationEntry root = new(new byte[308], 0, 0), child = new(new byte[308], 1, 0);
        root.SetText(0, "root"); child.SetText(0, "child");
        root.Primary.Events.Add(Sound("root-tone")); child.Primary.Events.Add(Sound("child-tone"));
        var call = AnimationCatalog.Create(24); call.SetText(12, "child"); root.Primary.Events.Add(call);
        package.Entries.AddRange([root, child]);
        var world = new AssetDocument("fixture", new(0, DateTime.MinValue),
            new(FormatFamily.GameZ, 15, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Scene = new() };
        AnimationPreviewContext context = new() { Package = package, World = world };
        context.Sounds.Add("root-tone", new("root-tone", "root.wav", false, Wave()));
        context.Sounds.Add("child-tone", new("child-tone", "child.wav", false, Wave()));
        int decoded = 0, created = 0;
        Output output = new();
        await using AnimationAudio audio = new(() => { created++; return output; },
            (_, _) => { decoded++; return new PreparedSound(Enumerable.Repeat(.25f, 96000).ToArray()); });

        await audio.PrepareAsync(context, 0, AnimationExecutionPolicy.RootOnly, false, token);
        Assert.Equal(1, decoded); Assert.Equal(1, audio.PreparedSoundCount);
        Assert.Equal(0, created); Assert.True(audio.IsPrepared);
        var cue = new AnimationSoundCue(1, "root-tone", false, false, 0, Vector3.Zero);
        var frame = new AnimationFrame(.25, [], [], [cue], [cue], null, null, default, default, [], [], []);
        audio.Update(frame, false, true);
        Assert.Equal(0, created); Assert.Equal(0, audio.VoiceCount);

        var included = new AnimationExecutionPolicy([AnimationExecutionPolicy.ChildPath("", 0, call.Id, 1)]);
        await audio.PrepareAsync(context, 0, included, false, token);
        Assert.Equal(2, decoded); Assert.Equal(2, audio.PreparedSoundCount);
        Assert.Equal(0, created);
        await audio.StartOutputAsync(token);
        audio.Update(frame, true);
        Assert.Equal(1, created); Assert.Equal(1, output.Starts); Assert.Equal(1, audio.VoiceCount);
        audio.Stop(); audio.Update(frame with { Time = .5 }, true);
        Assert.Equal(1, created); Assert.Equal(1, audio.VoiceCount);
    }

    private static AnimationEvent Sound(string name)
    { var result = AnimationCatalog.Create(2); result.SetText(12, name); result.SetInt(52, 1); return result; }

    private static byte[] Wave()
    {
        byte[] bytes = new byte[44 + 96000];
        "RIFF"u8.CopyTo(bytes); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length - 8);
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8)); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(20), 1); BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), 48000); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), 96000);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(32), 2); BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(34), 16);
        "data"u8.CopyTo(bytes.AsSpan(36)); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), 96000);
        return bytes;
    }

    private sealed class Output : IAnimationAudioOutput
    {
        public int Starts;
        public event Action<string>? Failed { add { } remove { } }
        public void Start(ISampleProvider source) => Starts++;
        public void Dispose() { }
    }
}
