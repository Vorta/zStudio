using System.Collections;
using System.Numerics;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ImportRound23FanWorkTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void AffineFitChargesItsSearchBeforeReadingOrProjectingCorners()
    {
        UnreadablePoints points = new(100_000);
        Assert.Contains("work limit", Assert.Throws<InvalidDataException>(() =>
            ModelBuilder.Affine(points, [], ModelBuilder.AffineTolerance, Token)).Message);
        Assert.Equal(0, points.Reads);
    }

    [Fact]
    public void CancellationDuringFitPreparationStopsBeforeTheTripleSearch()
    {
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        GltfPrimitive fan = Fan();
        CancellingPoints points = new(fan.Positions, cancellation);
        Assert.Throws<OperationCanceledException>(() =>
            ModelBuilder.Affine(points, fan.TexCoords, ModelBuilder.AffineTolerance, cancellation.Token));
        Assert.InRange(points.Reads, 1, 3 * fan.Positions.Count);
    }

    [Fact]
    public void FanFittingAllowanceIsSharedAcrossPrimitives()
    {
        PolygonWorkBudget work = new(Token, 400);
        Assert.Equal(Enumerable.Range(0, 8), Assert.Single(WorldGltf.MergeFans(Fan(), true, work)));
        Assert.Contains("work limit", Assert.Throws<InvalidDataException>(() => WorldGltf.MergeFans(Fan(), true, work)).Message);
        Assert.Single(WorldGltf.MergeFans(Fan(), true, new(Token, 400)));
    }

    [Fact]
    public void OneImportAllowanceCoversSeparateDocumentReadings()
    {
        WorldGltf.ImportContext context = Context(new(Token, 400));
        var document = Document();
        Assert.Single(WorldGltf.Import(document, "first.gltf", 0xFF, context));
        Assert.Single(context.World.Models);
        Assert.Contains("work limit", Assert.Throws<InvalidDataException>(() =>
            WorldGltf.Import(document, "second.gltf", 0xFF, context)).Message);
        // A separately started load still succeeds; the allowance is scoped to one context, not global state.
        Assert.Single(WorldGltf.Import(document, "second.gltf", 0xFF, Context(new(Token, 400))));
    }

    [Fact]
    public void AffineAndNonAffineTextureMappingsKeepTheirExistingPolygonSemantics()
    {
        var affine = Fan();
        Assert.Single(WorldGltf.Polygons(affine, true, new(Token)));
        affine.TexCoords[4] += new Vector2(0.5f, 0.5f);
        Assert.True(WorldGltf.Polygons(affine, true, new(Token)).Count > 1);
    }

    [Fact]
    public void RecordedPolygonValidationUsesTheSameWorkAllowance()
    {
        var primitive = Fan();
        primitive.Extras = new() { [WorldGltf.Key] = new System.Text.Json.Nodes.JsonObject
        {
            ["polygons"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["triangles"] = 6,
                ["corners"] = new System.Text.Json.Nodes.JsonArray(0, 1, 2, 3, 4, 5, 6, 7),
            }),
        } };
        Assert.Contains("work limit", Assert.Throws<InvalidDataException>(() =>
            WorldGltf.Polygons(primitive, true, new(Token, 100))).Message);
        Assert.Single(WorldGltf.Polygons(primitive, true, new(Token, 200)));
    }

    private static WorldGltf.ImportContext Context(PolygonWorkBudget work) => new()
    {
        World = new(), Token = Token, PolygonWork = work,
        Reference = (_, _) => throw new InvalidOperationException(), TextureName = (_, _, _) => "fan",
    };
    private static GltfDocument Document()
    {
        GltfMesh mesh = new(); mesh.Primitives.Add(Fan());
        GltfDocument document = new(); document.Roots.Add(new() { Name = "fan", Mesh = mesh }); return document;
    }
    private static GltfPrimitive Fan()
    {
        GltfPrimitive primitive = new() { Material = new() { ImageUri = "fan.png" } };
        for (int i = 0; i < 8; i++)
        {
            float angle = i * MathF.Tau / 8;
            Vector2 p = new(MathF.Cos(angle), MathF.Sin(angle));
            primitive.Positions.Add(new(p, 0)); primitive.TexCoords.Add(p);
        }
        for (int i = 1; i < 7; i++) primitive.Indices.AddRange([0, i, i + 1]);
        return primitive;
    }
    private sealed class UnreadablePoints(int count) : IReadOnlyList<Vector3>
    {
        public int Count => count;
        public int Reads { get; private set; }
        public Vector3 this[int index] { get { Reads++; throw new InvalidOperationException("Budget must refuse before reading points."); } }
        public IEnumerator<Vector3> GetEnumerator() => throw new InvalidOperationException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class CancellingPoints(IReadOnlyList<Vector3> values, CancellationTokenSource cancellation) : IReadOnlyList<Vector3>
    {
        public int Count => values.Count;
        public int Reads { get; private set; }
        public Vector3 this[int index] { get { Reads++; cancellation.Cancel(); return values[index]; } }
        public IEnumerator<Vector3> GetEnumerator() { for (int i = 0; i < Count; i++) yield return this[i]; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
