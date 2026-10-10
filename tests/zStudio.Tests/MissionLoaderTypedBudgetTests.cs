using System.IO;
using System.Numerics;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Xunit;

namespace Recoil.Zbd.Tests;

[Collection("Allocation-sensitive")]
public sealed class MissionLoaderTypedBudgetTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static ZrdNode A(params ZrdNode[] children) => new(Guid.NewGuid(), ZrdKind.Array, 0, "", children);
    private static ZrdNode S(string text) => new(Guid.NewGuid(), ZrdKind.String, 0, text, []);
    private static ZrdNode I(int value) => new(Guid.NewGuid(), ZrdKind.Int, unchecked((uint)value), "", []);
    private static ZrdNode F(float value) => new(Guid.NewGuid(), ZrdKind.Float, BitConverter.SingleToUInt32Bits(value), "", []);
    private static byte[] Spawn(string name = "tank_01") => ZrdWriter.Write(A(S(name), A(I(0), A(F(1), F(2), F(3)), F(0))), Token);
    private static byte[] Vehicles() => ZrdWriter.Write(A(S("tank"), A()), Token);

    [Theory]
    [InlineData(MissionDifficulty.Easy, "aiv_easy.zrd")]
    [InlineData(MissionDifficulty.Hard, "aiv_hard.zrd")]
    public async Task ARefusedSelectedLayoutDoesNotFallBackToAReadableDefault(MissionDifficulty difficulty, string selected)
    {
        using Fixture fixture = new(ResourceEditingTests.Archive(("aiv.zrd", Spawn()), ("vehicle.zrd", Vehicles())));
        var normal = fixture.Open(ArchiveZrdBudget.MaximumAllocation);
        fixture.Publish(normal);
        Assert.Single((await MissionSceneLoader.LoadAsync(fixture.World, fixture.Resolver, token: Token)).Actors);

        byte[] bytes = ResourceEditingTests.Archive((selected, Spawn()));
        string path = Path.Combine(Path.GetDirectoryName(fixture.ArchivePath)!, "difficulty.zbd");
        File.WriteAllBytes(path, bytes);
        ZbdDocument limited = new(path, new(bytes.Length, DateTime.MinValue), new(FormatFamily.Archive, 1, Recognition.Supported, "fixture"), bytes);
        new ArchiveReader(0).Read(limited, Token);
        fixture.Publish(normal, limited);
        var result = await MissionSceneLoader.LoadAsync(fixture.World, fixture.Resolver, token: Token, difficulty: difficulty);
        Assert.Empty(result.Actors);
        Assert.Equal(selected, result.Layout.AivResource);
        Assert.Contains(result.Diagnostics, note => note.Contains(selected) && note.Contains("shared typed-decoding budget"));
        Assert.DoesNotContain(result.Diagnostics, note => note.Contains(selected + " is unavailable; using aiv.zrd"));
        Assert.Null(Assert.Single(limited.Assets).Content);
    }

    [Fact]
    public async Task ActualMissionLoadingHonorsTypedRefusalAndRecoversOnSnapshotReplacement()
    {
        using Fixture fixture = new(ResourceEditingTests.Archive(("aiv.zrd", Spawn()), ("vehicle.zrd", Vehicles())));
        var normal = fixture.Open(ArchiveZrdBudget.MaximumAllocation);
        fixture.Publish(normal);
        var loaded = await MissionSceneLoader.LoadAsync(fixture.World, fixture.Resolver, token: Token);
        var actor = Assert.Single(loaded.Actors);
        Assert.Equal("tank_01", actor.Name);
        Assert.Equal(new Vector3(1, 2, 3), actor.PlacementPosition);

        var limited = fixture.Open(0);
        Assert.All(limited.Assets, asset =>
        {
            Assert.Equal(AssetKind.Raw, asset.Kind);
            Assert.Null(asset.Content);
            Assert.True(asset.Metadata["typed_decode_limited"]!.GetValue<bool>());
        });
        fixture.Publish(limited);
        var refused = await MissionSceneLoader.LoadAsync(fixture.World, fixture.Resolver, token: Token);
        Assert.Empty(refused.Actors);
        Assert.Contains(refused.Diagnostics, note => note.Contains("aiv.zrd") && note.Contains("shared typed-decoding budget"));
        Assert.Contains(refused.Diagnostics, note => note.Contains("vehicle.zrd") && note.Contains("shared typed-decoding budget"));
        Assert.Equal(fixture.World.Scene!.Nodes.Count, refused.Scene.Nodes.Count);
        Assert.All(limited.Assets, asset => Assert.Null(asset.Content));
        Assert.Equal(fixture.Bytes, limited.Bytes.ToArray());
        ResourceEditSession edits = new(limited);
        Assert.Equal(fixture.Bytes, ArchiveWriter.Write(limited, edits.Current.Members, Token));

        fixture.Publish(normal);
        var recovered = await MissionSceneLoader.LoadAsync(fixture.World, fixture.Resolver, token: Token);
        Assert.Equal("tank_01", Assert.Single(recovered.Actors).Name);
        Assert.DoesNotContain(recovered.Diagnostics, note => note.Contains("shared typed-decoding budget"));
        Assert.Equal(2, fixture.World.Scene.Nodes.Count); // Preview cloning never changes the original scene.
    }

    [Fact]
    public async Task RefusedMissionMemberIsNotDecodedBeforeShapeValidationOrJsonConversion()
    {
        using Fixture warm = new(ResourceEditingTests.Archive(("aiv.zrd", Spawn()), ("vehicle.zrd", Vehicles())));
        warm.Publish(warm.Open(0));
        _ = await MissionSceneLoader.LoadAsync(warm.World, warm.Resolver, token: Token);
        // A valid AIV record with an expensive authored name; no oversized allocation is needed to demonstrate re-decoding.
        byte[] payload = Spawn(new string('x', 2 * 1024 * 1024));
        using Fixture fixture = new(ResourceEditingTests.Archive(("aiv.zrd", payload), ("vehicle.zrd", Vehicles())));
        var limited = fixture.Open(0);
        fixture.Publish(limited);
        long before = GC.GetTotalAllocatedBytes(precise: true);
        var result = await MissionSceneLoader.LoadAsync(fixture.World, fixture.Resolver, token: Token);
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        Assert.Empty(result.Actors);
        Assert.Contains(result.Diagnostics, note => note.Contains("aiv.zrd") && note.Contains("shared typed-decoding budget"));
        Assert.InRange(allocated, 0, 512 * 1024);
        var asset = limited.Assets.Single(a => a.Name == "aiv.zrd");
        Assert.Null(asset.Content);
        Assert.Equal(payload, limited.Slice(asset.Offset, asset.Length).ToArray());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "zstudio-mission-typed-" + Guid.NewGuid().ToString("N"));
        private readonly Guid owner = Guid.NewGuid();
        public byte[] Bytes { get; }
        public string ArchivePath { get; }
        public ZbdDocument World { get; }
        public AssetResolver Resolver { get; }
        public Fixture(byte[] bytes)
        {
            Bytes = bytes;
            Directory.CreateDirectory(root);
            ArchivePath = Path.Combine(root, "resources.zbd");
            File.WriteAllBytes(ArchivePath, bytes);
            GameScene scene = new();
            scene.Nodes.Add(new(0, "world", "world", null, [], [1], new(), new()));
            scene.Nodes.Add(new(1, "tank", "object3d", null, [0], [], new(), new()));
            World = new(Path.Combine(root, "gamez.zbd"), new(0, DateTime.MinValue), new(FormatFamily.GameZ, 15, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Scene = scene };
            Resolver = new(root);
        }
        public ZbdDocument Open(long budget)
        {
            ZbdDocument document = new(ArchivePath, new(Bytes.Length, DateTime.MinValue), new(FormatFamily.Archive, 1, Recognition.Supported, "fixture"), Bytes);
            new ArchiveReader(budget).Read(document, Token);
            return document;
        }
        public void Publish(params ZbdDocument[] documents) => Resolver.SetWorkspaceSnapshots(owner, documents);
        public void Dispose() { Resolver.Dispose(); Directory.Delete(root, true); }
    }
}
