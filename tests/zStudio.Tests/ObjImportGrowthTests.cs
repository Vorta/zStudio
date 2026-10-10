using System.Numerics;
using System.Text.Json;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Export;
using Xunit;

namespace Recoil.Zbd.Tests;

[Collection("Allocation-sensitive")]
public sealed class ObjImportGrowthTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Triangle = "v 0 0 0\nv 1 0 0\nv 0 1 0\nvt 0 0\nvt 1 0\nvt 0 1\nvn 0 0 1\nf 1/1/1 2/2/1 3/3/1\n";

    [Fact]
    public void OperandSpansPreserveColorsIndicesCommentsAndLineEndings()
    {
        string text = "# heading\r\no two words\r\ng anything\ns off\nusemtl material\n" +
            "v 0 0 0 1 0 0#red\nv 1 0 0\nv 0 1 0 0 0 1\n" +
            "vt 0 0 99\nvt 1 0\nvt 0 1\nvn 0 0 2\nf -3/-3/-1 -2/-2/-1 -1/-1/-1#triangle\n";
        var mesh = ModelImport.ReadObj(text, Token);
        Assert.Equal([Vector3.Zero, Vector3.UnitX, Vector3.UnitY], mesh.Positions);
        Assert.Equal([Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ], mesh.Normals);
        Assert.Equal([new Vector2(0, 1), new Vector2(1, 1), Vector2.Zero], mesh.Uvs);
        Assert.Equal([Vector3.UnitX, Vector3.One, Vector3.UnitZ], mesh.Colors);
        Assert.Equal([0, 1, 2], mesh.Triangles);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BlankLinesAndIgnoredOperandsDoNotAllocatePerLineOrWord(bool ignored)
    {
        string expanded = (ignored ? "o " + string.Concat(Enumerable.Repeat("word ", 4096)) + "\n" : new string('\n', 4096)) + Triangle;
        _ = ModelImport.ReadObj(Triangle, Token); _ = ModelImport.ReadObj(expanded, Token);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var baseline = ModelImport.ReadObj(Triangle, Token);
        long small = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        var actual = ModelImport.ReadObj(expanded, Token);
        long grown = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(baseline.Positions, actual.Positions); Assert.Equal(baseline.Triangles, actual.Triangles);
        // Input strings are already owned. Old line/token arrays exceeded this allowance even for these small inputs.
        Assert.InRange(grown - small, -4096, 16 * 1024);
    }

    [Fact]
    public void CancellationIsObservedInsideOneLongIgnoredLineAndFreshRetryWorks()
    {
        string text = "o " + new string('a', 8192) + "\n" + Triangle;
        using CancellationTokenSource cancellation = new(); int visits = 0;
        Assert.Throws<OperationCanceledException>(() => ModelImport.ReadObj(text, cancellation.Token, phase =>
        { Assert.Equal("obj", phase); if (++visits == 3) cancellation.Cancel(); }));
        Assert.Equal(3, visits);
        Assert.Equal([0, 1, 2], ModelImport.ReadObj(text, Token).Triangles);
    }

    [Fact]
    public void CancellationAtEachPreparationBoundaryCannotReturnAPartialMesh()
    {
        int boundaries = 0; _ = ModelImport.ReadObj(Triangle, Token, _ => boundaries++);
        foreach (int stop in new[] { 1, 3, boundaries / 2, boundaries })
        {
            using CancellationTokenSource cancellation = new(); int visited = 0;
            Assert.Throws<OperationCanceledException>(() => ModelImport.ReadObj(Triangle, cancellation.Token, _ =>
            { if (++visited == stop) cancellation.Cancel(); }));
            Assert.Equal(stop, visited);
        }
        Assert.Equal([0, 1, 2], ModelImport.ReadObj(Triangle, Token).Triangles);
    }

    [Fact]
    public void RefusalsKeepLineIdentityWithoutCopyingLongAuthoredValues()
    {
        string unknown = "\n# comment\n" + new string('x', 8192);
        var directive = Assert.Throws<InvalidDataException>(() => ModelImport.ReadObj(unknown, Token));
        Assert.StartsWith("OBJ line 3: Unsupported OBJ directive ", directive.Message);
        Assert.Contains("…", directive.Message); Assert.True(directive.Message.Length < 256);
        var numeric = Assert.Throws<InvalidDataException>(() => ModelImport.ReadObj("v " + new string('x', 8192) + " 0 0", Token));
        Assert.Equal("OBJ line 1: Invalid numeric value.", numeric.Message);
        var face = Assert.Throws<InvalidDataException>(() => ModelImport.ReadObj(Triangle + "f 1/1/1 2/2/1 3/3/1 1/1/1", Token));
        Assert.Equal("OBJ line 9: Triangulate faces in Blender before export.", face.Message);
        Assert.Throws<InvalidDataException>(() => ModelImport.ReadObj(Triangle.Replace("1/1/1", "1/1/1/1"), Token));
        Assert.Equal([0, 1, 2], ModelImport.ReadObj(Triangle, Token).Triangles);
    }

    [Fact]
    public async Task ManifestLibrariesAndMaterialsShareTheScannerAndMeshCache()
    {
        using Folder folder = new();
        string libraries = "mtllib " + new string(' ', 8192) + "mesh.mtl\n";
        folder.Write("mesh.obj", libraries + libraries + Triangle);
        folder.Write("mesh.mtl", "newmtl ignored\n" + new string('\n', 4096) + "map_Kd diffuse.png\n");
        var result = await ModelImport.ReadAsync(folder.Manifest(), Token);
        Assert.Equal([0, 1, 2], result.Models[0].Triangles);
        Assert.Same(result.Models[0], result.Models[1]);
        folder.Write("mesh.mtl", "map_Kd other.png\n");
        Assert.Contains("diffuse PNG", (await Assert.ThrowsAsync<InvalidDataException>(() => ModelImport.ReadAsync(folder.Manifest(), Token))).Message);
        folder.Write("mesh.mtl", "map_Kd ../outside.png\n");
        Assert.Contains("escapes destination", (await Assert.ThrowsAsync<IOException>(() => ModelImport.ReadAsync(folder.Manifest(), Token))).Message);
    }

    [Fact]
    public async Task EightDistinctLibrariesAreAcceptedAndNinthRefusesBeforeAnyMaterialRead()
    {
        using Folder folder = new();
        string declarations = string.Concat(Enumerable.Range(0, 8).Select(i => $"mtllib {i}.mtl\n"));
        foreach (int i in Enumerable.Range(0, 8)) folder.Write($"{i}.mtl", "map_Kd diffuse.png\n");
        folder.Write("mesh.obj", declarations + declarations + Triangle);
        Assert.Equal(2, (await ModelImport.ReadAsync(folder.Manifest(), Token)).Models.Count);
        folder.Write("mesh.obj", declarations + "mtllib missing.mtl\n" + Triangle); int materialReads = 0;
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ModelImport.ReadAsync(folder.Manifest(), Token,
            phase => { if (phase == "materials") materialReads++; }));
        Assert.Contains("eight material libraries", error.Message); Assert.Equal(0, materialReads);
    }

    [Theory]
    [InlineData("libraries")]
    [InlineData("materials")]
    public async Task ManifestCancellationStopsWithinLongScansBeforeAcceptance(string stopPhase)
    {
        using Folder folder = new();
        folder.Write("mesh.obj", "#" + new string('x', 8192) + "\nmtllib mesh.mtl\n" + Triangle);
        folder.Write("mesh.mtl", "#" + new string('x', 8192) + "\nmap_Kd diffuse.png\n");
        string manifest = folder.Manifest(); byte[] original = File.ReadAllBytes(Path.Combine(folder.Root, "mesh.obj"));
        using CancellationTokenSource cancellation = new(); int visits = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() => ModelImport.ReadAsync(manifest, cancellation.Token, phase =>
        { if (phase == stopPhase && ++visits == 3) cancellation.Cancel(); }));
        Assert.Equal(3, visits); Assert.Equal(original, File.ReadAllBytes(Path.Combine(folder.Root, "mesh.obj")));
        Assert.Equal(2, (await ModelImport.ReadAsync(manifest, Token)).Models.Count);
    }

    private sealed class Folder : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "zstudio-obj-growth-" + Guid.NewGuid().ToString("N"));
        internal Folder()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllBytes(Path.Combine(Root, "diffuse.png"), PngEncoder.Encode(new(1, 1, [255, 0, 0, 255]), Token));
        }
        internal void Write(string path, string value) => File.WriteAllText(Path.Combine(Root, path), value);
        internal string Manifest()
        {
            Write("replace.json", JsonSerializer.Serialize(new ModelImportManifest(1, new string('a', 64), "diffuse", "diffuse.png", [new(0, "mesh.obj"), new(1, "mesh.obj")])));
            return Path.Combine(Root, "replace.json");
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
