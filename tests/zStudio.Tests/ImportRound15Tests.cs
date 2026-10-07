using System.Numerics;
using System.Text;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ImportRound15Tests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public void SoundDeclarationsAtAcceptedDepthKeepTheirCeilings()
    {
        string row = "( 1 a.wav HIGH ( 22050 16 1 ) MED ( 22050 8 1 ) LOW ( 11025 8 1 ) )";
        byte[] text = Encoding.ASCII.GetBytes(new string('(', 80) + row + new string(')', 80));
        var formats = SourceBuilder.DeclaredFormats(text, Token);
        Assert.Equal(new WaveFormat(11025, 8, 1), formats["a.wav"][2]);
        byte[] binary = ZrdWriter.Write(ZrdText.Parse(Encoding.ASCII.GetString(text), Token), Token);
        Assert.Equal(formats["a.wav"], SourceBuilder.DeclaredFormats(binary, Token)["a.wav"]);
    }

    [Fact]
    public async Task DeepSoundDeclarationsReachTheExportedBanks()
    {
        using SourceFixture fixture = new();
        await SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, token: Token);
        string path = Path.Combine(fixture.Project, "data/common/zrdr/sounds.zrd");
        string text = File.ReadAllText(path);
        File.WriteAllText(path, new string('(', 80) + text + new string(')', 80));
        string output = Path.Combine(fixture.Root, "export");
        await SourceBuilder.ExportAsync(fixture.Project, output, token: Token);
        foreach (var (bank, expected) in new[] { ("soundsm.zbd", SourceFixture.Medium), ("soundsl.zbd", SourceFixture.Low) })
        {
            var wave = ArchiveSources.Read(File.ReadAllBytes(Path.Combine(output, bank))).Single(m => m.Name == "a.wav").Payload;
            Assert.Equal(expected, WaveConverter.Format(wave));
        }
    }

    [Fact]
    public void DeepRecordedImagePathsAndAnimationListsRetainTheirMeaning()
    {
        string Wrap(string value) => new string('(', 80) + value + new string(')', 80);
        var image = ZrdText.Parse(Wrap("IMAGE_PATH ( \"../data/common/images/custom\" ) BACKGROUND ( icon )"), Token);
        Assert.Equal("data/common/images/custom", Assert.Single(TextureSources.PlaceImages(["icon"], [("zrdr.zbd", "dialog.zrd", image)])));
        byte[] definitions = Encoding.ASCII.GetBytes(Wrap("ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION_FILE ( old.zad ) ) )"));
        string added = Encoding.ASCII.GetString(SourceWorlds.AddDefinitionFiles(definitions, ["data/m1/zrdr/new.zad"], Token));
        Assert.Contains("new.zad", added); Assert.Contains("old.zad", added);
        var split = AnimationDefinitionSet.Split(ZrdText.Parse("ANIMATION_DEFINITIONS ( " + Wrap("ANIMATION_DEFINITION_FILE ( old.zrd )") + " )", Token));
        string renamed = ZrdText.Write(split.Definitions, Token); Assert.Contains("old.zad", renamed); Assert.DoesNotContain("old.zrd", renamed);
    }

    private static GltfMesh TriangleMesh(float x = 0, float z = 0, float size = 1)
    {
        GltfPrimitive p = new(); p.Positions.AddRange([new(x, 0, z), new(x + size, 0, z), new(x, 0, z + size)]); p.Indices.AddRange([0, 1, 2]);
        GltfMesh mesh = new(); mesh.Primitives.Add(p); return mesh;
    }

    [Fact]
    public void ConversionBudgetsGeometryBeforeAllocationAndChecksCancellationDuringComparison()
    {
        var mesh = TriangleMesh(); mesh.Primitives[0].Indices.AddRange(Enumerable.Repeat(new[] { 0, 1, 2 }, 100_000).SelectMany(i => i));
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Contains("triangle budget", Assert.Throws<InvalidDataException>(() => new TerrainConversionGeometry(Token, maximumTriangles: 100).Read(mesh)).Message);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 100_000);
        TerrainConversionGeometry geometry = new(Token, maximumWork: 2);
        var a = geometry.Read(TriangleMesh()); var b = geometry.Read(TriangleMesh());
        Assert.Contains("comparison budget", Assert.Throws<InvalidDataException>(() => geometry.Overlaps(a, b)).Message);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        TerrainConversionGeometry stopping = new(canceled.Token);
        a = stopping.Read(TriangleMesh()); b = stopping.Read(TriangleMesh()); canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => stopping.Overlaps(a, b));
    }

    [Fact]
    public void ConversionDistinguishesPositiveAreaFromTouchingOrDisjointTriangles()
    {
        TerrainConversionGeometry geometry = new(Token);
        var a = geometry.Read(TriangleMesh());
        Assert.True(geometry.Overlaps(a, geometry.Read(TriangleMesh(0.49999f, 0.49999f))));
        Assert.False(geometry.Overlaps(a, geometry.Read(TriangleMesh(0.5f, 0.5f))));
        Assert.False(geometry.Overlaps(a, geometry.Read(TriangleMesh(0.75f, 0.75f)))); // AABBs overlap, triangles do not.
        Assert.True(geometry.Overlaps(geometry.Read(TriangleMesh(size: 1e-12f)), geometry.Read(TriangleMesh(size: 1e-12f))));
    }

    [Fact]
    public void AnimationWildcardBudgetIsSharedAndRetainsDigitOrdering()
    {
        AnimationItem Definition(string name) => new("ANIMATION_DEFINITION", ZrdText.Parse("NAME ( \"" + name + "\" )", Token), "fixture");
        AnimationRoots roots = new(["a12", "a11", "a11", "aXY", "a1", "a123"], Token, maximumWork: 20);
        Assert.Equal(new[] { ("a11", "11"), ("a12", "12") }, roots.Resolve(Definition("a**"), _ => { }).ToArray());
        Assert.Throws<InvalidDataException>(() => roots.Resolve(Definition("b**"), _ => { }).ToArray());
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var stopping = new AnimationRoots(["a11"], canceled.Token); canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => stopping.Resolve(Definition("a**"), _ => { }).ToArray());
    }

    [Fact]
    public void ConversionBoundsTheNodeTimesPatternProduct()
    {
        using SourceWorldFixture fixture = new();
        GltfDocument doc = new(); var mesh = TriangleMesh();
        for (int i = 0; i < 500; i++) doc.Roots.Add(new() { Name = "terrain" + i, Mesh = mesh });
        var (json, bin) = doc.Write("m1.bin"); fixture.Write("data/m1/models/m1.gltf", json); fixture.Write("data/m1/models/m1.bin", bin);
        var patterns = Enumerable.Range(0, 4096).Select(i => new System.Text.RegularExpressions.Regex("^missing" + i + "[0-9]$")).ToArray();
        var error = Assert.Throws<InvalidDataException>(() => SourceTerrainConversion.Plan(new(fixture.Project), "data/m1/models/m1.gltf", (new(StringComparer.Ordinal), patterns), Token));
        Assert.Contains("wildcard matching budget", error.Message);
    }

    [Theory]
    [InlineData(0.1f)]
    [InlineData(0.0001f)]
    public void PositivePlanViewOverlapAlwaysKeepsSheetsSeparate(float size)
    {
        using SourceWorldFixture fixture = new();
        var document = new Recoil.Zbd.Core.Gltf.GltfDocument();
        foreach (float y in new[] { 0f, 1f })
        {
            var primitive = new Recoil.Zbd.Core.Gltf.GltfPrimitive();
            primitive.Positions.AddRange([new(0, y, 0), new(size, y, 0), new(0, y, size)]);
            primitive.Indices.AddRange([0, 1, 2]);
            var mesh = new Recoil.Zbd.Core.Gltf.GltfMesh(); mesh.Primitives.Add(primitive);
            document.Roots.Add(new() { Name = "sheet" + y, Mesh = mesh });
        }
        var (json, bin) = document.Write("m1.bin");
        fixture.Write("data/m1/models/m1.gltf", json); fixture.Write("data/m1/models/m1.bin", bin);
        var plan = SourceTerrainConversion.Plan(new(fixture.Project), "data/m1/models/m1.gltf", (new(StringComparer.Ordinal), []), Token);
        Assert.Equal(2, plan.Groups.Count);
    }
}
