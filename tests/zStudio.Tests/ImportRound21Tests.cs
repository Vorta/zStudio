using System.Buffers.Binary;
using System.Text;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ImportRound21Tests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private sealed class Files(string text) : IProjectFiles
    {
        private readonly byte[] ownedInput = Encoding.UTF8.GetBytes(text);
        public bool Exists(string relative) => relative == "gamegen/test.gs";
        public byte[] Read(string relative, CancellationToken token) => Read(relative, token, ProjectReadLimits.Document);
        public byte[] Read(string relative, CancellationToken token, ProjectReadLimits limits) { token.ThrowIfCancellationRequested(); byte[] result = ownedInput; limits.Validate(result); return result; }
    }

    [Theory]
    [InlineData("Object3DSetActive off")][InlineData("Object3DSetPriority 2")][InlineData("NewLOD detail")]
    [InlineData("MatlNew material")][InlineData("ModelPolygonVertex 1 2 3")][InlineData("SEQAddChild child")]
    [InlineData("Object3DSetOpacitySuffix 0.5")][InlineData("WindowSetClearPolygon 1")]
    public void IgnoredRetailMutationsAreReported(string command)
    {
        WorldAssembler assembler = new(new Files($"NewWorld world\nNewObject3D object\n{command}\nGameZWriteZBDFile gamez.zbd"),Token);
        assembler.Assemble("test.gs");
        Assert.Contains(assembler.Warnings,w=>w.Contains(ScriptCommands.Core(command.Split(' ')[0]),StringComparison.Ordinal)&&w.Contains("does not apply",StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(-1)][InlineData(-2)][InlineData(1)][InlineData(int.MaxValue)]
    public void TexturedMaterialRequiresAValidDirectoryIndex(int index)
    {
        GameZWorld world = new() { MaterialCapacity = 4, ModelCapacity = 4, NodeCapacity = 16 };
        WorldTexture texture = new("stone");world.Textures.Add(texture);world.Materials.Add(new() { Texture=texture });world.Nodes.Add(new("world",WorldNodeClass.World));
        byte[] bytes=GameZWriter.Write(world,Token);int offset=BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16))+16;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset+16),index);
        var doc=FormatRegistry.Default.OpenBytes("world.zbd",bytes,token:Token);
        Assert.Contains("texture",Assert.Throws<InvalidDataException>(()=>GameZWorldReader.FromDocument(doc,Token)).Message,StringComparison.OrdinalIgnoreCase);
        // The same word is inactive when the textured bit is absent and must remain harmless.
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset),0);
        Assert.Null(GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("world.zbd",bytes,token:Token),Token).Materials[0].Texture);
    }

    [Theory]
    [InlineData("")][InlineData(",\"scene\":null")]
    public void MultipleScenesNeedAnExplicitSelectionBeforeResolvingBuffers(string selection)
    {
        string json="{\"asset\":{\"version\":\"2.0\"},\"nodes\":[{\"name\":\"one\"},{\"name\":\"two\"}],\"scenes\":[{\"nodes\":[0]},{\"nodes\":[1]}],\"buffers\":[{\"uri\":\"unused.bin\",\"byteLength\":1}]"+selection+"}";
        int reads=0;
        Assert.Contains("scene",Assert.Throws<InvalidDataException>(()=>GltfDocument.Read(Encoding.UTF8.GetBytes(json),_=>{reads++;return [0];},Token)).Message,StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0,reads);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void AggregateMissionInputsAreBounded(bool animations)
    {
        string root=Directory.CreateTempSubdirectory("zstudio-plan-bound-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root,"gamegen"));Directory.CreateDirectory(Path.Combine(root,"data"));
            List<string> added = [..Enumerable.Range(0,40_000).Select(i=>$"data/common/{i}.{(animations?"zan":"gltf")}")];
            for(int i=1;i<=30;i++)
            {
                Directory.CreateDirectory(Path.Combine(root,"data",$"m{i}"));
                added.Add(animations?$"data/m{i}/zrdr/anim.zad":$"gamegen/m{i}.gs");
            }
            Assert.Contains("inputs",Assert.Throws<InvalidDataException>(()=>SourceBuilder.Plan(root,added,automaticPacks:false,token:Token)).Message,StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(root,true); }
    }
    [Theory]
    [InlineData(false, -2)][InlineData(false, 0)][InlineData(false, int.MaxValue)]
    [InlineData(true, -2)][InlineData(true, 0)][InlineData(true, int.MaxValue)]
    public void InvalidWorldReferencesCannotDisappearDuringReconstruction(bool model, int index)
    {
        GameZWorld world = new() { MaterialCapacity = 4, ModelCapacity = 4, NodeCapacity = 16 };
        world.Nodes.Add(new("world", WorldNodeClass.World));
        if (!model)
        {
            WorldModel mesh = new(); mesh.Vertices.AddRange([new(0,0,0),new(1,0,0),new(0,1,0)]);
            mesh.Polygons.Add(new() { Vertices = [0,1,2] }); world.Models.Add(mesh);
        }
        byte[] bytes = GameZWriter.Write(world, Token);
        var doc = FormatRegistry.Default.OpenBytes("world.zbd", bytes, token: Token);
        int offset = model ? (int)doc.Assets.Single(a => a.Kind == AssetKind.Node).Offset + 60
            : (int)doc.Assets.Single(a => a.Kind == AssetKind.Model).Offset + 36 + 20;
        // -1 remains the valid optional-reference sentinel in both records.
        Assert.NotNull(GameZWorldReader.FromDocument(doc, Token));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset), index);
        Assert.Contains(model ? "model index" : "material index", Assert.Throws<InvalidDataException>(() =>
            GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("world.zbd", bytes, token: Token), Token)).Message);
    }

    [Theory]
    [InlineData("\"scenes\":[{\"nodes\":[0]},{\"nodes\":[1]}],\"scene\":1", "two")]
    [InlineData("\"scenes\":[{\"nodes\":[0]}]", "one")]
    public void UnambiguousSceneSelectionRetainsItsAuthoredRoot(string scenes, string expected)
    {
        var doc = GltfDocument.Read(Encoding.UTF8.GetBytes("{\"asset\":{\"version\":\"2.0\"},\"nodes\":[{\"name\":\"one\"},{\"name\":\"two\"}]," + scenes + "}"), _ => [], Token);
        Assert.Equal(expected, Assert.Single(doc.Roots).Name);
    }

    [Fact]
    public void PendingSourcesObeyTheSameFileLimitAsDiskSources()
    {
        string root = Directory.CreateTempSubdirectory("zstudio-plan-overlay-").FullName;
        try
        {
            var added = Enumerable.Range(0, SourceProject.MaximumFiles + 1).Select(i => $"data/common/{i}.gltf").ToArray();
            Assert.Throws<IOException>(() => SourceProject.Files(root, "data", _ => true, added, Token));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AdditionalMissionsDoNotCopyTheSharedModelInventory()
    {
        string root = Directory.CreateTempSubdirectory("zstudio-plan-allocation-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "gamegen")); Directory.CreateDirectory(Path.Combine(root, "data", "m1"));
            List<string> added = [.. Enumerable.Range(0, 40_000).Select(i => $"data/common/{i}.gltf"), "gamegen/m1.gs"];
            long Measure(out IReadOnlyList<SourceOutputPlan> plans)
            {
                long start = GC.GetAllocatedBytesForCurrentThread();
                plans = SourceBuilder.Plan(root, added, automaticPacks: false, token: Token);
                return GC.GetAllocatedBytesForCurrentThread() - start;
            }
            Measure(out _); long one = Measure(out _);
            for (int i = 2; i <= 20; i++) { Directory.CreateDirectory(Path.Combine(root, "data", $"m{i}")); added.Add($"gamegen/m{i}.gs"); }
            long many = Measure(out var plans);
            Assert.Equal(20, plans.Count(p => p.Family == "world"));
            var last = plans.Last(p => p.Family == "world"); Assert.Equal(40_001, last.Inputs.Count);
            Assert.Equal("gamegen/m20.gs", last.Inputs[0]); Assert.Equal(40_001, last.Inputs.Count());
            // Copying nineteen 40,000-item arrays alone costs over 6 MiB, independent of parsing/scanning.
            Assert.True(many - one < 2_000_000, $"Additional mission plans allocated {many - one:N0} bytes.");
        }
        finally { Directory.Delete(root, true); }
    }

}
