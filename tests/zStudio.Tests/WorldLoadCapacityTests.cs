using System.Text;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class WorldLoadCapacityTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    public void EveryLoadRootRefusesBeyondTheWorldCapacityBeforePublishingAnotherRoot(string model)
    {
        string prefix = Prefix(GameZWorld.MaximumNodeCapacity);
        var assembler = new WorldAssembler(new Files(prefix + $"LoadGameGen {model}.gltf extra\nGameZWriteZBDFile gamez.zbd"), Token);
        Assert.Contains("more nodes than a world holds", Assert.Throws<InvalidDataException>(() => assembler.Assemble("m1.gs")).Message);
        Assert.Equal(GameZWorld.MaximumNodeCapacity, assembler.World.Nodes.Count);
        Assert.Empty(assembler.LoadedRoots);
        Assert.DoesNotContain(assembler.World.Nodes, n => n.Name == "extra");
    }

    [Theory]
    [InlineData("missing", 1)]
    [InlineData("empty", 1)]
    [InlineData("one", 2)]
    public void ExactCapacityLoadsAndFreedSlotsRemainUsable(string model, int loadedNodes)
    {
        string script = Prefix(GameZWorld.MaximumNodeCapacity - loadedNodes) + $"LoadGameGen {model}.gltf first\nDeleteTree first\nLoadGameGen {model}.gltf replacement\nGameZWriteZBDFile gamez.zbd";
        var assembler = new WorldAssembler(new Files(script), Token);
        var world = assembler.Assemble("m1.gs");
        Assert.Equal(GameZWorld.MaximumNodeCapacity, world.Nodes.Count);
        Assert.DoesNotContain(world.Nodes, n => n.Name == "first");
        Assert.Single(world.Nodes, n => n.Name == "replacement");
        Assert.Equal(-1, world.FreeHead);
        Assert.Empty(world.FreedSlots);
    }

    [Fact]
    public void ImportedContentReservesItsUnallocatedLoadRootBeforeGrowing()
    {
        var assembler = new WorldAssembler(new Files(Prefix(GameZWorld.MaximumNodeCapacity - 1) + "LoadGameGen one.gltf root\nGameZWriteZBDFile gamez.zbd"), Token);
        Assert.Contains("more nodes than a world holds", Assert.Throws<InvalidDataException>(() => assembler.Assemble("m1.gs")).Message);
        Assert.Equal(GameZWorld.MaximumNodeCapacity - 1, assembler.World.Nodes.Count);
        Assert.DoesNotContain(assembler.World.Nodes, n => n.Name is "root" or "inside");
    }

    private static string Prefix(int count) => "SetModelDirectory ../data/models\n" + string.Concat(Enumerable.Repeat("NewObject3D padding\n", count));
    private sealed class Files(string script) : IProjectFiles
    {
        private readonly byte[] bytes = Encoding.ASCII.GetBytes(script);
        public bool Exists(string relative) => relative is "gamegen/m1.gs" or "data/models/empty.gltf" or "data/models/one.gltf";
        private readonly byte[] empty = """{"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[]}],"nodes":[]}"""u8.ToArray();
        private readonly byte[] one = """{"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0]}],"nodes":[{"name":"inside"}]}"""u8.ToArray();
        public byte[] Read(string relative, CancellationToken token) => Read(relative, token, ProjectReadLimits.Document);
        public byte[] Read(string relative, CancellationToken token, ProjectReadLimits limits)
        {
            token.ThrowIfCancellationRequested();
            byte[] result = relative switch
            {
            "gamegen/m1.gs" => bytes,
            "data/models/empty.gltf" => empty,
            "data/models/one.gltf" => one,
            _ => throw new FileNotFoundException(relative),
            };
            limits.Validate(result); return result;
        }
    }
}
