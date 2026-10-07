using System.Buffers.Binary;
using System.Text;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ImportRound20Tests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static GameZWorld Textured(bool second, bool reorder = false)
    {
        GameZWorld world = new();
        WorldTexture a = new("same") { State = 1 }, b = new("same") { State = 2 }, other = new("other");
        world.Textures.AddRange(reorder ? [other,a,b] : [a,b,other]);
        WorldMaterial material = new() { Texture = second ? b : a };
        WorldModel model = new(); model.Vertices.AddRange([new(0,0,0),new(1,0,0),new(0,0,1)]);
        model.Polygons.Add(new() { Material = material, Vertices = [0,1,2] });
        WorldNode root = new("world",WorldNodeClass.World), node = new("object",WorldNodeClass.Object3D) { Model = model };
        root.Children.Add(node); node.Parents.Add(root); world.Nodes.AddRange([root,node]); world.Models.Add(model); world.Materials.Add(material);
        return world;
    }

    [Fact]
    public void MaterialTextureUsesDirectoryOccurrence()
    {
        Assert.Equal(0,WorldComparer.CompareTree(Textured(false),Textured(false,true),token:Token).DifferenceCount);
        var difference = Assert.Single(WorldComparer.CompareTree(Textured(false),Textured(true),token:Token).Differences);
        Assert.Equal("model.polygons",difference.Field);
        Assert.Contains("same [1]",difference.Expected); Assert.Contains("same [2]",difference.Actual);
    }

    [Fact]
    public void SharedModelStillUsesEachWorldsTextureDirectory()
    {
        var a = Textured(false); var b = Textured(false);
        b.Textures.Clear(); b.Textures.AddRange([a.Textures[1],a.Textures[0],a.Textures[2]]);
        b.Nodes[1].Model = a.Nodes[1].Model;
        Assert.Contains(WorldComparer.CompareTree(a,b,token:Token).Differences,d=>d.Field=="model.polygons");
    }

    [Fact]
    public void SameWorldCopiesDoNotCollapseDifferentTextureReferences()
    {
        var world = Textured(false); var a = world.Nodes[1]; var b = Textured(true).Nodes[1];
        b.Model!.Polygons[0].Material!.Texture = world.Textures[1];
        b.Parents.Clear(); b.Parents.Add(world.Nodes[0]);
        Assert.False(WorldComparer.Interchangeable(a,b,token:Token));
        b.Model.Polygons[0].Material!.Texture = world.Textures[0];
        Assert.True(WorldComparer.Interchangeable(a,b,token:Token));
    }

    [Fact]
    public void RenamedPrimaryWorldIsReported()
    {
        GameZWorld a = new(), b = new(); a.Nodes.Add(new("first",WorldNodeClass.World)); b.Nodes.Add(new("second",WorldNodeClass.World));
        var d = Assert.Single(WorldComparer.CompareTree(a,b,token:Token).Differences);
        Assert.Equal("name",d.Field); Assert.Equal("first",d.Expected); Assert.Equal("second",d.Actual);
    }

    private sealed class Files(string text) : IProjectFiles
    {
        public bool Exists(string relative) => relative == "gamegen/test.gs";
        public byte[] Read(string relative,CancellationToken token) => Encoding.UTF8.GetBytes(text);
    }

    [Theory]
    [InlineData("CameraSetHorizonXZ")][InlineData("CameraSetHorizonXZsuffix")]
    public void SourceCameraKeepsBothHorizonBindingsAndProvenance(string command)
    {
        WorldAssembler assembler = new(new Files($"NewWorld world\nNewObject3D sky\nNewObject3D skyXZ\nNewCamera camera\nCameraSetHorizon sky\n{command} skyXZ\nGameZWriteZBDFile gamez.zbd"),Token);
        var world = assembler.Assemble("test.gs"); var camera = world.Nodes.Single(n=>n.Name=="camera");
        Assert.Same(world.Nodes.Single(n=>n.Name=="sky"),camera.CameraHorizon);
        Assert.Same(world.Nodes.Single(n=>n.Name=="skyXZ"),camera.CameraHorizonXZ);
        Assert.Contains(assembler.Provenance[camera].Applied,i=>i.Command=="CameraSetHorizonXZ");
        var reopened = GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd",GameZWriter.Write(world,Token),token:Token),Token);
        Assert.Equal("skyXZ",reopened.Nodes.Single(n=>n.Name=="camera").CameraHorizonXZ?.Name);
    }

    [Fact]
    public void HorizonLookupUsesObjectClassAndRemovedBindingsAreCleared()
    {
        var source = "NewWorld world\nNewObject3D sky\nNewWorld sky\nNewCamera camera\nCameraSetHorizon sky\nCameraSetHorizonXZ sky\n";
        var world = new WorldAssembler(new Files(source+"GameZWriteZBDFile gamez.zbd"),Token).Assemble("test.gs");
        var camera = world.Nodes.Single(n=>n.Name=="camera");
        Assert.Equal(WorldNodeClass.Object3D,camera.CameraHorizon!.Class); Assert.Same(camera.CameraHorizon,camera.CameraHorizonXZ);
        var deleted = new WorldAssembler(new Files("NewWorld world\nNewObject3D sky\nNewCamera camera\nCameraSetHorizonXZ sky\nDeleteTree sky\nGameZWriteZBDFile gamez.zbd"),Token).Assemble("test.gs");
        Assert.Null(deleted.Nodes.Single(n=>n.Name=="camera").CameraHorizonXZ);
    }

    [Theory]
    [InlineData("CameraTranslate 1 2 3")][InlineData("CameraRotate 1 2 3")]
    [InlineData("CameraSetActive off")][InlineData("CameraSetNearClip 2")][InlineData("CameraSetFarClip 5")]
    public void UnimplementedCameraMutationsAreReported(string command)
    {
        WorldAssembler assembler = new(new Files($"NewWorld world\nNewCamera camera\n{command}\nGameZWriteZBDFile gamez.zbd"),Token);
        assembler.Assemble("test.gs");
        Assert.Contains(assembler.Warnings,w=>w.Contains(command.Split(' ')[0],StringComparison.Ordinal)&&w.Contains("does not apply",StringComparison.Ordinal));
    }

    [Fact]
    public void AutomaticTierPlanObservesInPlaceDimensionChanges()
    {
        using SourceWorldFixture fixture = new();
        byte[] header = [137,80,78,71,13,10,26,10,0,0,0,13,(byte)'I',(byte)'H',(byte)'D',(byte)'R',0,0,0,64,0,0,0,64];
        for(int i=0;i<9;i++) fixture.Write($"data/m1/textures/grow{i}.png",header);
        Assert.DoesNotContain(SourceBuilder.Plan(fixture.Project,profile:BuildProfiles.Modern,token:Token),p=>p.Path=="m1/rtexture32.zbd");
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(16),1024); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(20),1024);
        for(int i=0;i<9;i++)
        {
            string file=SourceProject.Resolve(fixture.Project,$"data/m1/textures/grow{i}.png"); var written=File.GetLastWriteTimeUtc(file);var created=File.GetCreationTimeUtc(file);
            File.WriteAllBytes(file,header);File.SetCreationTimeUtc(file,created);File.SetLastWriteTimeUtc(file,written);
        }
        Assert.Contains(SourceBuilder.Plan(fixture.Project,profile:BuildProfiles.Modern,token:Token),p=>p.Path=="m1/rtexture32.zbd"&&p.Automatic);
    }

    [Fact]
    public void TexturePlanningReadsChangedHeaderWithIdenticalFileMetadata()
    {
        string folder = Directory.CreateTempSubdirectory("zstudio-round20-").FullName;
        try
        {
            string path = Path.Combine(folder,"image.png");
            byte[] header = [137,80,78,71,13,10,26,10,0,0,0,13,(byte)'I',(byte)'H',(byte)'D',(byte)'R',0,0,0,64,0,0,0,64];
            File.WriteAllBytes(path,header); var written = File.GetLastWriteTimeUtc(path); var created = File.GetCreationTimeUtc(path);
            var variant = BuildProfiles.Modern.TexturePacks.Single(p=>p.Automatic).Variant;
            Assert.Equal(2L*64*64,SourceBuilder.TextureMemory(folder,["image.png"],variant,Token));
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(16),128); File.WriteAllBytes(path,header);
            File.SetCreationTimeUtc(path,created); File.SetLastWriteTimeUtc(path,written);
            Assert.Equal(2L*128*64,SourceBuilder.TextureMemory(folder,["image.png"],variant,Token));
            header[0]=0; File.WriteAllBytes(path,header); File.SetCreationTimeUtc(path,created); File.SetLastWriteTimeUtc(path,written);
            Assert.Null(TextureSources.PngSize(path));
        }
        finally { Directory.Delete(folder,true); }
    }
}
