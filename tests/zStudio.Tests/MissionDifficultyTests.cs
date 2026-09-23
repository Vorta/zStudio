using System.Text;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed partial class AnimationTests
{
    [Fact]
    public async Task DifficultySelectsAuthoredPositionsAndSeparatesCachedBaselines()
    {
        await WithMissionArchiveAsync(new() {
            ["aiv.zrd"] = Zrd(Arr(Str("animated_01"), Spawn(10, 2, 3, 0))),
            ["aiv_easy.zrd"] = Zrd(Arr(Str("animated_02"), Spawn(20, 4, 6, 90))),
            ["aiv_hard.zrd"] = Zrd(Arr(Str("animated_03"), Spawn(30, 6, 9, 180))),
            ["vehicle.zrd"] = Zrd(Arr(Str("animated"), Arr())),
            ["vehicle_easy.zrd"] = Zrd(Arr(Str("animated"), Arr())),
            ["vehicle_hard.zrd"] = Zrd(Arr(Str("animated"), Arr()))
        }, async (world, resolver) => {
            string before = world.Scene!.Nodes[0].Data.ToJsonString();
            var medium = await Load(MissionDifficulty.Medium); var easy = await Load(MissionDifficulty.Easy); var hard = await Load(MissionDifficulty.Hard);
            Assert.Equal("animated_01", Assert.Single(medium.Actors).Name);
            Assert.Equal("animated_02", Assert.Single(easy.Actors).Name);
            Assert.Equal("animated_03", Assert.Single(hard.Actors).Name);
            Assert.Equal(20, SceneBuilder.LocalTransform(easy.Scene.Nodes[easy.Actors[0].Root]).M41);
            Assert.Same(medium, await Load(MissionDifficulty.Medium)); Assert.NotSame(easy, hard);
            Assert.Equal(before, world.Scene.Nodes[0].Data.ToJsonString()); Assert.Equal(2, world.Scene.Nodes.Count);
            Task<MissionSceneContext> Load(MissionDifficulty difficulty) => MissionSceneLoader.LoadAsync(world, resolver, token: TestContext.Current.CancellationToken, difficulty: difficulty);
        });
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingVariantsFallBackIndependently(bool aivVariant)
    {
        var resources = new Dictionary<string, byte[]> {
            ["aiv.zrd"] = Zrd(Arr(Str("animated_01"), Spawn(1, 2, 3, 0))),
            ["vehicle.zrd"] = Zrd(Arr(Str("animated"), Arr()))
        };
        string selected = aivVariant ? "aiv_easy.zrd" : "vehicle_easy.zrd";
        resources[selected] = aivVariant ? Zrd(Arr(Str("animated_02"), Spawn(4, 5, 6, 0))) : Zrd(Arr(Str("animated"), Arr()));
        await WithMissionArchiveAsync(resources, async (world, resolver) => {
            var result = await MissionSceneLoader.LoadAsync(world, resolver, token: TestContext.Current.CancellationToken, difficulty: MissionDifficulty.Easy);
            Assert.Equal(aivVariant ? "aiv_easy.zrd" : "aiv.zrd", result.Layout.AivResource);
            Assert.Equal(aivVariant ? "vehicle.zrd" : "vehicle_easy.zrd", result.Layout.VehicleResource);
            Assert.Equal(aivVariant ? "animated_02" : "animated_01", Assert.Single(result.Actors).Name);
            Assert.Contains(result.Diagnostics, text => text.Contains("using ", StringComparison.Ordinal));
        });
    }
    [Theory]
    [InlineData("aiv_easy.zrd")]
    [InlineData("vehicle_easy.zrd")]
    public async Task EmptySelectedResourceDoesNotMergeOrFallBack(string emptyResource)
    {
        await WithMissionArchiveAsync(new() {
            ["aiv.zrd"] = Zrd(Arr(Str("animated_01"), Spawn(1, 2, 3, 0))),
            ["vehicle.zrd"] = Zrd(Arr(Str("animated"), Arr())), [emptyResource] = Zrd(Arr())
        }, async (world, resolver) => {
            var result = await MissionSceneLoader.LoadAsync(world, resolver, token: TestContext.Current.CancellationToken, difficulty: MissionDifficulty.Easy);
            Assert.Empty(result.Actors);
            Assert.DoesNotContain(result.Diagnostics, text => text.Contains(emptyResource + " is unavailable", StringComparison.Ordinal));
        });
    }
    [Theory]
    [InlineData("aiv_hard.zrd", false)]
    [InlineData("vehicle_hard.zrd", false)]
    [InlineData("aiv_hard.zrd", true)]
    public async Task MalformedSelectedResourceIsReportedWithoutQuietFallback(string resource, bool invalidShape)
    {
        await WithMissionArchiveAsync(new() {
            ["aiv.zrd"] = Zrd(Arr(Str("animated_01"), Spawn(1, 2, 3, 0))),
            ["vehicle.zrd"] = Zrd(Arr(Str("animated"), Arr())),
            [resource] = invalidShape ? Zrd(Arr(Str("incomplete"))) : [4, 0, 0, 0, 0, 0, 0, 0]
        }, async (world, resolver) => {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => MissionSceneLoader.LoadAsync(world, resolver, token: TestContext.Current.CancellationToken, difficulty: MissionDifficulty.Hard));
            Assert.Contains(resource, error.Message); Assert.Contains("Hard", error.Message);
        });
    }
    [Fact]
    public void ReorderedInstancesRemapByProvenanceAndMissingInstancesAreCleared()
    {
        var context = MissionFixture(false); var definitions = Arr(Str("animated"), Arr());
        var before = BuildMission(context.World, null, Arr(Str("animated_01"), Spawn(1, 2, 3, 0), Str("animated_02"), Spawn(4, 5, 6, 0)), definitions, null);
        var after = BuildMission(context.World, null, Arr(Str("animated_02"), Spawn(40, 50, 60, 0)), definitions, null);
        Assert.Equal(before.Actors[0].Root, after.Actors[0].Root); // Same index, different actor.
        Assert.Equal(-1, after.RemapNodeFrom(before, before.Actors[0].Root));
        Assert.Equal(after.Actors[0].Root, after.RemapNodeFrom(before, before.Actors[1].Root));
        context.Mission = before; context.RootOverrides[0] = before.Actors[1].Root;
        var updated = context.Snapshot(); updated.Mission = after; updated.RemapBindingsFrom(context);
        Assert.Equal(after.Actors[0].Root, updated.RootOverrides[0]);
        context.RootOverrides[0] = before.Actors[0].Root; updated.RemapBindingsFrom(context);
        Assert.Empty(updated.RootOverrides); Assert.Contains(updated.Diagnostics, text => text.Contains("was cleared", StringComparison.Ordinal));
    }
    [Fact]
    public void NestedVehicleFieldsDoNotBecomeVehicleDefinitions()
    {
        var context = MissionFixture(false);
        var result = BuildMission(context.World, null, Arr(Str("animated_01"), Spawn(1, 2, 3, 0)), Arr(Str("different"), Arr(Str("animated"), Arr())), null);
        Assert.Empty(result.Actors); Assert.DoesNotContain(result.Diagnostics, text => text.Contains("vehicle definition", StringComparison.Ordinal));
    }
    [Fact]
    public async Task CancellationIsRespectedEvenWhenTheLayoutIsCached()
    {
        await WithMissionArchiveAsync(new() { ["aiv.zrd"] = Zrd(Arr()), ["vehicle.zrd"] = Zrd(Arr()) }, async (world, resolver) => {
            await MissionSceneLoader.LoadAsync(world, resolver, token: TestContext.Current.CancellationToken);
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MissionSceneLoader.LoadAsync(world, resolver, token: canceled.Token));
        });
    }
    [Fact]
    public void DuplicateActorNamesDoNotMakeBindingRemappingAmbiguousSilently()
    {
        var context = MissionFixture(false); var definitions = Arr(Str("animated"), Arr());
        var before = BuildMission(context.World, null, Arr(Str("animated_01"), Spawn(1, 2, 3, 0)), definitions, null);
        var duplicate = BuildMission(context.World, null, Arr(Str("animated_01"), Spawn(1, 2, 3, 0), Str("animated_01"), Spawn(1, 2, 3, 0)), definitions, null);
        Assert.Equal(-1, duplicate.RemapNodeFrom(before, before.Actors[0].Root));
        Assert.Equal(-1, before.RemapNodeFrom(duplicate, duplicate.Actors[0].Root));
    }
    private static async Task WithMissionArchiveAsync(Dictionary<string, byte[]> resources, Func<ZbdDocument, AssetResolver, Task> test)
    {
        string directory = Path.Combine(Path.GetTempPath(), "zbd-difficulty-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            using var data = new MemoryStream(); using var writer = new BinaryWriter(data, Encoding.Latin1, true);
            var offsets = new List<int>(); foreach (byte[] bytes in resources.Values) { offsets.Add((int)data.Position); writer.Write(bytes); }
            int i = 0;
            foreach (var (name, bytes) in resources)
            {
                writer.Write(offsets[i++]); writer.Write(bytes.Length); byte[] record = new byte[140]; Encoding.Latin1.GetBytes(name).CopyTo(record, 0); writer.Write(record);
            }
            writer.Write(1); writer.Write(resources.Count); File.WriteAllBytes(Path.Combine(directory, "resources.zbd"), data.ToArray());
            var source = MissionFixture(false).World;
            var world = new ZbdDocument(Path.Combine(directory, "gamez.zbd"), source.Stamp, source.Probe, source.Bytes) { Scene = source.Scene };
            using var resolver = new AssetResolver(directory); await test(world, resolver);
        }
        finally { Directory.Delete(directory, true); }
    }
    private static byte[] Zrd(JsonNode node)
    {
        using var data = new MemoryStream(); using var writer = new BinaryWriter(data, Encoding.Latin1, true); Write(node); return data.ToArray();
        void Write(JsonNode n)
        {
            switch (n["type"]!.GetValue<string>())
            {
                case "array": var children = n["children"]!.AsArray(); writer.Write(4); writer.Write(children.Count + 1); foreach (var child in children) Write(child!); break;
                case "string": byte[] bytes = Encoding.Latin1.GetBytes(n["value"]!.GetValue<string>()); writer.Write(3); writer.Write(bytes.Length); writer.Write(bytes); break;
                case "int": writer.Write(1); writer.Write(n["value"]!.GetValue<int>()); break;
                default: writer.Write(2); writer.Write(n["value"]!.GetValue<float>()); break;
            }
        }
    }
}
