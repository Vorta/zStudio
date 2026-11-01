using System.Globalization;
using System.Numerics;
using zStudio.Core;
using zStudio.Core.Export;
using zStudio.Core.Formats;
using zStudio.Core.Worlds;
using Xunit;

namespace zStudio.Tests;

public sealed class ObjExportTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // Standard assets and model bundles must remain usable in ordinary OBJ consumers after an authored zero scale.
    [Fact]
    public async Task CompiledWorldAndModelBundleKeepOpacityAndFlattenedFaceNormals()
    {
        string root = Path.Combine(Path.GetTempPath(), "zstudio-obj-semantics-" + Guid.NewGuid().ToString("N"));
        string input = Path.Combine(root, "input"); Directory.CreateDirectory(input);
        CultureInfo culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("hr-HR");
            GameZWorld world = new() { NodeCapacity = 2, ModelCapacity = 1, MaterialCapacity = 3 };
            WorldMaterial[] materials = [new() { Flags = 0 }, new() { Flags = 128 }, new() { Flags = 255 }];
            world.Materials.AddRange(materials);
            WorldModel model = new(); model.Vertices.AddRange([new(0, 2, 0), new(2, 2, 0), new(0, 2, 2)]);
            foreach (var material in materials) model.Polygons.Add(new() { Material = material, Vertices = [0, 1, 2] });
            world.Models.Add(model);
            WorldNode top = new("world", WorldNodeClass.World), flat = new("flattened", WorldNodeClass.Object3D) { Model = model };
            flat.SetPayloadInt(0, 0x10);
            // Scale(1, 0, 1) leaves this XZ plane intact, translated away from the origin.
            float[] matrix = [1, 0, 0, 0, 0, 0, 0, 0, 1, 3, 4, 5];
            for (int i = 0; i < matrix.Length; i++) flat.SetPayloadFloat(0x30 + i * 4, matrix[i]);
            top.Children.Add(flat); flat.Parents.Add(top); world.Nodes.AddRange([top, flat]);
            byte[] bytes = GameZWriter.Write(world, Token);
            var document = FormatRegistry.Default.OpenBytes(Path.Combine(input, "gamez.zbd"), bytes, token: Token);
            Assert.DoesNotContain(document.Diagnostics, d => d.Severity == "Error");
            document.Scene!.Materials[2].Remove("alpha"); // The optional scene field defaults to fully opaque.
            using AssetResolver resolver = new(input);
            ExportService exporter = new(resolver);
            var result = await exporter.ExportAsync(document, [document.Assets.Single(a => a.Kind == AssetKind.World)],
                Path.Combine(root, "standard"), false, token: Token);
            Assert.Equal(1, result.Completed); Assert.Empty(result.Errors);
            string obj = Assert.Single(Directory.GetFiles(result.Directory, "*.obj", SearchOption.AllDirectories));
            AssertFaceNormals(obj, new Vector3(3, 4, 5));
            AssertOpacity(Path.ChangeExtension(obj, ".mtl"), [0, 128f / 255, 1]);
            int selected = document.Scene.Nodes.Single(n => n.Name == "flattened").Index;
            var bundle = await exporter.ExportModelBundleAsync(document, selected, Path.Combine(root, "bundle"), token: Token);
            AssertFaceNormals(Path.Combine(bundle.Directory, "assembled.obj"), new Vector3(3, 4, 5));
            AssertFaceNormals(Path.Combine(bundle.Directory, "local", "model_0.obj"), new Vector3(0, 2, 0));
            foreach (string mtl in Directory.GetFiles(bundle.Directory, "*.mtl", SearchOption.AllDirectories))
                AssertOpacity(mtl, [0, 128f / 255, 1]);
        }
        finally { CultureInfo.CurrentCulture = culture; if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    internal static void AssertOpacity(string path, float[] expected)
    {
        float[] opacity = File.ReadLines(path).Where(line => line.StartsWith("d ", StringComparison.Ordinal))
            .Select(line => float.Parse(line[2..], CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(expected, opacity);
    }

    internal static void AssertFaceNormals(string path, Vector3? firstPosition = null)
    {
        string[] lines = File.ReadAllLines(path);
        Vector3[] positions = lines.Where(line => line.StartsWith("v ", StringComparison.Ordinal)).Select(Vector).ToArray();
        Vector3[] normals = lines.Where(line => line.StartsWith("vn ", StringComparison.Ordinal)).Select(Vector).ToArray();
        Assert.NotEmpty(positions); Assert.Equal(positions.Length, normals.Length);
        if (firstPosition is { } expected) Assert.Equal(expected, positions[0]);
        Assert.All(normals, n => Assert.InRange(n.Length(), .99999f, 1.00001f));
        string[] faces = lines.Where(line => line.StartsWith("f ", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(faces);
        foreach (string face in faces)
        {
            string[][] corners = face.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(c => c.Split('/')).ToArray();
            Assert.Equal(3, corners.Length);
            Vector3[] triangle = corners.Select(c => positions[int.Parse(c[0], CultureInfo.InvariantCulture) - 1]).ToArray();
            Vector3 faceNormal = Vector3.Normalize(Vector3.Cross(triangle[1] - triangle[0], triangle[2] - triangle[0]));
            foreach (var corner in corners)
                Assert.InRange(Vector3.Dot(faceNormal, normals[int.Parse(corner[2], CultureInfo.InvariantCulture) - 1]), .99999f, 1.00001f);
        }
        static Vector3 Vector(string line)
        {
            float[] values = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(3)
                .Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            Assert.All(values, v => Assert.True(float.IsFinite(v), "OBJ positions and normals must be finite."));
            return new(values[0], values[1], values[2]);
        }
    }
}
