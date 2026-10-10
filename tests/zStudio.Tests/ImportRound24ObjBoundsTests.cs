using System.Numerics;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Export;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ImportRound24ObjBoundsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void RepeatedPlacementsSpendTheAggregateAllowanceEvenWithOneSharedModel()
    {
        GameScene scene = new(); scene.Models.Add(Model(0, 1));
        ScenePlacement placement = new(0, 0, "same", Matrix4x4.Identity);
        ExportService.CheckObjExpansion(scene, new([placement, placement], []), Token, maximumBytes: 6000);
        Assert.Contains("OBJ", Assert.Throws<InvalidDataException>(() =>
            ExportService.CheckObjExpansion(scene, new([placement, placement, placement], []), Token, maximumBytes: 6000)).Message);
        // Unselected models do not consume an export's allowance.
        scene.Models.Add(Model(1, 40_000));
        ExportService.CheckObjExpansion(scene, new([placement], []), Token, maximumBytes: 3000);
    }

    [Fact]
    public void LargeSharedPlacementExpansionRefusesBeforeAllocatingGeometryOrText()
    {
        GameScene scene = new(); scene.Models.Add(Model(0, 1000));
        ScenePlacement placement = new(0, 0, "same", Matrix4x4.Identity);
        SceneView view = new(Enumerable.Repeat(placement, 10_000).ToArray(), []);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Contains("64 MiB", Assert.Throws<InvalidDataException>(() => ExportService.CheckObjExpansion(scene, view, Token)).Message);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 256 * 1024);
    }

    [Fact]
    public void CancellationStopsBeforeAccessingPlacementGeometry()
    {
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        SceneView view = new([new(0, int.MaxValue, "unread", Matrix4x4.Identity)], []);
        Assert.Throws<OperationCanceledException>(() => ExportService.CheckObjExpansion(new(), view, cancellation.Token));
    }

    [Fact]
    public async Task ActualModelExportReportsTheBoundWithoutWritingPartialObjOrMaterials()
    {
        string root = Path.Combine(Path.GetTempPath(), "zstudio-obj-bound-" + Guid.NewGuid().ToString("N"));
        string input = Path.Combine(root, "input"), output = Path.Combine(root, "output");
        Directory.CreateDirectory(input);
        try
        {
            using AssetResolver resolver = new(input);
            GameScene scene = new(); var model = Model(0, 40_000); scene.Models.Add(model);
            ZbdDocument document = new(Path.Combine(input, "gamez.zbd"), new(0, DateTime.MinValue),
                new(FormatFamily.GameZ, 15, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Scene = scene };
            var asset = document.Add(AssetKind.Model, 0, "large", 0, 0, content: model);
            var result = await new ExportService(resolver).ExportAsync(document, [asset], output, false, token: Token);
            Assert.Equal(0, result.Completed);
            Assert.Contains("64 MiB", Assert.Single(result.Errors));
            Assert.Empty(Directory.EnumerateFiles(output, "*.obj", SearchOption.AllDirectories));
            Assert.Empty(Directory.EnumerateFiles(output, "*.mtl", SearchOption.AllDirectories));
            Assert.Single(Directory.EnumerateFiles(output, "export-report.json", SearchOption.AllDirectories));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static GameModel Model(int index, int polygons)
    {
        Polygon triangle = new(0, 0, [0, 1, 2], [], [], []);
        return new(index, [Vector3.Zero, Vector3.UnitX, Vector3.UnitZ], [], [], Enumerable.Repeat(triangle, polygons).ToArray(), []);
    }
}
