using System.Numerics;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using zStudio.Core;
using zStudio.Core.Animation;
using zStudio.Core.Formats;
using zStudio.Core.Export;
using zStudio.Core.Gltf;
using zStudio.Core.Sources;
using zStudio.Core.Worlds;
using Xunit;

namespace zStudio.Tests;

public sealed class Mw3SourceProjectTests
{
    private const string LongLibraryMember = "abcdefghijklmnopqrstuvwxyz1234567890.flt";
    // Initialization must preserve shared audio and every declaration setting while changing only WAV filenames.
    [Fact]
    public async Task SoundIdsNameNativeSourcesAndAllTheirAliases()
    {
        string scratch = Path.Combine(Path.GetTempPath(), "zstudio-mw3-sound-" + Guid.NewGuid().ToString("N"));
        string corpus = Path.Combine(scratch, "zbd"), project = Path.Combine(scratch, "project"), output = Path.Combine(scratch, "export");
        var token = TestContext.Current.CancellationToken;
        try
        {
            Mw3TestCorpus.Create(corpus); byte[] wave = Mw3TestCorpus.AddSounds(corpus);
            var original = ZrdDecoder.Read(ArchiveSources.Read(File.ReadAllBytes(Path.Combine(corpus, "reader.zbd"))).Single(m => m.Name == "sounds.zrd").Payload, token);
            var report = await SourceExtractor.ExtractAsync(corpus, project, token: token);
            Assert.Contains(report.Notes, n => n.Contains("first declared sound ID", StringComparison.Ordinal));
            Assert.Contains(report.Notes, n => n.Contains("absent", StringComparison.Ordinal));
            string sounds = Path.Combine(project, "data/common/sounds");
            Assert.Equal(["mech_powerup.wav", "menu_select.wav", "orphan.wav"], Directory.GetFiles(sounds).Select(Path.GetFileName).Order());
            Assert.Equal(wave, File.ReadAllBytes(Path.Combine(sounds, "mech_powerup.wav")));
            var renamed = ZrdText.Parse(File.ReadAllBytes(Path.Combine(project, SourceBuilder.SoundDefinitions)), token);
            var aliases = AnimationPreviewContext.ReadSoundAliases(renamed, token).ToArray();
            Assert.Equal(["mech_powerup.wav", "menu_select.wav", "mech_powerup.wav", "absent.wav"], aliases.Select(a => a.File));
            Assert.Equal(WithoutWaveFilenames(original, token), WithoutWaveFilenames(renamed, token));
            var built = await SourceBuilder.ExportAsync(project, output, token: token, target: ExportTarget.Mw3);
            Assert.Equal(0, built.Failed); Assert.Equal(Mw3TestCorpus.OutputCount + 1, built.Built);
            var bank = ArchiveSources.Read(File.ReadAllBytes(Path.Combine(output, "soundsH.zbd")));
            Assert.Equal(wave, bank.Single(m => m.Name == "mech_powerup.wav").Payload.ToArray());
            var compiled = ZrdDecoder.Read(ArchiveSources.Read(File.ReadAllBytes(Path.Combine(output, "reader.zbd"))).Single(m => m.Name == "sounds.zrd").Payload, token);
            Assert.Equal(ZrdWriter.Write(renamed, token), ZrdWriter.Write(compiled, token));
        }
        finally { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }

    }

    private static byte[] WithoutWaveFilenames(ZrdNode node, CancellationToken token)
    {
        ZrdNode Strip(ZrdNode n) => n with { Children = n.Children.Select((c, i) => i == 1 && n.Children is [{ Kind: ZrdKind.String }, { Kind: ZrdKind.String } f, ..]
            && f.Text.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? c with { Text = "file.wav" } : Strip(c)).ToArray() };
        return ZrdWriter.Write(Strip(node), token);
    }

    [Theory]
    [InlineData("( same \"0001.wav\" ) ( same button.wav )")]
    [InlineData("( orphan \"0001.wav\" )")]
    [InlineData("( absent \"0001.wav\" ) ( missing absent.wav )")]
    [InlineData("( NUL \"0001.wav\" )")]
    public async Task AmbiguousOrUnsavableSoundNamesRollbackInitialization(string declarations)
    {
        string scratch = Path.Combine(Path.GetTempPath(), "zstudio-mw3-sound-refuse-" + Guid.NewGuid().ToString("N"));
        try
        {
            string corpus = Path.Combine(scratch, "zbd"), project = Path.Combine(scratch, "project");
            Mw3TestCorpus.Create(corpus); Mw3TestCorpus.AddSounds(corpus, declarations);
            await Assert.ThrowsAsync<InvalidDataException>(() => SourceExtractor.ExtractAsync(corpus, project, token: TestContext.Current.CancellationToken));
            Assert.False(Directory.Exists(project));
        }
        finally { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
    }
    [Theory]
    [InlineData(242, 2, false)]
    [InlineData(255, 1, false)]
    [InlineData(32, 1, true)]
    public void PaintedLargeAndStraightCornerNativePolygonsRetainTheirRecordedBoundaries(int corners, int copies, bool straightCorner)
    {
        var token = TestContext.Current.CancellationToken;
        GltfDocument document = new() { SceneExtras = new() { [WorldGltf.Key] = new JsonObject { ["sourceFormat"] = 4, ["game"] = "mw3" } } };
        GltfMesh mesh = new();
        for (int copy = 0; copy < copies; copy++)
        {
            GltfPrimitive p = new() { Material = new() };
            for (int i = 0; i < corners; i++)
            {
                double angle = i * Math.PI * 2 / corners; float radius = straightCorner || i % 2 == 0 ? 1 : 0.8f;
                p.Positions.Add(new(radius * (float)Math.Cos(angle), copy * 3, radius * (float)Math.Sin(angle)));
                p.Colors.Add(new((float)i / (corners - 1), 0, 0));
            }
            if (straightCorner) p.Positions[1] = (p.Positions[0] + p.Positions[2]) / 2;
            foreach (var (a, b, c) in WorldGltf.Triangulate(p.Positions))
                if (!ModelBuilder.Straight(p.Positions[a], p.Positions[b], p.Positions[c])) p.Indices.AddRange([a, b, c]);
            if (straightCorner) Assert.True(p.Indices.Count / 3 < corners - 2);
            p.Extras = new() { [WorldGltf.Key] = new JsonObject { ["polygons"] = new JsonArray(new JsonObject
                { ["triangles"] = p.Indices.Count / 3, ["corners"] = new JsonArray(Enumerable.Range(0, corners).Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()) }) } };
            mesh.Primitives.Add(p);
        }
        document.Roots.Add(new() { Name = "painted", Mesh = mesh });
        var bytes = document.Write("model.bin", token); var read = GltfDocument.Read(bytes.Json, _ => bytes.Binary, token);
        GameZWorld world = new() { SourceVersion = 27 };
        _ = WorldGltf.Import(read, "model.gltf", 255, new() { World = world, Token = token, Reference = (_, _) => throw new InvalidOperationException(), TextureName = (_, _, _) => "unused" });
        Assert.Equal(copies, world.Models.Single().Polygons.Count);
        foreach (var pair in mesh.Primitives.Zip(world.Models.Single().Polygons))
        { Assert.Equal(corners, pair.Second.Vertices.Length); Assert.Equal(pair.First.Colors.Select(c => c * 255), pair.Second.Colors); }
    }

    [Fact]
    public void NativeTerrainAuthoringRefusesBeforeChangingTheWorkspace()
    {
        var token = TestContext.Current.CancellationToken;
        string root = Path.Combine(Path.GetTempPath(), "zstudio-native-terrain-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "data/c1/models")); Directory.CreateDirectory(Path.Combine(root, "gamegen"));
            GltfPrimitive primitive = new() { Material = new() };
            primitive.Positions.AddRange([Vector3.Zero, Vector3.UnitX, Vector3.UnitZ]); primitive.Indices.AddRange([0, 1, 2]);
            primitive.Colors.AddRange([Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ]);
            GltfMesh mesh = new(); mesh.Primitives.Add(primitive);
            GltfDocument document = new() { SceneExtras = new() { [WorldGltf.Key] = new JsonObject { ["sourceFormat"] = 4, ["game"] = "mw3" } } };
            document.Roots.Add(new() { Name = "land", Mesh = mesh });
            string database = "data/c1/models/c1.gltf", surface = "data/c1/models/land.gltf";
            var bytes = document.Write("model.bin", token);
            foreach (string path in new[] { database, surface }) File.WriteAllBytes(Path.Combine(root, path), bytes.Json);
            File.WriteAllBytes(Path.Combine(root, "data/c1/models/model.bin"), bytes.Binary);
            SourceWorkspace workspace = new(root);
            var references = (new HashSet<string>(), (IReadOnlyList<System.Text.RegularExpressions.Regex>)[]);
            Assert.Equal(SourceTerrain.NativeUnsupported, Assert.Throws<InvalidDataException>(() => SourceTerrainConversion.Plan(workspace, database, references, token)).Message);
            TerrainConversionPlan plan = new(database, "data/c1/models/out.gltf", "data/c1/models/out.terrain.json", [new("land", 0x18, 255, [0])], []);
            Assert.Equal(SourceTerrain.NativeUnsupported, Assert.Throws<InvalidDataException>(() => SourceTerrainConversion.Apply(workspace, plan, token)).Message);
            Assert.Equal(SourceTerrain.NativeUnsupported, Assert.Throws<InvalidDataException>(() => SourceTerrain.Create(workspace, database, surface, ["land"], token: token)).Message);
            Assert.False(workspace.IsDirty); Assert.Equal(bytes.Json, File.ReadAllBytes(Path.Combine(root, database)));
            GltfDocument marker = new(); marker.Roots.Add(new() { Name = "terrain", Extras = new() { [WorldGltf.Key] = new JsonObject { ["terrain"] = "out.terrain.json" } } });
            Assert.Equal(SourceTerrain.NativeUnsupported, Assert.Throws<InvalidDataException>(() => WorldGltf.Import(marker, database, 255,
                new() { World = new() { SourceVersion = 27 }, Token = token, Reference = (_, _) => throw new InvalidOperationException(), TextureName = (_, _, _) => "unused" })).Message);
            var recipe = new zStudio.Core.Terrain.TerrainRecipe(1, [new("land", "land.gltf", "land", zStudio.Core.Terrain.TerrainAttributes.None)], zStudio.Core.Terrain.TerrainAttributes.None, []);
            Assert.Equal(SourceTerrain.NativeUnsupported, Assert.Throws<InvalidDataException>(() => WorldGltf.Import(marker, database, 255,
                new() { World = new(), Token = token, Reference = (_, _) => (document, surface), ReadFile = (_, _) => (recipe.Write(), "out.terrain.json"),
                    TextureName = (_, _, _) => "unused", Grid = () => new(0, 2, 2, -2, 2, -2, 1, 1) })).Message);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void NativeSourcePolygonRecoveryPreservesSurfaceAndAttributes()
    {
        var token = TestContext.Current.CancellationToken;
        GltfPrimitive primitive = new() { Material = new() };
        primitive.Positions.AddRange([Vector3.Zero, Vector3.UnitX, Vector3.UnitX + Vector3.UnitZ, Vector3.UnitZ]);
        primitive.Colors.AddRange([Vector3.UnitX, Vector3.Zero, Vector3.UnitX, Vector3.Zero]);
        primitive.Indices.AddRange([0, 1, 2, 0, 2, 3]);
        GltfMesh mesh = new(); mesh.Primitives.Add(primitive);
        GltfDocument document = new() { SceneExtras = new() { [WorldGltf.Key] = new JsonObject { ["sourceFormat"] = 4, ["game"] = "mw3" } } };
        document.Roots.Add(new() { Name = "surface", Mesh = mesh });
        Assert.Equal(2, Compile().Models.Single().Polygons.Count); // Unrecorded triangles must retain their RGB interpolation.
        primitive.Extras = new() { [WorldGltf.Key] = new JsonObject { ["polygons"] = new JsonArray(new JsonObject { ["triangles"] = 2, ["corners"] = new JsonArray(0, 1, 2, 3) }) } };
        var recorded = Compile();
        Assert.Equal(4, Assert.Single(recorded.Models.Single().Polygons).Vertices.Length); // Matching native boundaries remain authoritative.
        primitive.Indices.Clear(); primitive.Indices.AddRange([0, 1, 3, 1, 2, 3]);
        Assert.Contains("polygon boundaries", Assert.Throws<InvalidDataException>(() => Compile()).Message, StringComparison.Ordinal);
        primitive.Colors.Clear(); primitive.Colors.AddRange(Enumerable.Repeat(Vector3.One, 4));
        var savedRecords = primitive.Extras![WorldGltf.Key]!["polygons"]!.DeepClone();
        primitive.Extras[WorldGltf.Key]!.AsObject().Remove("polygons");
        primitive.Indices.Clear(); primitive.Indices.AddRange([0, 1, 2, 0, 2, 3]);
        primitive.Normals.AddRange([Vector3.UnitY, Vector3.UnitY, Vector3.UnitZ, Vector3.UnitY]);
        Assert.Equal(2, Compile().Models.Single().Polygons.Count);
        primitive.Material!.Extras = new() { [WorldGltf.Key] = new JsonObject { ["normals"] = false } };
        Assert.Single(Compile().Models.Single().Polygons); // An unused normal stream does not constrain the surface.
        primitive.Material.Extras = null; primitive.Normals.Clear();
        primitive.Extras[WorldGltf.Key]!["polygons"] = savedRecords;
        primitive.Indices.Clear(); primitive.Indices.AddRange([0, 1, 3, 1, 2, 3]);
        primitive.Material!.ImageUri = "checker.png";
        primitive.TexCoords.AddRange([Vector2.Zero, Vector2.UnitX, Vector2.One, new(0, 2)]);
        Assert.Contains("polygon boundaries", Assert.Throws<InvalidDataException>(() => Compile()).Message, StringComparison.Ordinal);
        primitive.TexCoords[3] = Vector2.UnitY;
        Assert.Single(Compile().Models.Single().Polygons); // Alternate diagonals are safe when every attribute is affine.
        primitive.Targets.Add([Vector3.Zero, Vector3.Zero, Vector3.UnitY, Vector3.Zero]);
        Assert.Contains("polygon boundaries", Assert.Throws<InvalidDataException>(() => Compile()).Message, StringComparison.Ordinal);
        primitive.Targets.Clear();
        primitive.Normals.AddRange([Vector3.UnitY, Vector3.UnitY, Vector3.UnitZ, Vector3.UnitY]);
        Assert.Contains("polygon boundaries", Assert.Throws<InvalidDataException>(() => Compile()).Message, StringComparison.Ordinal);
        primitive.Normals.Clear(); primitive.Positions[2] += Vector3.UnitY;
        Assert.Contains("polygon boundaries", Assert.Throws<InvalidDataException>(() => Compile()).Message, StringComparison.Ordinal);
        primitive.Positions[2] -= Vector3.UnitY;
        primitive.Indices.Clear(); primitive.Indices.AddRange([0, 1, 2, 0, 2, 3]);
        primitive.Extras[WorldGltf.Key]!["polygons"] = new JsonArray(2);
        primitive.Positions[1] = new(0.2f, 0, 0.8f);
        Assert.Single(Compile().Models.Single().Polygons); // Numeric metadata authors the exact input fan, even when concave.
        GameZWorld Compile()
        {
            var bytes = document.Write("mesh.bin", token);
            var read = GltfDocument.Read(bytes.Json, _ => bytes.Binary, token);
            GameZWorld world = new() { SourceVersion = 27 };
            _ = WorldGltf.Import(read, "model.gltf", 255, new() { World = world, Token = token, Reference = (_, _) => throw new InvalidOperationException(), TextureName = (_, _, _) => "unused" });
            return world;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectedMechTextureExportRefusesNewLibraryMembers(bool existingMember)
    {
        var token = TestContext.Current.CancellationToken;
        string root = Path.Combine(Path.GetTempPath(), "zstudio-native-texture-plan-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "data/common/mechs/models"));
            Directory.CreateDirectory(Path.Combine(root, "data/common/mechs/textures")); Directory.CreateDirectory(Path.Combine(root, "gamegen"));
            // A separate native marker covers adding the first library member as well as adding another one.
            WriteModel("data/c1/models/c1.gltf", false, false);
            if (existingMember) WriteModel("data/common/mechs/models/a.gltf", true, false);
            File.WriteAllBytes(Path.Combine(root, "data/common/mechs/textures/spare.png"), PngEncoder.Encode(new(1, 1, [255, 0, 0, 255]), token));
            SourceBuilder.Snapshot snapshot = new(root);
            var all = SourceBuilder.Plan(root, null, true, token, snapshot);
            var selected = all.Single(p => p.Path == "rmechtex.zbd");
            _ = SourceBuilder.Build(root, selected, snapshot, token);
            WriteModel("data/common/mechs/models/b.gltf", true, true);
            Assert.Throws<InvalidDataException>(() => SourceBuilder.CheckPlanUnchanged(root, TextureDithering.None, [selected], all, snapshot, token));
            SourceBuilder.Snapshot fresh = new(root);
            var current = SourceBuilder.Plan(root, null, true, token, fresh).Single(p => p.Path == "rmechtex.zbd");
            _ = SourceBuilder.Build(root, current, fresh, token);
            Assert.Equal(1, fresh.NativeLibraryResult!.TextureAddressing["spare"]);
            void WriteModel(string path, bool member, bool textured)
            {
                GltfDocument doc = new() { SceneExtras = new() { [WorldGltf.Key] = new JsonObject { ["sourceFormat"] = 4, ["game"] = "mw3" } } };
                if (member) doc.SceneExtras[WorldGltf.Key]!["mechLibraryMember"] = Path.GetFileNameWithoutExtension(path) + ".flt";
                GltfNode node = new() { Name = "part" }; doc.Roots.Add(node);
                if (textured)
                {
                    GltfPrimitive p = new() { Material = new() { ImageUri = "../textures/spare.png", ClampS = true } };
                    p.Positions.AddRange([Vector3.Zero, Vector3.UnitX, Vector3.UnitZ]); p.Indices.AddRange([0, 1, 2]);
                    p.TexCoords.AddRange([Vector2.Zero, Vector2.UnitX, Vector2.UnitY]); node.Mesh = new(); node.Mesh.Primitives.Add(p);
                }
                string target = Path.Combine(root, path); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var bytes = doc.Write(Path.GetFileNameWithoutExtension(path) + ".bin", token);
                File.WriteAllBytes(target, bytes.Json); File.WriteAllBytes(Path.ChangeExtension(target, ".bin"), bytes.Binary);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(5126)]
    [InlineData(5121)]
    [InlineData(5123)]
    public void EditedRgbaVertexColorsPreserveRgbAndRefuseLostAlpha(int componentType)
    {
        var token = TestContext.Current.CancellationToken;
        GltfDocument document = new() { SceneExtras = new() { [WorldGltf.Key] = new JsonObject { ["game"] = "mw3", ["sourceFormat"] = 4 } } };
        GltfPrimitive primitive = new() { Material = new() };
        primitive.Positions.AddRange([Vector3.Zero, Vector3.UnitX, Vector3.UnitZ]); primitive.Indices.AddRange([0, 1, 2]);
        primitive.Colors.AddRange([Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ]);
        GltfMesh mesh = new(); mesh.Primitives.Add(primitive); document.Roots.Add(new() { Name = "colored", Mesh = mesh });
        var serialized = document.Write("mesh.bin", token); var root = JsonNode.Parse(serialized.Json)!.AsObject();
        int colorIndex = root["meshes"]![0]!["primitives"]![0]!["attributes"]!["COLOR_0"]!.GetValue<int>();
        var accessor = root["accessors"]![colorIndex]!; var view = root["bufferViews"]![accessor["bufferView"]!.GetValue<int>()]!;
        int size = componentType == 5126 ? 4 : componentType == 5123 ? 2 : 1;
        int offset = (serialized.Binary.Length + 3) & ~3;
        byte[] bytes = new byte[offset + 12 * size]; serialized.Binary.CopyTo(bytes, 0);
        for (int i = 0; i < 3; i++) { Put(offset + (i * 4 + i) * size, true); Put(offset + (i * 4 + 3) * size, true); }
        accessor["type"] = "VEC4"; accessor["componentType"] = componentType; accessor["normalized"] = componentType != 5126;
        view["byteOffset"] = offset; view["byteLength"] = 12 * size; root["buffers"]![0]!["byteLength"] = bytes.Length;
        byte[] json = Encoding.UTF8.GetBytes(root.ToJsonString());
        var read = GltfDocument.Read(json, _ => bytes, token);
        Assert.Equal(primitive.Colors, read.Roots[0].Mesh!.Primitives[0].Colors);
        Put(offset + 3 * size, false);
        var error = Assert.Throws<InvalidDataException>(() => GltfDocument.Read(json, _ => bytes, token));
        Assert.Contains("vertex colour alpha", error.Message, StringComparison.Ordinal);
        void Put(int at, bool opaque)
        {
            if (componentType == 5126) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(at), opaque ? 1 : 0);
            else if (componentType == 5123) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), opaque ? ushort.MaxValue : (ushort)0);
            else bytes[at] = opaque ? byte.MaxValue : (byte)0;
        }
    }

    [Fact]
    public async Task NativeReconstructionUsesOriginalPathsAndBuildsEditsWithoutTheCorpus()
    {
        var token = TestContext.Current.CancellationToken;
        string scratch = Path.Combine(Path.GetTempPath(), "zstudio-mw3-native-" + Guid.NewGuid().ToString("N"));
        try
        {
            string corpus = Path.Combine(scratch, "corpus"), root = Path.Combine(scratch, "project"); Mw3TestCorpus.Create(corpus);
            var extracted = await SourceExtractor.ExtractAsync(corpus, root, token: token);
            Assert.Equal(GameVariant.MechWarrior3, extracted.Game); Assert.Empty(extracted.NotReconstructed);
            Assert.Equal(["data", "gamegen"], Directory.GetDirectories(root).Select(Path.GetFileName).Order().ToArray());
            Assert.DoesNotContain(Directory.GetFiles(root, "*", SearchOption.AllDirectories), p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || p.Contains(".sources", StringComparison.OrdinalIgnoreCase));
            foreach (string path in new[] { Mw3TestCorpus.Resource, Mw3TestCorpus.Definitions, Mw3TestCorpus.Script, "gamegen/c1.gs", "data/c1/models/c1.gltf" }) Assert.True(File.Exists(P(path)), path);
            Assert.Contains("%DB%.gltf", File.ReadAllText(P("gamegen/c1.gs")));
            Directory.Move(corpus, corpus + "-offline");
            Directory.CreateDirectory(P("data/aaa/models"));
            File.WriteAllText(P("data/aaa/models/new.gltf"), "{\"asset\":{\"version\":\"2.0\"},\"scenes\":[{\"nodes\":[]}],\"scene\":0,\"nodes\":[]}");
            File.WriteAllText(P("data/aaa/models/broken.gltf"), "{ unfinished edit");
            Assert.Equal(GameVariant.MechWarrior3, SourceProject.Game(root, token));
            Assert.All(SourceBuilder.Plan(root, token: token), p => Assert.Equal(new[] { "mw3" }, ExportTargets.Targets(p)));
            Assert.Equal(27u, ExportTarget.Mw3.WorldVersion()); Assert.Equal(39u, ExportTarget.Mw3.AnimationFormat());
            await Assert.ThrowsAsync<InvalidDataException>(() => SourceBuilder.CheckAsync(root, token: token, target: ExportTarget.V2));
            var original = await SourceBuilder.CheckAsync(root, token: token, target: ExportTarget.Mw3); Assert.Equal(0, original.Failed); Assert.Equal(Mw3TestCorpus.OutputCount, original.Built);
            string first = Path.Combine(scratch, "first"), second = Path.Combine(scratch, "second");
            await SourceBuilder.ExportAsync(root, first, token: token, target: ExportTarget.Mw3); await SourceBuilder.ExportAsync(root, second, token: token, target: ExportTarget.Mw3);
            var firstWorld = GameZWorldReader.FromDocument(await FormatRegistry.Default.OpenAsync(Path.Combine(first, "c1/gamez.zbd"), token), token);
            Assert.Equal(8u, firstWorld.Nodes.Single(n => n.Name == "c1.flt").Flags & 0x18u);
            Assert.Contains(firstWorld.Nodes, n => n.Name == "ScrollAlways" && n.Parents.Count == 0);
            var uncolored = firstWorld.Models.Single().Polygons.Single(); uncolored.Colors = [];
            firstWorld.Models.Single().Polygons.Add(new() { Material = uncolored.Material, Vertices = uncolored.Vertices, Uvs = uncolored.Uvs, Colors = [] });
            var whiteDocument = FormatRegistry.Default.OpenBytes("default-colors.zbd", GameZWriter.Write(firstWorld, token), token: token);
            Assert.DoesNotContain(whiteDocument.Diagnostics, d => d.Severity == "Error" || d.Message.Contains("out-of-range", StringComparison.Ordinal));
            var whiteWorld = GameZWorldReader.FromDocument(whiteDocument, token);
            Assert.Equal(2, whiteWorld.Models.Single().Polygons.Count);
            foreach (var polygon in whiteWorld.Models.Single().Polygons) { Assert.Equal(uncolored.Vertices, polygon.Vertices); Assert.All(polygon.Colors, color => Assert.Equal(new Vector3(255), color)); }
            foreach (string path in Directory.GetFiles(first, "*", SearchOption.AllDirectories)) Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(Path.Combine(second, Path.GetRelativePath(first, path))));
            var originalImage = await FormatRegistry.Default.OpenAsync(Path.Combine(first, "rimage.zbd"), token);
            Assert.Equal(new byte[] { 0, 174, 0, 255, 0, 0, 0, 0 }, TextureDecoder.Decode(originalImage, originalImage.Assets.Single(), token).Rgba);
            await Assert.ThrowsAsync<InvalidDataException>(() => SourceBuilder.ExportAsync(root, Path.Combine(root, "out"), token: token, target: ExportTarget.Mw3));
            await Assert.ThrowsAsync<IOException>(() => SourceBuilder.ExportAsync(root, first, token: token, target: ExportTarget.Mw3));
            File.WriteAllText(P(Mw3TestCorpus.Resource), "( VALUE 2 )");
            File.WriteAllBytes(P("data/common/images/button.png"), PngEncoder.Encode(new(2, 1, [0, 0, 0, 255, 0, 0, 0, 0]), token));
            File.WriteAllText(P("gamegen/c1.gs"), File.ReadAllText(P("gamegen/c1.gs")).Replace("WorldExtents 16 -16", "WorldExtents 32 -16", StringComparison.Ordinal));
            File.WriteAllText(P("gamegen/c1.gs"), File.ReadAllText(P("gamegen/c1.gs")).Replace("altitud_off", "altitude_off", StringComparison.Ordinal).Replace("on 0.015 0", "on 0.02 0.04", StringComparison.Ordinal));
            string model = "data/c1/models/c1.gltf";
            var doc = GltfDocument.Read(File.ReadAllBytes(P(model)), uri => File.ReadAllBytes(P(WorldAssembler.Relative(model, uri))), token);
            Assert.Equal(1, doc.SceneExtras![WorldGltf.Key]!["mw3TextureAddressing"]!["scripttex"]!.GetValue<int>());
            doc.SceneExtras[WorldGltf.Key]!["mw3TextureAddressing"]!["scripttex"] = 2;
            doc.Roots[0].Matrix = Matrix4x4.CreateTranslation(0, 2, 0);
            doc.Roots[0].Extras![WorldGltf.Key]!["landmark"] = true;
            var surface = doc.AllNodes().SelectMany(n => n.Mesh?.Primitives ?? []).First().Material!;
            Assert.All(doc.AllNodes().SelectMany(n => n.Mesh?.Primitives ?? []).SelectMany(p => p.Colors), color => Assert.Equal(new Vector3(1, 128f / 255f, 64f / 255f), color));
            foreach (var primitive in doc.AllNodes().SelectMany(n => n.Mesh?.Primitives ?? []))
                for (int i = 0; i < primitive.Colors.Count; i++) primitive.Colors[i] = new(0.25f, 0.5f, 1);
            surface.ImageUri = "../textures/custom/new.png";
            Directory.CreateDirectory(P("data/c1/textures/custom"));
            File.WriteAllBytes(P("data/c1/textures/custom/new.png"), PngEncoder.Encode(new(1, 1, [0, 255, 0, 255]), token));
            var editedModel = doc.Write("c1.bin", token); File.WriteAllBytes(P(model), editedModel.Json); File.WriteAllBytes(P("data/c1/models/c1.bin"), editedModel.Binary);
            File.Delete(P(model)); model = "data/c1/models/c1.glb"; File.WriteAllBytes(P(model), Glb(editedModel.Json, editedModel.Binary));
            File.WriteAllText(P("gamegen/c1.gs"), File.ReadAllText(P("gamegen/c1.gs")).Replace("%DB%.gltf", "%DB%.glb", StringComparison.Ordinal));
            Assert.Equal(GameVariant.MechWarrior3, SourceProject.Game(root, token)); // The last marked model can use the binary container.
            File.WriteAllText(P(Mw3TestCorpus.Definitions), File.ReadAllText(P(Mw3TestCorpus.Definitions)).Replace("ON_CALL", "ON_STARTUP", StringComparison.Ordinal));
            File.WriteAllText(P(Mw3TestCorpus.Script), "OBJECT ground\nFRAME 0 POSITION 0 0 0 VELOCITY 0 2 0\nFRAME 2 POSITION 0 0.13333334 0\n");
            Directory.CreateDirectory(P("data/common/mechs/models"));
            GltfDocument part = new(); part.Roots.Add(new() { Name = "library-part" });
            var partBytes = part.Write("part.bin", token); File.WriteAllBytes(P("data/common/mechs/models/part.gltf"), partBytes.Json);
            File.WriteAllText(P("data/common/mechs/models/unrelated.gltf"), "{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{\"nodes\":[]}],\"buffers\":[{\"uri\":\"unfinished.bin\",\"byteLength\":4}]}");
            for (int i = 0; i < 2; i++)
            {
                string path = $"data/common/mechs/models/toy{i}.gltf";
                var libraryModel = GltfDocument.Read(editedModel.Json, _ => editedModel.Binary, token);
                libraryModel.SceneExtras![WorldGltf.Key]!["mechLibraryMember"] = i == 1 ? LongLibraryMember : $"toy{i}.flt";
                if (i == 1) libraryModel.SceneExtras[WorldGltf.Key]!.AsObject().Remove("loadRoot");
                if (i == 0) libraryModel.SceneExtras[WorldGltf.Key]!["loadRoot"] = new JsonObject { ["zone"] = 3, ["altitudeZoneCheck"] = true };
                var libraryMaterial = libraryModel.AllNodes().SelectMany(n => n.Mesh?.Primitives ?? []).First().Material!;
                libraryMaterial.ImageUri = "../../../c1/textures/custom/new.png";
                if (i == 1) libraryMaterial.Extras![WorldGltf.Key]!.AsObject().Remove("texture");
                if (i == 1) libraryModel.Roots[0].Children.Add(new() { Name = "part-reference", Extras = new JsonObject { [WorldGltf.Key] = new JsonObject { ["ref"] = "part.gltf" } } });
                var written = libraryModel.Write($"toy{i}.bin", token);
                if (i == 1) File.WriteAllBytes(P(path[..^5] + ".glb"), Glb(written.Json, written.Binary));
                else { File.WriteAllBytes(P(path), written.Json); File.WriteAllBytes(P(path[..^5] + ".bin"), written.Binary); }
            }
            var changed = await SourceBuilder.ExportAsync(root, second, overwrite: true, token: token, target: ExportTarget.Mw3); Assert.Equal(0, changed.Failed);
            var changedImage = await FormatRegistry.Default.OpenAsync(Path.Combine(second, "rimage.zbd"), token);
            Assert.Equal(new byte[] { 0, 0, 0, 255, 0, 0, 0, 0 }, TextureDecoder.Decode(changedImage, changedImage.Assets.Single(), token).Rgba);
            var resource = await FormatRegistry.Default.OpenAsync(Path.Combine(second, "reader.zbd"), token);
            Assert.Equal(2u, ((ZrdNode)resource.Assets.Single().Content!).Children.Single().Children[1].Bits);
            var animations = (await FormatRegistry.Default.OpenAsync(Path.Combine(second, "c1/anim.zbd"), token)).Animations!;
            Assert.Equal(4, animations.Entries[1].Bytes[153]);
            Assert.NotEqual(File.ReadAllBytes(Path.Combine(first, "c1/anim.zbd")), File.ReadAllBytes(Path.Combine(second, "c1/anim.zbd")));
            var world = GameZWorldReader.FromDocument(await FormatRegistry.Default.OpenAsync(Path.Combine(second, "c1/gamez.zbd"), token), token);
            Assert.Contains(world.Nodes, n => n.Name == "ground" && WorldUpdate.LocalMatrix(n)?.Translation.Y == 2 && (n.Flags & WorldUpdate.LandmarkFlag) != 0);
            Assert.Equal(64, world.Models.Single().Polygons.Single().Vertices.Length);
            Assert.Equal(0u, world.Nodes.Single(n => n.Name == "c1.flt").Flags & 0x18u);
            Assert.Equal(0.02f, world.Models.Single().ScrollU); Assert.Equal(0.04f, world.Models.Single().ScrollV);
            Assert.All(world.Models.Single().Polygons.Single().Colors, color => Assert.Equal(new Vector3(63.75f, 127.5f, 255), color));
            Assert.Equal("c1.flt", world.Nodes.Single(n => n.Name == "reserve").Parents.Single().Name);
            Assert.DoesNotContain(world.Nodes.Single(n => n.Class == WorldNodeClass.World).Children, n => n.Name == "reserve");
            Assert.NotEqual(File.ReadAllBytes(Path.Combine(first, "c1/gamez.zbd")), File.ReadAllBytes(Path.Combine(second, "c1/gamez.zbd")));
            foreach (string pack in new[] { "c1/rtexture.zbd", "rmechtex.zbd" })
            {
                var textures = await FormatRegistry.Default.OpenAsync(Path.Combine(second, pack), token);
                Assert.Equal(new byte[] { 0, 255, 0, 255 }, TextureDecoder.Decode(textures, textures.Assets.Single(a => a.Name == "new"), token).Rgba);
                if (pack.StartsWith("c1/", StringComparison.Ordinal)) Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(textures.Bytes.Span[((int)textures.Assets.Single(a => a.Name == "scripttex").Offset + 14)..]) & 3);
            }
            var library = await FormatRegistry.Default.OpenAsync(Path.Combine(second, "mechlib.zbd"), token);
            Assert.Equal(2, library.Assets.Count(a => a.Content is MechAssembly)); Assert.Contains(library.Scene!.Textures, t => t.Text("name") == "new");
            Assert.Contains(library.Scene.Nodes, n => n.Name == "library-part");
            var libraryWorld = GameZWorldReader.FromDocument(library, token);
            Assert.Equal(3u, libraryWorld.Nodes.Single(n => n.Name == "toy0.flt").Zone);
            var newLibraryRoot = libraryWorld.Nodes[((MechAssembly)library.Assets.Single(a => a.Name == LongLibraryMember).Content!).RootNode];
            Assert.NotEqual(0u, newLibraryRoot.Flags & 4); Assert.Equal(new[] { 0, 160, 0, 0 }, newLibraryRoot.Mw3Words);
            surface.Extras![WorldGltf.Key]!.AsObject().Remove("mw3Flags");
            surface.Extras[WorldGltf.Key]!["mw3Cycle"] = new JsonObject { ["loop"] = true, ["speed"] = 2.0, ["textures"] = new JsonArray("new", "frame1") };
            File.WriteAllBytes(P("data/c1/textures/custom/frame1.png"), PngEncoder.Encode(new(1, 1, [0, 0, 255, 255]), token));
            var cycled = doc.Write("c1.bin", token); File.WriteAllBytes(P(model), Glb(cycled.Json, cycled.Binary));
            var cycleReport = await SourceBuilder.ExportAsync(root, second, ["c1/gamez.zbd", "c1/rtexture.zbd"], overwrite: true, token: token, target: ExportTarget.Mw3); Assert.Equal(2, cycleReport.Built);
            var cycleWorld = GameZWorldReader.FromDocument(await FormatRegistry.Default.OpenAsync(Path.Combine(second, "c1/gamez.zbd"), token), token);
            Assert.Contains(cycleWorld.Materials, m => m.Mw3Cycle is { Speed: 2, Loop: true } c && c.Textures.Select(t => t.Name).SequenceEqual(["new", "frame1"]));
            var cyclePack = await FormatRegistry.Default.OpenAsync(Path.Combine(second, "c1/rtexture.zbd"), token); Assert.Contains(cyclePack.Assets, a => a.Name == "frame1");
            File.WriteAllText(P("gamegen/c1.gs"), "Quit\n");
            var independent = await SourceBuilder.CheckAsync(root, ["reader.zbd"], token: token, target: ExportTarget.Mw3); Assert.Equal(1, independent.Built); Assert.Equal(0, independent.Failed);
            Assert.Equal(TexturePackKind.Hardware, SourceBuilder.Plan(root, token: token).Single(p => p.Path == "c1/rtexture.zbd").Pack!.Kind);
            Directory.CreateDirectory(P("data/common/mechs/textures/backup"));
            File.WriteAllBytes(P("data/common/mechs/textures/spare.png"), PngEncoder.Encode(new(1, 1, [255, 0, 0, 255]), token));
            File.WriteAllBytes(P("data/common/mechs/textures/backup/spare.png"), PngEncoder.Encode(new(1, 1, [0, 255, 0, 255]), token));
            var textureCollision = await SourceBuilder.CheckAsync(root, ["rmechtex.zbd"], token: token, target: ExportTarget.Mw3);
            Assert.Equal(1, textureCollision.Failed); Assert.Contains("same library name", textureCollision.Outputs.Single().Error!);
            File.Copy(P("data/common/mechs/models/toy0.gltf"), P("data/common/mechs/models/duplicate.gltf"));
            var collision = await SourceBuilder.CheckAsync(root, ["mechlib.zbd"], token: token, target: ExportTarget.Mw3); Assert.Equal(1, collision.Failed); Assert.Contains("both name mech library member", collision.Outputs.Single().Error!);
            Directory.CreateDirectory(P("data/common/mechs/motions/backup"));
            const string sampled = "MOTION ( LOOP_SECONDS ( 1 ) FRAME_COUNT ( 1 ) PART ( NAME ( arm ) SAMPLES ( ( 0 0 0 1 0 0 0 ) ( 1 2 3 1 0 0 0 ) ) ) )";
            File.WriteAllText(P("data/common/mechs/motions/gait.zan"), sampled);
            File.WriteAllText(P("data/common/mechs/motions/backup/GAIT.zan"), sampled.Replace("1 2 3", "4 5 6", StringComparison.Ordinal));
            var motionCollision = await SourceBuilder.CheckAsync(root, ["motion.zbd"], token: token, target: ExportTarget.Mw3);
            Assert.Equal(1, motionCollision.Failed); Assert.Contains("same motion member name", motionCollision.Outputs.Single().Error!);
            string P(string path) => SourceProject.Resolve(root, path);
        }
        finally { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
    }

    private static byte[] Glb(byte[] json, byte[] binary)
    {
        var tree = JsonNode.Parse(json)!;
        foreach (var buffer in tree["buffers"]?.AsArray() ?? []) buffer!.AsObject().Remove("uri");
        byte[] body = Encoding.UTF8.GetBytes(tree.ToJsonString()); int jsonLength = (body.Length + 3) & ~3, binaryLength = (binary.Length + 3) & ~3;
        byte[] result = new byte[28 + jsonLength + binaryLength]; "glTF"u8.CopyTo(result);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), 2); BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8), result.Length);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(12), jsonLength); BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16), 0x4E4F534A);
        result.AsSpan(20, jsonLength).Fill((byte)' '); body.CopyTo(result, 20);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(20 + jsonLength), binaryLength); BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(24 + jsonLength), 0x004E4942);
        binary.CopyTo(result, 28 + jsonLength); return result;
    }

    [Fact]
    public void SampledMotionSourcesPreserveSamplesAndCompileChangedPoses()
    {
        var token = TestContext.Current.CancellationToken;
        byte[] text = Encoding.ASCII.GetBytes("MOTION ( LOOP_SECONDS ( 1.0 ) FRAME_COUNT ( 1 ) PART ( NAME ( arm ) SAMPLES ( ( 0 0 0 1 0 0 0 ) ( 1 2 3 1 0 0 0 ) ) ) )");
        var bytes = Mw3LibrarySources.BuildMotion(text, token); var clip = MotionClip.Read(bytes, token);
        Assert.Equal(bytes, Mw3LibrarySources.BuildMotion(Mw3LibrarySources.MotionText(clip, token), token));
        var edited = Mw3LibrarySources.BuildMotion(Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(text).Replace("1 2 3", "4 5 6", StringComparison.Ordinal)), token);
        Assert.Equal(new Vector3(4, 5, 6), MotionClip.Read(edited, token).Parts[0].Frames[1].Translation);
    }

    [Fact]
    public void OldRecordRecipeProjectsAreRefusedWithReconstructionGuidance()
    {
        string root = Path.Combine(Path.GetTempPath(), "zstudio-old-mw3-" + Guid.NewGuid().ToString("N"));
        try { Directory.CreateDirectory(Path.Combine(root, "data")); Directory.CreateDirectory(Path.Combine(root, "gamegen")); File.WriteAllText(Path.Combine(root, "gamegen", "mw3.project.json"), "{}"); Assert.Contains("Reconstruct", Assert.Throws<InvalidDataException>(() => SourceBuilder.Plan(root, token: TestContext.Current.CancellationToken)).Message); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task OptionalRetailCorpusProducesNativeSourcesAndEveryOutput()
    {
        string? corpus = Environment.GetEnvironmentVariable("ZSTUDIO_MW3_CORPUS"); if (corpus == null) return;
        var token = TestContext.Current.CancellationToken; string scratch = Path.Combine(Path.GetTempPath(), "zstudio-native-mw3-corpus-" + Guid.NewGuid().ToString("N"));
        try
        {
            var before = Directory.GetFiles(corpus, "*.zbd", SearchOption.AllDirectories).ToDictionary(p => p, p => SourceProject.Sha256(File.ReadAllBytes(p)));
            string root = Path.Combine(scratch, "source"), output = Path.Combine(scratch, "export");
            var recovered = await SourceExtractor.ExtractAsync(corpus, root, token: token); Assert.Empty(recovered.NotReconstructed);
            Assert.NotEmpty(Directory.GetFiles(root, "*.zad", SearchOption.AllDirectories)); Assert.NotEmpty(Directory.GetFiles(root, "*.zan", SearchOption.AllDirectories)); Assert.Empty(Directory.GetFiles(root, "*.json", SearchOption.AllDirectories));
            var report = await SourceBuilder.ExportAsync(root, output, token: token, target: ExportTarget.Mw3); Assert.Equal(0, report.Failed); Assert.Equal(before.Count, report.Built);
            var oldDefinitions = ZrdDecoder.Read(ArchiveSources.Read(File.ReadAllBytes(Path.Combine(corpus, "reader.zbd"))).Single(m => m.Name == "sounds.zrd").Payload, token);
            var newDefinitions = ZrdText.Parse(File.ReadAllBytes(Path.Combine(root, SourceBuilder.SoundDefinitions)), token);
            Assert.Equal(WithoutWaveFilenames(oldDefinitions, token), WithoutWaveFilenames(newDefinitions, token));
            var declared = AnimationPreviewContext.ReadSoundAliases(oldDefinitions, token).ToArray();
            var renamed = AnimationPreviewContext.ReadSoundAliases(newDefinitions, token).ToArray();
            var firstNames = declared.GroupBy(a => a.File, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Name + ".wav", StringComparer.OrdinalIgnoreCase);
            var oldSounds = ArchiveSources.Read(File.ReadAllBytes(Path.Combine(corpus, "soundsH.zbd"))).DistinctBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
            var newSounds = ArchiveSources.Read(File.ReadAllBytes(Path.Combine(output, "soundsH.zbd"))).ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var sound in oldSounds.Values)
            {
                string name = firstNames.GetValueOrDefault(sound.Name, sound.Name);
                Assert.Equal(sound.Payload.ToArray(), File.ReadAllBytes(Path.Combine(root, SourceBuilder.SoundsFolder, name)));
                var actual = newSounds[name].Payload; var expectedFormat = WaveDecoder.Read(sound.Payload, token); var actualFormat = WaveDecoder.Read(actual, token);
                Assert.Equal((expectedFormat.Encoding, expectedFormat.Channels, expectedFormat.SampleRate, expectedFormat.BitsPerSample), (actualFormat.Encoding, actualFormat.Channels, actualFormat.SampleRate, actualFormat.BitsPerSample));
                Assert.Equal(sound.Payload.Slice(expectedFormat.DataOffset, expectedFormat.DataLength).ToArray(), actual.Slice(actualFormat.DataOffset, actualFormat.DataLength).ToArray());
            }
            foreach (var pair in declared.Zip(renamed))
                Assert.Equal(oldSounds.ContainsKey(pair.First.File) ? firstNames[pair.First.File] : pair.First.File, pair.Second.File);
            foreach (var pair in before)
            {
                string relative = Path.GetRelativePath(corpus, pair.Key); var rebuilt = await FormatRegistry.Default.OpenAsync(Path.Combine(output, relative), token);
                Assert.DoesNotContain(rebuilt.Diagnostics, d => d.Severity == "Error"); Assert.Equal(pair.Value, SourceProject.Sha256(File.ReadAllBytes(pair.Key)));
                var original = await FormatRegistry.Default.OpenAsync(pair.Key, token);
                if (original.Animations is { } animations)
                {
                    Assert.Equal(animations.Entries.Count, rebuilt.Animations!.Entries.Count);
                    foreach (var entries in animations.Entries.Zip(rebuilt.Animations.Entries)) Assert.Null(Mw3AnimationSources.Difference(entries.First, entries.Second, token));
                }
                if (original.Probe.Family == FormatFamily.TexturePack)
                {
                    var textures = rebuilt.Assets.ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);
                    foreach (var asset in original.Assets)
                    {
                        Assert.True(textures.TryGetValue(asset.Name, out var exported), relative + ": " + asset.Name);
                        var expected = TextureDecoder.Decode(original, asset, token); var actual = TextureDecoder.Decode(rebuilt, exported!, token);
                        Assert.Equal(BinaryPrimitives.ReadUInt16LittleEndian(original.Bytes.Span[((int)asset.Offset + 14)..]) & 3,
                            BinaryPrimitives.ReadUInt16LittleEndian(rebuilt.Bytes.Span[((int)exported!.Offset + 14)..]) & 3);
                        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
                        for (int i = 0; i < expected.Rgba.Length; i += 4)
                        {
                            Assert.Equal(expected.Rgba[i + 3], actual.Rgba[i + 3]);
                            if (expected.Rgba[i + 3] != 0) Assert.True(expected.Rgba.AsSpan(i, 3).SequenceEqual(actual.Rgba.AsSpan(i, 3)), relative + ": " + asset.Name);
                        }
                    }
                }
                if (original.Probe.Family == FormatFamily.GameZ)
                {
                    var expected = GameZWorldReader.FromDocument(original, token); var actual = GameZWorldReader.FromDocument(rebuilt, token);
                    Assert.Equal(expected.Nodes.Count, actual.Nodes.Count);
                    var nodes = actual.Nodes.GroupBy(n => n.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => new Queue<WorldNode>(g), StringComparer.Ordinal);
                    foreach (var node in expected.Nodes)
                    {
                        Assert.True(nodes.TryGetValue(node.Name, out var candidates) && candidates.Count != 0, relative + ": " + node.Name);
                        var compiled = candidates!.Dequeue(); Assert.Equal(node.Mw3Words, compiled.Mw3Words);
                    }
                    // Slot creation order can reorder identical named copies. Compare the complete edge multiset.
                    Assert.Equal(expected.Nodes.SelectMany(n => n.Parents.Select(p => (Child: n.Name, Parent: p.Name))).Order(),
                        actual.Nodes.SelectMany(n => n.Parents.Select(p => (Child: n.Name, Parent: p.Name))).Order());
                }
            }
        }
        finally { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
    }
}
