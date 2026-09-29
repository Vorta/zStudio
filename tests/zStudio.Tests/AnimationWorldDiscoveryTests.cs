using System.Buffers.Binary;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Formats;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class AnimationWorldDiscoveryTests
{
    [Theory]
    [InlineData(28, 15, 27)]
    [InlineData(39, 27, 15)]
    public async Task AutoDiscoverySkipsTheOtherGamesWorld(int animationVersion, int worldVersion, int otherVersion)
    {
        using var fixture = new Fixture(animationVersion, worldVersion, otherVersion);
        var context = await AnimationPreviewContext.LoadAsync(fixture.Package, fixture.AnimationPath, fixture.Resolver, token: TestContext.Current.CancellationToken);
        Assert.Same(fixture.MatchingWorld, context.World);
        Assert.Equal((uint)animationVersion, context.Package.Version);
    }

    [Theory]
    [InlineData(28, 15, 27)]
    [InlineData(39, 27, 15)]
    public async Task ExplicitSelectionAndMissingCompatibleWorldRetainTheirDiagnostics(int animationVersion, int worldVersion, int otherVersion)
    {
        using var fixture = new Fixture(animationVersion, worldVersion, otherVersion);
        var token = TestContext.Current.CancellationToken;
        var context = await AnimationPreviewContext.LoadAsync(fixture.Package, fixture.AnimationPath, fixture.Resolver, fixture.MatchingWorld.Path, token);
        Assert.Same(fixture.MatchingWorld, context.World);
        var incompatible = await Assert.ThrowsAsync<InvalidDataException>(() => AnimationPreviewContext.LoadAsync(fixture.Package, fixture.AnimationPath, fixture.Resolver, fixture.OtherWorld.Path, token));
        Assert.Contains("different games", incompatible.Message);
        File.Delete(fixture.MatchingWorld.Path);
        var missing = await Assert.ThrowsAsync<InvalidDataException>(() => AnimationPreviewContext.LoadAsync(fixture.Package, fixture.AnimationPath, fixture.Resolver, token: token));
        Assert.Contains("Select the matching mission GameZ file", missing.Message);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "zstudio-world-discovery-" + Guid.NewGuid().ToString("N"));
        public AssetResolver Resolver { get; }
        public AnimationPackage Package { get; }
        public ZbdDocument MatchingWorld { get; }
        public ZbdDocument OtherWorld { get; }
        public string AnimationPath => Path.Combine(directory, "animation.zbd");

        public Fixture(int animationVersion, int worldVersion, int otherVersion)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, "one.zbd"), []);
            File.WriteAllBytes(Path.Combine(directory, "two.zbd"), []);
            // Assign versions in actual enumeration order so the incompatible world always comes first.
            string[] paths = Directory.GetFiles(directory, "*.zbd");
            OtherWorld = World(paths[0], otherVersion);
            MatchingWorld = World(paths[1], worldVersion);
            Resolver = new(directory);
            Resolver.SetWorkspaceSnapshots(Guid.NewGuid(), [OtherWorld, MatchingWorld]);
            byte[] prefix = new byte[animationVersion == 39 ? 80 : 72];
            BinaryPrimitives.WriteUInt32LittleEndian(prefix, 0x08170616);
            BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(4), animationVersion);
            Package = new() { Prefix = prefix, Tail = [] };
        }

        private static ZbdDocument World(string path, int version)
        {
            byte[] bytes = new byte[36];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x02971222);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), version);
            File.WriteAllBytes(path, bytes);
            return new(path, FileStamp.Read(path), FormatRegistry.Probe(path), bytes) { Scene = new() };
        }

        public void Dispose() { Resolver.Dispose(); Directory.Delete(directory, true); }
    }
}
