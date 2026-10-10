using System.Numerics;
using System.Text;
using Recoil.Zbd.Core.Gltf;
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

    /// <summary>
    /// The engine's search-path rule (zRdrAddSearchPaths): a folder named again keeps its place, and a folder that does not
    /// exist is not listed. As common.gw, weapons.gw and bftN.gw do, b is searched before a after a is named again, for
    /// models and for textures.
    /// </summary>
    [Fact]
    public void SearchFoldersNamedAgainKeepTheirPlaceAndMissingFoldersAreNotListed()
    {
        GltfPrimitive primitive = new() { Material = new() { ImageUri = "skin.png" } };
        primitive.Positions.AddRange([Vector3.Zero, Vector3.UnitX, Vector3.UnitZ]);
        primitive.TexCoords.AddRange([Vector2.Zero, Vector2.UnitX, Vector2.UnitY]);
        primitive.Indices.AddRange([0, 1, 2]);
        GltfMesh mesh = new(); mesh.Primitives.Add(primitive);
        GltfDocument document = new(); document.Roots.Add(new() { Name = "surface", Mesh = mesh });
        var (json, binary) = document.Write("surface.bin", Token);
        string script = "SetModelDirectory ..\\data\\a;..\\data\\b;..\\data\\missing\nSetTextureDirectory ..\\data\\t1\nSetTextureDirectory ..\\data\\t2\n"
            + "SetModelDirectory ..\\data\\a\nSetTextureDirectory ..\\data\\t1\nLoadGameGen surface.flt root\nLoadGameGen absent.flt other\nGameZWriteZBDFile gamez.zbd\n";
        var assembler = new WorldAssembler(new Files(script, new()
        {
            ["data/a/surface.gltf"] = json, ["data/a/surface.bin"] = binary, ["data/b/surface.gltf"] = json, ["data/b/surface.bin"] = binary,
            ["data/t1/skin.png"] = [], ["data/t2/skin.png"] = [],
        }), Token);
        assembler.Assemble("m1.gs");
        Assert.Contains("data/b/surface.gltf", assembler.ModelFiles);
        Assert.DoesNotContain("data/a/surface.gltf", assembler.ModelFiles);
        Assert.Equal("data/t2/skin.png", assembler.TextureFiles["skin"]);
        Assert.Contains(assembler.Warnings, w => w.EndsWith("found no model for absent.flt in data/b, data/a.", StringComparison.Ordinal));
    }

    /// <summary>
    /// Search-path probes and model bytes are one allowance for the whole build: repeated loads that try every folder, and
    /// loads that read a declared but unused buffer again, are refused once it is spent, before the next read.
    /// </summary>
    [Fact]
    public void RepeatedLoadsShareOneProbeAndModelByteAllowance()
    {
        string folders = string.Join(";", Enumerable.Range(0, 40).Select(i => $"..\\data\\f{i}")) + ";../data/models";
        string Missing(int loads) => $"SetModelDirectory {folders}\n" + string.Concat(Enumerable.Repeat("LoadGameGen missing.flt x\nDeleteTree x\n", loads)) + "GameZWriteZBDFile gamez.zbd";
        var probing = new WorldAssembler(new Files(Missing(10), folders: [.. Enumerable.Range(0, 40).Select(i => $"data/f{i}")]), Token);
        probing.Assemble("m1.gs");
        long probes = probing.Search.Probes;
        Assert.True(probes > 10 * 41 * 2, $"{probes} probes");
        var refused = new WorldAssembler(new Files(Missing(11), folders: [.. Enumerable.Range(0, 40).Select(i => $"data/f{i}")]), Token) { ProbeLimit = probes };
        Assert.Contains("looks for files more than", Assert.Throws<InvalidDataException>(() => refused.Assemble("m1.gs")).Message);
        Assert.Equal(probes, refused.Search.Probes);

        string Unused(int loads) => "SetModelDirectory ../data/models\n" + string.Concat(Enumerable.Repeat("LoadGameGen unused.gltf x\nDeleteTree x\n", loads)) + "GameZWriteZBDFile gamez.zbd";
        var reading = new WorldAssembler(new Files(Unused(2)), Token);
        reading.Assemble("m1.gs");
        long bytes = reading.Search.ModelBytes;
        Assert.True(bytes > 2 * UnusedBuffer, $"{bytes} bytes");
        Files limitedFiles = new(Unused(3));
        var limited = new WorldAssembler(limitedFiles, Token) { ModelByteLimit = bytes };
        Assert.Contains("cannot be read within the", Assert.Throws<InvalidDataException>(() => limited.Assemble("m1.gs")).Message);
        Assert.Equal(bytes, limited.Search.ModelBytes);
        // The provider refused the third model before handing it over.
        Assert.Equal(bytes, limitedFiles.ModelBytesRead);
    }

    private const int UnusedBuffer = 1 << 16;
    private static string Prefix(int count) => "SetModelDirectory ../data/models\n" + string.Concat(Enumerable.Repeat("NewObject3D padding\n", count));
    private sealed class Files(string script, Dictionary<string, byte[]>? assets = null, IReadOnlyCollection<string>? folders = null) : IProjectFiles
    {
        private readonly byte[] bytes = Encoding.ASCII.GetBytes(script);
        private readonly byte[] empty = """{"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[]}],"nodes":[]}"""u8.ToArray();
        private readonly byte[] one = """{"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0]}],"nodes":[{"name":"inside"}]}"""u8.ToArray();
        private readonly byte[] unused = Encoding.ASCII.GetBytes($$"""{"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0]}],"nodes":[{"name":"inside"}],"buffers":[{"byteLength":{{UnusedBuffer}},"uri":"unused.bin"}]}""");
        private byte[]? Content(string relative) => relative switch
        {
            "gamegen/m1.gs" => bytes,
            "data/models/empty.gltf" => empty,
            "data/models/one.gltf" => one,
            "data/models/unused.gltf" => unused,
            "data/models/unused.bin" => new byte[UnusedBuffer],
            _ => assets?.GetValueOrDefault(relative),
        };
        public bool Exists(string relative) => Content(relative) != null;
        public bool FolderExists(string relative) => relative == "data/models" || folders?.Contains(relative) == true
            || assets?.Keys.Any(k => k.StartsWith(relative + "/", StringComparison.Ordinal)) == true;
        public byte[] Read(string relative, CancellationToken token) => Read(relative, token, ProjectReadLimits.Document);
        public byte[] Read(string relative, CancellationToken token, ProjectReadLimits limits)
        {
            token.ThrowIfCancellationRequested();
            byte[] result = Content(relative) ?? throw new FileNotFoundException(relative);
            limits.Validate(result);
            if (relative.StartsWith("data/", StringComparison.Ordinal)) ModelBytesRead += result.Length;
            return result;
        }
        public long ModelBytesRead { get; private set; }
    }
}
