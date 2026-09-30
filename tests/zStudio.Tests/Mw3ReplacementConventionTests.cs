using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Export;
using Recoil.Zbd.Core.Formats;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>
/// Version-27 replacement records follow authored corpus conventions. These values are not engine-verified;
/// version-15 output must stay byte-identical to the pre-existing writer.
/// </summary>
public sealed class Mw3ReplacementConventionTests
{
    private const uint Priority = 4, Field24 = 0x1234ABCD, Zone = 0xFFFF0201;
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void Version27WorldReplacementCopiesTheReplacedModelsPolygonConventionAndAuthoredMaterialFlags()
    {
        var source = FormatRegistry.Default.OpenBytes("gamez.zbd", World27(0x13, withPolygons: true), token: Token);
        Assert.DoesNotContain(source.Diagnostics, d => d.Severity == "Error");
        var output = FormatRegistry.Default.OpenBytes("gamez.zbd", ModelReplacementWriter.Replace(source, new Dictionary<int, ImportedMesh> { [1] = Quad }, "mw3_new", Token), token: Token);
        Assert.DoesNotContain(output.Diagnostics, d => d.Severity == "Error");
        Assert.All(PolygonHeaders(output, 1), p => Assert.Equal((Priority, Field24, Zone), p));
        Assert.Equal(2, PolygonHeaders(output, 1).Count);
        // Untouched model keeps its authored record.
        Assert.Equal(source.Slice(source.Assets.Single(a => a.Kind == AssetKind.Model && a.Index == 0).Offset, 108).ToArray(),
            output.Slice(output.Assets.Single(a => a.Kind == AssetKind.Model && a.Index == 0).Offset, 108).ToArray());
        var material = output.Scene!.Materials[^1];
        Assert.Equal(0x13u, material.UInt("flags")); Assert.Equal(255, material.Int("alpha")); Assert.Equal(1, material.Int("texture_index"));
    }

    [Fact]
    public void Version27WorldReplacementFallsBackToCorpusConventionsWithoutAuthoredTemplates()
    {
        // Material 0 is untextured and model 1 has no polygon from which to copy a convention.
        var source = FormatRegistry.Default.OpenBytes("gamez.zbd", World27(0x10, withPolygons: false), token: Token);
        Assert.DoesNotContain(source.Diagnostics, d => d.Severity == "Error"); Assert.Empty(source.Scene!.Models[1].Polygons);
        var output = FormatRegistry.Default.OpenBytes("gamez.zbd", ModelReplacementWriter.Replace(source, new Dictionary<int, ImportedMesh> { [1] = Quad }, "mw3_new", Token), token: Token);
        Assert.DoesNotContain(output.Diagnostics, d => d.Severity == "Error");
        Assert.All(PolygonHeaders(output, 1), p => Assert.Equal((0u, 1u, 0xFFFFFF00u), p));
        Assert.Equal(0x11u, output.Scene!.Materials[^1].UInt("flags"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Version27MechMemberReplacementCopiesTheReplacedModelsPolygonConvention(bool withPolygons)
    {
        var library = FormatRegistry.Default.OpenBytes("mechlib.zbd", MechLibrary(withPolygons), token: Token);
        Assert.DoesNotContain(library.Diagnostics, d => d.Severity == "Error");
        var assembly = Assert.IsType<MechAssembly>(library.Assets[3].Content);
        var resources = new ResourceEditSession(library); var member = resources.Current.Members[3].Id;
        resources.Accept(await resources.PrepareMechModelAsync(member, 0, Quad, 0, Token));
        var changed = resources.Current.Document;
        Assert.Equal(Quad.Positions, changed.Scene!.Models[assembly.FirstModel].Vertices);
        var expected = withPolygons ? (Priority, Field24, Zone) : (0u, 1u, 0xFFFFFF00u);
        Assert.All(PolygonHeaders(changed, assembly.FirstModel), p => Assert.Equal(expected, p));
        // Materials and the metadata members stay byte-identical.
        for (int i = 0; i < 3; i++) Assert.Equal(library.Slice(library.Assets[i].Offset, library.Assets[i].Length).ToArray(), changed.Slice(changed.Assets[i].Offset, changed.Assets[i].Length).ToArray());
    }

    [Fact]
    public void Version15ReplacementBytesAreUnchangedAndDoNotCopyAuthoredPolygonWords()
    {
        // Hashes recorded from the c9e7daa writer before version-27 conventions were introduced.
        var plain = FormatRegistry.Default.OpenBytes("gamez.zbd", ModelFixture.GameZ(), token: Token);
        Assert.Equal(PlainVersion15Hash, Hash(ModelReplacementWriter.Replace(plain, new Dictionary<int, ImportedMesh> { [1] = ModelFixture.Mesh }, "new", Token)));
        byte[] authored = ModelFixture.GameZ();
        foreach (int data in new[] { 36 + 16 + 4 * 44 + 12 + 2 * 88, 36 + 16 + 4 * 44 + 12 + 2 * 88 + 100 })
        { BinaryPrimitives.WriteInt32LittleEndian(authored.AsSpan(data + 40), 5); BinaryPrimitives.WriteUInt32LittleEndian(authored.AsSpan(data + 60), 0xFFFF0101); }
        var source = FormatRegistry.Default.OpenBytes("gamez.zbd", authored, token: Token);
        byte[] output = ModelReplacementWriter.Replace(source, new Dictionary<int, ImportedMesh> { [1] = ModelFixture.Mesh }, "new", Token);
        Assert.Equal(AuthoredVersion15Hash, Hash(output));
        Assert.All(PolygonHeaders(FormatRegistry.Default.OpenBytes("gamez.zbd", output, token: Token), 1), p => Assert.Equal((0u, 0u, 0u), p));
        Assert.Equal(1u, FormatRegistry.Default.OpenBytes("gamez.zbd", output, token: Token).Scene!.Materials[^1].UInt("flags"));
    }

    [Fact]
    public async Task OptionalVersion15CorpusReplacementBytesAreUnchanged()
    {
        string? root = Environment.GetEnvironmentVariable("ZSTUDIO_CORPUS"); if (root == null) return;
        string path = Path.Combine(root, "m1", "gamez.zbd"); if (!File.Exists(path)) return;
        var source = await FormatRegistry.Default.OpenAsync(path, Token);
        if (Hash(source.Bytes.ToArray()) != RetailM1Hash) return; // Only the known retail m1 world has a recorded result.
        var changes = Enumerable.Range(1207, 6).ToDictionary(i => i, _ => ModelFixture.Mesh);
        Assert.Equal(RetailM1ReplacementHash, Hash(ModelReplacementWriter.Replace(source, changes, "pu001_new", Token)));
    }

    [Fact]
    public async Task OptionalMw3CorpusReplacementsFollowAuthoredConventions()
    {
        string? root = Environment.GetEnvironmentVariable("ZSTUDIO_MW3_CORPUS"); if (root == null) return;
        foreach (string map in Directory.GetDirectories(root).Where(d => File.Exists(Path.Combine(d, "gamez.zbd"))))
        {
            var world = await FormatRegistry.Default.OpenAsync(Path.Combine(map, "gamez.zbd"), Token);
            Assert.All(world.Scene!.Materials, m => Assert.NotEqual(0u, m.UInt("flags") & 0x10));
            var model = world.Scene.Models.First(m => m.Morphs.Length == 0 && m.Metadata.Int("light_count") == 0 && m.Polygons.Length > 0 && world.Scene.Nodes.Any(n => n.ModelIndex == m.Index));
            var authored = PolygonHeaders(world, model.Index);
            Assert.All(authored, p => Assert.NotEqual(0u, p.Field24)); Assert.DoesNotContain(authored, p => p.Zone == 0);
            var mesh = new ImportedMesh(model.Vertices.Take(3).ToArray(), [Vector3.UnitY, Vector3.UnitY, Vector3.UnitY], [Vector2.Zero, Vector2.UnitX, Vector2.UnitY], [0, 1, 2]);
            var output = FormatRegistry.Default.OpenBytes(world.Path, ModelReplacementWriter.Replace(world, new Dictionary<int, ImportedMesh> { [model.Index] = mesh }, "mw3_edit_test", Token), token: Token);
            Assert.DoesNotContain(output.Diagnostics, d => d.Severity == "Error"); Assert.Equal(mesh.Positions, output.Scene!.Models[model.Index].Vertices);
            Assert.All(PolygonHeaders(output, model.Index), p => Assert.Equal(authored[0], p));
            Assert.Equal(0x11u, output.Scene.Materials[^1].UInt("flags"));
        }
        var library = await FormatRegistry.Default.OpenAsync(Path.Combine(root, "mechlib.zbd"), Token);
        Assert.All(library.Scene!.Materials, m => Assert.NotEqual(0u, m.UInt("flags") & 0x10));
        var asset = library.Assets.First(a => a.Content is MechAssembly m && library.Scene.Models[m.FirstModel].Polygons.Length > 0 && library.Scene.Models[m.FirstModel].Morphs.Length == 0);
        var mech = (MechAssembly)asset.Content!; var replaced = library.Scene.Models[mech.FirstModel];
        var before = PolygonHeaders(library, replaced.Index); Assert.All(before, p => Assert.NotEqual(0u, p.Field24)); Assert.All(before, p => Assert.Equal(0xFFFFFF00u, p.Zone));
        var center = (replaced.Vertices.Aggregate(Vector3.Min) + replaced.Vertices.Aggregate(Vector3.Max)) / 2;
        var small = new ImportedMesh([center, center + new Vector3(.001f, 0, 0), center + new Vector3(0, .001f, 0)], [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ], [Vector2.Zero, Vector2.UnitX, Vector2.UnitY], [0, 1, 2]);
        var resources = new ResourceEditSession(library);
        resources.Accept(await resources.PrepareMechModelAsync(resources.Current.Members[asset.Index].Id, 0, small, replaced.Polygons[0].MaterialIndex, Token));
        var changed = resources.Current.Document; Assert.Equal(small.Positions, changed.Scene!.Models[mech.FirstModel].Vertices);
        Assert.All(PolygonHeaders(changed, mech.FirstModel), p => Assert.Equal(before[0], p));
    }

    private const string PlainVersion15Hash = "9799AA367BEA0F6F2C01ED17128E9CB9278E58061E695AD2C2E348F105694D3E",
        AuthoredVersion15Hash = "01C1C6CC711B75548DB04973B35CC2C5A9FC515F9CA98B35F0725E2CF09BA2BE",
        RetailM1Hash = "51C284CB50194DC6ED21212E32BDDED6C0A248D9F68D0F8BFD7E689DC1C4C5DD",
        RetailM1ReplacementHash = "F29E686F1BD5B104EC54763E9664D0EB6F3DDE9D5D2791FF66E208EED5E32E70";
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static ImportedMesh Quad => new([new(-.3f, 0, 0), new(.3f, 0, 0), new(.3f, 1, 0), new(-.3f, 1, 0)], [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
        [new(0, 1), new(1, 1), new(1, 0), new(0, 0)], [0, 1, 2, 0, 2, 3]);

    /// <summary>Priority, field24 (v27 only) and zone words of every stored polygon header in one model.</summary>
    internal static List<(uint Priority, uint Field24, uint Zone)> PolygonHeaders(ZbdDocument doc, int modelIndex)
    {
        var model = doc.Scene!.Models[modelIndex]; bool v27 = doc.Game == GameVariant.MechWarrior3; int size = v27 ? 36 : 28;
        long start = model.Metadata["source_data_offset"]?.GetValue<long>() ?? doc.Assets.Single(a => a.Kind == AssetKind.Model && a.Index == modelIndex).Offset;
        long at = start + 12L * (model.Vertices.Length + model.Normals.Length + model.Morphs.Length);
        List<(uint, uint, uint)> result = [];
        for (int i = 0; i < model.Polygons.Length; i++)
        {
            var r = doc.Slice(at + i * (long)size, size).Span;
            Assert.Equal(model.Polygons[i].Flags, BinaryPrimitives.ReadUInt32LittleEndian(r));
            result.Add((BinaryPrimitives.ReadUInt32LittleEndian(r[4..]), v27 ? BinaryPrimitives.ReadUInt32LittleEndian(r[24..]) : 0, BinaryPrimitives.ReadUInt32LittleEndian(r[(size - 4)..])));
        }
        return result;
    }

    /// <summary>Two-model, two-node version-27 world. Model 1 optionally has one authored polygon; material 0 uses <paramref name="materialFlags"/>.</summary>
    internal static byte[] World27(byte materialFlags, bool withPolygons)
    {
        const int textures = 36, materials = textures + 40, models = materials + 16 + 4 * 44, table = models + 12 + 2 * 96;
        int[] size = [108, withPolygons ? 108 : 36]; int data = table, nodes = data + size[0] + size[1], nodeData = nodes + 2 * 212;
        byte[] b = new byte[nodeData + 2 * 148];
        void I(int o, int v) => BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o), v);
        void U(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v);
        void F(int o, float v) => I(o, BitConverter.SingleToInt32Bits(v));
        I(0, 0x02971222); I(4, 27); I(8, 1); I(12, textures); I(16, materials); I(20, models); I(24, 2); I(28, -1); I(32, nodes);
        Encoding.ASCII.GetBytes("tex0").CopyTo(b, textures + 8); I(textures + 28, 2); I(textures + 36, -1);
        I(materials, 4); I(materials + 4, 1); I(materials + 8, 1); I(materials + 12, 0);
        for (int i = 0; i < 4; i++)
        {
            int o = materials + 16 + i * 44; b[o] = 255; I(o + 16, -1);
            BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(o + 42), (short)(i is 0 or 3 ? -1 : i + 1)); BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(o + 40), (short)(i < 2 ? -1 : i - 1));
        }
        b[materials + 16 + 1] = materialFlags; I(materials + 16 + 16, (materialFlags & 1) != 0 ? 0 : -1);
        I(models, 2); I(models + 4, 2); I(models + 8, -1);
        var mesh = ModelFixture.Mesh;
        for (int i = 0; i < 2; i++)
        {
            int h = models + 12 + i * 96, d = data + (i == 0 ? 0 : size[0]); bool polygon = i == 0 || withPolygons;
            I(h, 1); I(h + 8, 3); I(h + 12, 1); I(h + 16, polygon ? 1 : 0); I(h + 20, 3); I(h + 52, 123); I(h + 68, 456); F(h + 84, 2); I(h + 92, d);
            for (int v = 0; v < 3; v++) { F(d + v * 12, mesh.Positions[v].X); F(d + v * 12 + 4, mesh.Positions[v].Y); F(d + v * 12 + 8, mesh.Positions[v].Z); }
            if (polygon)
            {
                I(d + 36, 3); U(d + 40, Priority); I(d + 44, 111); I(d + 52, 222); U(d + 60, Field24); I(d + 64, 0); U(d + 68, Zone);
                for (int v = 0; v < 3; v++) { I(d + 72 + v * 4, v); F(d + 84 + v * 8, mesh.Uvs[v].X); F(d + 88 + v * 8, mesh.Uvs[v].Y); }
            }
            h = nodes + i * 212; Encoding.ASCII.GetBytes(i == 0 ? "root" : "child").CopyTo(b, h);
            I(h + 52, 5); I(h + 60, i); I(h + 84, i); I(h + 92, 1 - i); I(h + 36, i == 0 ? 0x700 : 0x300);
            for (int box = 116; box <= (i == 0 ? 164 : 140); box += 24) { F(h + box, -3); F(h + box + 4, -3); F(h + box + 8, -3); F(h + box + 12, 3); F(h + box + 16, 3); F(h + box + 20, 3); }
            I(h + 208, nodeData + i * 148); I(nodeData + i * 148, 8); I(nodeData + i * 148 + 144, 1 - i);
        }
        return b;
    }

    /// <summary>Version-27 mech library archive: version, format, one textured material and one single-node member.</summary>
    internal static byte[] MechLibrary(bool withPolygons)
    {
        using MemoryStream s = new(); using BinaryWriter w = new(s); List<(string Name, long Offset, long Length)> members = [];
        void Member(string name, Action write) { long start = s.Position; write(); members.Add((name, start, s.Position - start)); }
        Member("version", () => w.Write(27)); Member("format", () => w.Write(1));
        Member("materials", () =>
        {
            w.Write(1); byte[] m = new byte[40]; m[0] = 255; m[1] = 0x11; BinaryPrimitives.WriteInt32LittleEndian(m.AsSpan(16), 0x0BADF00D); w.Write(m);
            w.Write(4); w.Write("mtex"u8);
        });
        Member("mech_test.flt", () =>
        {
            byte[] node = new byte[208]; Encoding.ASCII.GetBytes("root").CopyTo(node, 0);
            BinaryPrimitives.WriteInt32LittleEndian(node.AsSpan(36), 0x300); BinaryPrimitives.WriteInt32LittleEndian(node.AsSpan(52), 5); BinaryPrimitives.WriteInt32LittleEndian(node.AsSpan(60), 0x4000);
            for (int box = 116; box <= 140; box += 24) for (int k = 0; k < 6; k++) BinaryPrimitives.WriteSingleLittleEndian(node.AsSpan(box + k * 4), k < 3 ? -3 : 3);
            w.Write(node); byte[] object3d = new byte[144]; BinaryPrimitives.WriteInt32LittleEndian(object3d, 8); w.Write(object3d);
            byte[] header = new byte[92]; BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), withPolygons ? 1 : 0); BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20), 3);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(52), 123); BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(84), 2); w.Write(header);
            foreach (var v in ModelFixture.Mesh.Positions) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
            if (withPolygons)
            {
                w.Write(3); w.Write(Priority); w.Write(111); w.Write(0); w.Write(222); w.Write(0); w.Write(Field24); w.Write(0); w.Write(Zone);
                w.Write(0); w.Write(1); w.Write(2); foreach (var uv in ModelFixture.Mesh.Uvs) { w.Write(uv.X); w.Write(uv.Y); }
            }
        });
        foreach (var (name, offset, length) in members)
        {
            byte[] record = new byte[148]; BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)offset); BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4), (uint)length);
            Encoding.Latin1.GetBytes(name).CopyTo(record, 8); w.Write(record);
        }
        w.Write(1); w.Write(members.Count);
        return s.ToArray();
    }
}
