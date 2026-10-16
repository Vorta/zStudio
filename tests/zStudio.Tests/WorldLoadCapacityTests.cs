using System.Numerics;
using System.Text;
using zStudio.Core.Gltf;
using zStudio.Core.Worlds;
using Xunit;

namespace zStudio.Tests;

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
            + "SetModelDirectory ..\\data\\a\nSetTextureDirectory ..\\data\\t1\nLoadGameGen surface.gltf root\nLoadGameGen absent.gltf other\nGameZWriteZBDFile gamez.zbd\n";
        var assembler = new WorldAssembler(new Files(script, new()
        {
            ["data/a/surface.gltf"] = json, ["data/a/surface.bin"] = binary, ["data/b/surface.gltf"] = json, ["data/b/surface.bin"] = binary,
            ["data/t1/skin.png"] = [], ["data/t2/skin.png"] = [],
        }), Token);
        assembler.Assemble("m1.gs");
        Assert.Contains("data/b/surface.gltf", assembler.ModelFiles);
        Assert.DoesNotContain("data/a/surface.gltf", assembler.ModelFiles);
        Assert.Equal("data/t2/skin.png", assembler.TextureFiles["skin"]);
        Assert.Contains(assembler.Warnings, w => w.EndsWith("found no model for absent.gltf in data/b, data/a.", StringComparison.Ordinal));
    }

    /// <summary>
    /// Script transforms as the engine applies them (gwObject3DSetRotation 0x44e030, SetScale 0x44df00, SetPosition 0x44e300,
    /// rebuilt in the update pass gwNodeUpdate 0x448cc0): rotation and scale drop a node's authored matrix, which is then
    /// rebuilt from the stored rotation and scale (0 and 1 for a glTF node) and its translation; a position only replaces the
    /// matrix's translation; each keeps the identity flag 0x08 while its values are the identity's.
    /// </summary>
    [Fact]
    public void ScriptRotationAndScaleReplaceAnAuthoredMatrixWhileATranslationKeepsIt()
    {
        const string turn = "\"rotation\":[0,0.25881904,0,0.9659258]";
        string model = $$"""{"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0,1,2,3]}],"nodes":[{"name":"turned",{{turn}},"translation":[5,0,0]},{"name":"scaled",{{turn}},"translation":[0,0,7]},{"name":"moved",{{turn}},"translation":[5,0,0]},{"name":"plain"}]}""";
        string script = "SetModelDirectory ../data/models\nLoadGameGen parts.gltf parts\nFindSubNode turned\nObject3DRotate 0 90 0\nFindNode parts\nFindSubNode scaled\nObject3DScale 2 2 2\n"
            + "FindNode parts\nFindSubNode moved\nObject3DTranslate 1 2 3\nFindNode parts\nFindSubNode plain\nObject3DTranslate 0 0 0\nGameZWriteZBDFile gamez.zbd\n";
        var world = new WorldAssembler(new Files(script, new() { ["data/models/parts.gltf"] = Encoding.ASCII.GetBytes(model) }), Token).Assemble("m1.gs");
        WorldNode Node(string name) => world.Nodes.Single(n => n.Name == name);
        Matrix4x4 Expected(Matrix4x4 basis, Vector3 translation) { basis.Translation = translation; return basis; }
        void Placed(string name, int flags, Matrix4x4 expected)
        {
            Assert.Equal(flags, Node(name).PayloadInt(0));
            var actual = WorldUpdate.LocalMatrix(Node(name)) ?? Matrix4x4.Identity;
            for (int r = 0; r < 4; r++) for (int c = 0; c < 3; c++) Assert.Equal(expected[r, c], actual[r, c], 1e-5f);
        }
        Placed("turned", 0x20, Expected(Matrix4x4.CreateRotationY(MathF.PI / 2), new(5, 0, 0)));
        Placed("scaled", 0x20, Expected(Matrix4x4.CreateScale(2), new(0, 0, 7)));
        Placed("moved", 0x30, Expected(Matrix4x4.CreateRotationY(MathF.PI / 6), new(1, 2, 3)));
        Placed("plain", 0x28, Matrix4x4.Identity);
    }

    /// <summary>
    /// Search-path probes and model bytes are one allowance for the whole build: repeated loads that try every folder, and
    /// loads that read a declared but unused buffer again, are refused once it is spent, before the next read.
    /// </summary>
    [Fact]
    public void RepeatedLoadsShareOneProbeAndModelByteAllowance()
    {
        string folders = string.Join(";", Enumerable.Range(0, 40).Select(i => $"..\\data\\f{i}")) + ";../data/models";
        string Missing(int loads) => $"SetModelDirectory {folders}\n" + string.Concat(Enumerable.Repeat("LoadGameGen missing x\nDeleteTree x\n", loads)) + "GameZWriteZBDFile gamez.zbd";
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
