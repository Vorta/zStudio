using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Export;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>The Blender round trip's safety: stale checkouts, the sealed copy's place, ids, and textures Blender did not change.</summary>
public sealed class SourceBlenderTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Model = "data/m1/models/m1.gltf";

    /// <summary>
    /// A model two maps zone differently, as Blender exports it: one primitive per material, no recorded polygons, vertices
    /// split by face, identical index data shared, children listed by name and an unnamed node shared by two parents named
    /// after its place in the file. The model keeps its node order, polygon order (its materials interleave) and empty
    /// name; both maps keep every face's and node's zones and the faces their boundaries, also the back face of a pair on
    /// the same vertices that Blender's import drops; a rebuilt zoned face is named before anything changes and, accepted,
    /// loses them and is reported gone. Changed runs of two primitives over one project triangle pair it once, and a node
    /// moved between parents that take their zone from the load keeps taking it. A placement whose model the maps name
    /// (post), duplicated and moved in Blender, is a copy: it keeps the post's model and zones in both maps, as a copy made
    /// in the world editor does.
    /// </summary>
    [Fact]
    public void BlenderRoundTripKeepsEveryMapsZonesAndAsksBeforeRebuiltFacesLoseThem()
    {
        using SourceWorldFixture fixture = new();
        const string turret = "data/m1/models/turret.gltf", other = "data/m2/models/turret.gltf";
        WorldMaterial rock = new() { Texture = new("rock"), Flags = 0x1FF }, paint = new() { Flags = 0x1FF };
        Vector2[] square = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        WorldNode Node(string name, uint zone, params PolygonInput[] polygons)
        {
            ModelBuilder builder = new(); foreach (var polygon in polygons) builder.Add(polygon);
            WorldNode node = new(name, WorldNodeClass.Object3D) { Model = polygons.Length > 0 ? builder.Finish() : null, Flags = WorldGltf.DefaultCarried, Zone = zone };
            node.SetPayloadInt(0, 0x28); return node;
        }
        PolygonInput Quad(float y, uint zone) => new([new(0, y, 0), new(1, y, 0), new(1, y, -1), new(0, y, -1)], square, [], [], rock, Zone: zone);
        var hull = Node("hull", 3, Quad(0, 0xFFFF0101), new([new(2, 0, 0), new(3, 0, 0), new(2, 0, -1)], square[..3], [], [], rock, Zone: 0xFFFF0701),
            new([new(0, 1, 0), new(4, 1, 0), new(2, 3, 0)], [], [], [], paint, Zone: 0xFFFF0201),
            new([new(0, 2, -6), new(2, 2, -6), new(3, 2, -8), new(1, 2, -9), new(-1, 2, -8)], [.. square, new(0.5f, 0.5f)], [], [], rock, Zone: 0xFFFF0301));
        // gun's second and third triangles are a back-to-back pair on the same vertices, of which Blender keeps one.
        PolygonInput Triangle(float x, bool back, uint zone) => new(back ? [new(x, 5, -3), new(x + 1, 5, -2), new(x, 5, -2)] : [new(x, 5, -2), new(x + 1, 5, -2), new(x, 5, -3)],
            back ? [square[3], square[1], square[0]] : [square[0], square[1], square[3]], [], [], rock, Zone: zone);
        WorldNode gun = Node("gun", 7, Quad(5, 0xFFFF0401), Triangle(0, false, 0xFFFF0801), Triangle(0, true, 0xFFFF0901), Triangle(2, false, 0xFFFF0A01)),
            antenna = Node("antenna", 3, Quad(6, 0xFFFF0501)), mount = Node("", 7, Quad(7, 0xFFFF0601));
        hull.Children.Add(gun); hull.Children.Add(antenna); gun.Children.Add(mount); antenna.Children.Add(mount); mount.Children.Add(Node("pin", 7));
        antenna.Children.Add(Node("lamp", 3)); hull.Children.Add(Node("post", 7));
        var zoned = WorldGltf.ExportZoned([hull], 0xFF, new() { Texture = t => ($"../textures/{t.Name}.png", 0), Reference = n => n.Name == "post" ? "post.gltf" : null, Token = Token });
        int post = Assert.Single(zoned.References).Key;
        zoned.Geometry.Roots[0].Matrix = Matrix4x4.CreateTranslation(2000, 0, -1000);
        var (json, bin) = zoned.Geometry.Write("turret.bin", Token);
        fixture.Write(turret, json); fixture.Write("data/m1/models/turret.bin", bin);
        // m2 leaves hull, antenna and lamp to the load (as reconstruction binds vehicles and turrets).
        var named = WorldGltf.ZoneLayout.Read(zoned.Geometry, Token).Nodes;
        var second = zoned.Profile with
        {
            Nodes = [.. zoned.Profile.Nodes.Select((n, i) => new WorldNodeZone((uint)(20 + i), i % 2 == 0, named[i].Name is "hull" or "antenna" or "lamp"))],
            MeshPolygons = [.. zoned.Profile.MeshPolygons.Select(m => (IReadOnlyList<uint>)[.. m.Select((w, i) => 0xFF000001u | (uint)(10 + i) << 8)])],
        };
        fixture.Write("data/m1/meta/zones.json", new SourceMapZones([new(turret, turret, zoned.Profile, [new(post, "data/m1/models/post.gltf", "post.gltf")])]).Write(Token));
        fixture.Write("data/m2/meta/zones.json", new SourceMapZones([new(other, turret, second, [new(post, "data/m2/models/post.gltf", "./post.gltf")])]).Write(Token));
        SourceWorkspace workspace = new(fixture.Project);
        string[] first = Zones("m1", turret), shared = Zones("m2", other), shape = Shape();
        var checkout = SourceBlender.Checkout(workspace, turret, Token);

        var plan = SourceBlender.PlanUpdate(workspace, checkout, BlenderExport(checkout, "unchanged"), token: Token);
        Assert.Empty(plan.Notes);
        Apply(workspace, plan); SourceBlender.RecordApplied(checkout, plan);
        Assert.Equal(first, Zones("m1", turret)); Assert.Equal(shared, Zones("m2", other));
        Assert.Equal(shape, Shape());
        Assert.DoesNotContain("Node_", Encoding.UTF8.GetString(workspace.Read(turret, Token)!), StringComparison.Ordinal);

        string copied = BlenderExport(checkout, "copied", nodes: export =>
        {
            var original = export.Roots[0].Children.Single(n => n.Name == "post");
            export.Roots[0].Children.Add(new() { Name = "post.001", Matrix = (original.Matrix ?? Matrix4x4.Identity) * Matrix4x4.CreateTranslation(0, 0, 3), Extras = (JsonObject)original.Extras!.DeepClone() });
        });
        var copy = SourceBlender.PlanUpdate(workspace, SourceBlender.Find(fixture.Project, checkout.Id), copied, token: Token);
        Assert.Contains(copy.Notes, n => n.Contains("hull/post.001 (a copy of hull/post)", StringComparison.Ordinal));
        Apply(workspace, copy);
        string Post(string[] zones) => zones.Single(z => z.StartsWith("post ", StringComparison.Ordinal));
        Assert.Equal(first.Append(Post(first)).Order(StringComparer.Ordinal), Zones("m1", turret));
        Assert.Equal(shared.Append(Post(shared)).Order(StringComparer.Ordinal), Zones("m2", other));
        foreach (var (mission, logical, spelling) in new[] { ("m1", turret, "post.gltf"), ("m2", other, "./post.gltf") })
        {
            Assert.True(SourceMapZones.Parse(workspace.Read($"data/{mission}/meta/zones.json", Token)!, Token).TryGetAsset(logical, out var asset));
            Assert.Equal(2, asset.References.Count(r => r.Asset == $"data/{mission}/models/post.gltf" && r.Spelling == spelling));
        }
        workspace.Undo();

        // The quad's second triangle moves; the paint triangle moves and takes, with the pentagon, a new material, so its
        // primitive's changed run lies over the same project triangle as the quad's. Only the paint triangle is named, and
        // only it loses its zones. lamp moves from antenna to hull: m2 leaves both to the load, so it still takes the load's
        // zone there; m1's hull gives it the 3 it had, which it now states.
        string edited = BlenderExport(checkout, "edited", faces: groups =>
        {
            List<(Vector3 Position, Vector2 Uv)[]> rocks = groups[0].Faces, paints = groups[1].Faces;
            rocks[1][2].Position.Y += 0.5f; paints[0][0].Position.Z += 0.5f;
            groups.Add((new() { Name = "moss", ImageUri = groups[0].Material!.ImageUri, Extras = groups[0].Material!.Extras }, true, [paints[0], .. rocks.Skip(3)]));
            rocks.RemoveRange(3, 3); groups.RemoveAt(1);
        }, nodes: export =>
        {
            var antenna = export.Roots[0].Children.Single(n => n.Name == "antenna"); var lamp = antenna.Children.Single(n => n.Name == "lamp");
            antenna.Children.Remove(lamp); export.Roots[0].Children.Add(lamp);
        });
        var asked = Assert.Throws<BlenderConflictException>(() => SourceBlender.PlanUpdate(workspace, checkout, edited, token: Token));
        Assert.Contains("m1 (turret.gltf: 1 zoned face of hull)", asked.Message); Assert.Contains("m2 (turret.gltf: 1 zoned face of hull)", asked.Message);
        Apply(workspace, SourceBlender.PlanUpdate(workspace, checkout, edited, force: true, token: Token));
        string paintCorner = new Vector3(4, 1, 0).ToString();
        string[] Faces(string[] zones, bool lost = false) => [.. zones.Where(z => z.Contains('<')).Select(z => lost && z.Contains(paintCorner, StringComparison.Ordinal) ? "FFFFFF00" : z[(z.LastIndexOf(' ') + 1)..]).Order()];
        string[] Nodes(string[] zones) => [.. zones.Where(z => !z.Contains('<'))];
        string[] moved1 = Zones("m1", turret), moved2 = Zones("m2", other);
        Assert.Equal(Faces(first, lost: true), Faces(moved1)); Assert.Equal(Faces(shared, lost: true), Faces(moved2));
        Assert.Equal(Nodes(first).Select(z => z.StartsWith("lamp ", StringComparison.Ordinal) ? z.Replace("Inherit = True", "Inherit = False") : z), Nodes(moved1));
        Assert.Equal(Nodes(shared), Nodes(moved2));
        workspace.Undo();

        string rebuilt = BlenderExport(checkout, "rebuilt", poke: true);
        var conflict = Assert.Throws<BlenderConflictException>(() => SourceBlender.PlanUpdate(workspace, checkout, rebuilt, token: Token));
        Assert.Contains("m2 (turret.gltf: 1 zoned face of hull)", conflict.Message);
        Assert.Contains("data/m1/meta/zones.json", conflict.Files);
        var forced = SourceBlender.PlanUpdate(workspace, checkout, rebuilt, force: true, token: Token);
        Assert.Contains("Faces no longer present in turret.gltf (1 removed): hull (1).", forced.Notes);
        Apply(workspace, forced);
        var after = Zones("m1", turret);
        Assert.DoesNotContain(after, z => z.EndsWith("FFFF0101", StringComparison.Ordinal));
        Assert.Equal(first.Where(z => !z.EndsWith("FFFF0101", StringComparison.Ordinal)), after.Where(z => !z.EndsWith("FFFFFF00", StringComparison.Ordinal)));

        // Each node's assignment by name, and each polygon's word by its corner positions.
        string[] Zones(string mission, string logical)
        {
            var document = WorldAssembler.ReadModel(workspace.Read(turret, Token)!, turret, (path, _) => workspace.Read(path, Token)!, Token);
            Assert.True(SourceMapZones.Parse(workspace.Read($"data/{mission}/meta/zones.json", Token)!, Token).TryGetAsset(logical, out var asset));
            WorldGltf.ValidateZoneProfile(document, asset.Profile, Token);
            var layout = WorldGltf.ZoneLayout.Read(document, Token);
            List<string> result = [.. layout.Nodes.Select((n, i) => $"{WorldGltf.EngineName(n)} {asset.Profile.Nodes[i]}")];
            for (int m = 0; m < layout.Meshes.Count; m++)
            {
                int q = 0;
                foreach (var primitive in layout.Meshes[m].Primitives)
                    foreach (var polygon in layout.Polygons[primitive])
                        result.Add(string.Join(";", polygon.Select(c => primitive.Positions[c].ToString()).Order(StringComparer.Ordinal)) + " " + asset.Profile.MeshPolygons[m][q++].ToString("X8"));
            }
            return [.. result.Order(StringComparer.Ordinal)];
        }
        // The model in the order the build reads it: its nodes, depth first, with their transforms, then each mesh's polygons with material and corners.
        string[] Shape()
        {
            var document = WorldAssembler.ReadModel(workspace.Read(turret, Token)!, turret, (path, _) => workspace.Read(path, Token)!, Token);
            var layout = WorldGltf.ZoneLayout.Read(document, Token);
            return [.. layout.Nodes.Select(n => $"node '{WorldGltf.EngineName(n)}' {n.Matrix}"), .. layout.Meshes.SelectMany(m => m.Primitives.SelectMany(p =>
                layout.Polygons[p].Select(polygon => $"{p.Material?.Name}: {string.Join(";", polygon.Select(c => p.Positions[c]))}")))];
        }
    }

    /// <summary>
    /// The checkout as Blender 5.2 exports it (glTF Separate, Custom Properties on): node, mesh and material extras kept,
    /// an unnamed node named after its index (<c>Node_12</c>) and a repeated name numbered (<c>pin.001</c>) in the order
    /// import reads them, children's transforms rounded relative to a parent far from the origin (a node without one gains
    /// one), children listed by name, one primitive per material (materials in first-appearance order,
    /// triangles in order) without extras, vertices split by face normal (here every face), primitives with identical
    /// index data sharing one accessor, and one triangle of those of a primitive that name the same three vertices.
    /// <paramref name="poke"/> rebuilds the first face of the first mesh as a fan around its centre; <paramref name="faces"/>
    /// edits that mesh's faces by material and <paramref name="nodes"/> the exported nodes.
    /// </summary>
    private static string BlenderExport(BlenderCheckout checkout, string folder, bool poke = false,
        Action<List<(GltfMaterial? Material, bool Textured, List<(Vector3 Position, Vector2 Uv)[]> Faces)>>? faces = null, Action<GltfDocument>? nodes = null)
    {
        string input = Path.GetDirectoryName(checkout.Input)!, outbox = Path.Combine(checkout.Outbox, folder), name = Path.GetFileNameWithoutExtension(checkout.Input);
        var source = GltfDocument.Read(File.ReadAllBytes(checkout.Input), uri => File.ReadAllBytes(Path.Combine(input, Uri.UnescapeDataString(uri))), Token);
        Dictionary<GltfMesh, GltfMesh> meshes = new(ReferenceEqualityComparer.Instance);
        Dictionary<GltfNode, string> names = new(ReferenceEqualityComparer.Instance); HashSet<string> used = new(StringComparer.Ordinal);
        foreach (var root in source.Roots) Name(root);
        GltfDocument export = new();
        foreach (var root in source.Roots.OrderBy(r => names[r], StringComparer.Ordinal)) export.Roots.Add(Copy(root, true));
        nodes?.Invoke(export);
        var (json, bin) = export.Write(name + ".bin", Token);
        var gltf = JsonNode.Parse(json)!.AsObject();
        Dictionary<string, int> indices = [];
        foreach (var primitive in gltf["meshes"]!.AsArray().SelectMany(m => m!["primitives"]!.AsArray()))
        {
            var view = gltf["bufferViews"]![(int)gltf["accessors"]![(int)primitive!["indices"]!]!["bufferView"]!]!;
            string data = Convert.ToHexString(bin.AsSpan((int)view["byteOffset"]!, (int)view["byteLength"]!));
            primitive["indices"] = indices.TryAdd(data, (int)primitive["indices"]!) ? (int)primitive["indices"]! : indices[data];
        }
        Directory.CreateDirectory(Path.Combine(outbox, "textures"));
        File.WriteAllText(Path.Combine(outbox, name + ".gltf"), gltf.ToJsonString());
        File.WriteAllBytes(Path.Combine(outbox, name + ".bin"), bin);
        foreach (string texture in Directory.GetFiles(Path.Combine(input, "textures"))) File.Copy(texture, Path.Combine(outbox, "textures", Path.GetFileName(texture)));
        return Path.Combine(folder, name + ".gltf").Replace('\\', '/');

        void Name(GltfNode node)
        {
            string name = node.Name.Length > 0 ? node.Name : $"Node_{node.Index}", unique = name;
            for (int k = 1; !used.Add(unique); k++) unique = $"{name}.{k:000}";
            names[node] = unique;
            foreach (var child in node.Children) Name(child);
        }
        GltfNode Copy(GltfNode node, bool root = false)
        {
            GltfNode copy = new() { Name = names[node], Matrix = root ? node.Matrix : (node.Matrix ?? Matrix4x4.Identity) * Matrix4x4.CreateTranslation(MathF.ScaleB(1, -15), 0, 0), Extras = node.Extras };
            if (node.Mesh is { } mesh) copy.Mesh = meshes.TryGetValue(mesh, out var known) ? known : meshes[mesh] = Blend(mesh, meshes.Count > 0 ? null : groups =>
            {
                if (poke)
                {
                    var first = groups[0].Faces;
                    (Vector3, Vector2)[] ring = [first[0][0], first[0][1], first[0][2], first[1][2]];
                    (Vector3, Vector2) centre = (ring.Aggregate(Vector3.Zero, (s, c) => s + c.Item1) / 4, ring.Aggregate(Vector2.Zero, (s, c) => s + c.Item2) / 4);
                    first.RemoveRange(0, 2);
                    first.InsertRange(0, Enumerable.Range(0, 4).Select(i => new[] { ring[i], ring[(i + 1) % 4], centre }));
                }
                faces?.Invoke(groups);
            });
            foreach (var child in node.Children.OrderBy(c => names[c], StringComparer.Ordinal)) copy.Children.Add(Copy(child));
            return copy;
        }
        static GltfMesh Blend(GltfMesh mesh, Action<List<(GltfMaterial? Material, bool Textured, List<(Vector3 Position, Vector2 Uv)[]> Faces)>>? change)
        {
            // Import keeps one of a primitive's triangles that use the same three vertices.
            static IEnumerable<int> Kept(GltfPrimitive p)
            {
                HashSet<string> sets = [];
                return Enumerable.Range(0, p.Indices.Count / 3).Where(t => sets.Add(string.Join(",", p.Indices.Skip(3 * t).Take(3).Order())));
            }
            List<(GltfMaterial? Material, bool Textured, List<(Vector3 Position, Vector2 Uv)[]> Faces)> groups = [.. mesh.Primitives.GroupBy(p => p.Material).Select(group =>
                (group.Key, group.First().TexCoords.Count > 0, (List<(Vector3 Position, Vector2 Uv)[]>)[.. group.SelectMany(p => Kept(p).Select(t =>
                    Enumerable.Range(0, 3).Select(c => p.Indices[3 * t + c]).Select(i => (p.Positions[i], p.TexCoords.Count > 0 ? p.TexCoords[i] : Vector2.Zero)).ToArray()))]))];
            change?.Invoke(groups);
            GltfMesh result = new() { Name = mesh.Name, Extras = mesh.Extras };
            foreach (var (material, textured, faces) in groups)
            {
                GltfPrimitive primitive = new() { Material = material };
                foreach (var face in faces)
                {
                    var normal = Vector3.Normalize(Vector3.Cross(face[1].Position - face[0].Position, face[2].Position - face[0].Position));
                    foreach (var (position, uv) in face)
                    {
                        primitive.Indices.Add(primitive.Positions.Count); primitive.Positions.Add(position); primitive.Normals.Add(normal);
                        if (textured) primitive.TexCoords.Add(uv);
                    }
                }
                result.Primitives.Add(primitive);
            }
            return result;
        }
    }

    [Fact]
    public void RemovingOneOfTwoSameNamedNodesWarnsBeforeAccepting()
    {
        using SourceWorldFixture fixture = new();
        var json = JsonNode.Parse(File.ReadAllText(fixture.Path(Model)))!.AsObject();
        var nodes = json["nodes"]!.AsArray();
        int duplicate = nodes.Count;
        nodes.Add(nodes[0]!.DeepClone());
        json["scenes"]![0]!["nodes"]!.AsArray().Add(duplicate);
        fixture.Write(Model, json.ToJsonString());
        SourceWorkspace workspace = new(fixture.Project);
        var checkout = SourceBlender.Checkout(workspace, Model, Token);
        string export = Export(checkout, change: g =>
        {
            g["nodes"]!.AsArray().RemoveAt(duplicate);
            var roots = g["scenes"]![0]!["nodes"]!.AsArray(); roots.RemoveAt(roots.Count - 1);
        });
        var plan = SourceBlender.PlanUpdate(workspace, checkout, export, token: Token);
        Assert.Contains(plan.Notes, n => n.Contains("Nodes no longer present", StringComparison.Ordinal) && n.Contains("1 removed"));
        string unchanged = Export(checkout, "unchanged");
        Assert.DoesNotContain(SourceBlender.PlanUpdate(workspace, checkout, unchanged, token: Token).Notes, n => n.Contains("Nodes no longer present"));
    }

    [Fact]
    public void DeletedNodeWarningBoundsItsSampleAndRetainsTheTotalCount()
    {
        using SourceWorldFixture fixture = new();
        var json = JsonNode.Parse(File.ReadAllText(fixture.Path(Model)))!.AsObject();
        var nodes = json["nodes"]!.AsArray(); int original = nodes.Count;
        for (int i = 0; i < 40; i++)
        {
            var node = nodes[0]!.DeepClone();
            node["name"] = new string('x', 120) + i;
            node["extras"]![WorldGltf.Key]!["name"] = new string('x', 32) + i;
            json["scenes"]![0]!["nodes"]!.AsArray().Add(nodes.Count); nodes.Add(node);
        }
        fixture.Write(Model, json.ToJsonString());
        SourceWorkspace workspace = new(fixture.Project); var checkout = SourceBlender.Checkout(workspace, Model, Token);
        string export = Export(checkout, change: g =>
        {
            while (g["nodes"]!.AsArray().Count > original) g["nodes"]!.AsArray().RemoveAt(original);
            var roots = g["scenes"]![0]!["nodes"]!.AsArray(); while (roots.Count > original) roots.RemoveAt(original);
        });
        var warning = Assert.Single(SourceBlender.PlanUpdate(workspace, checkout, export, token: Token).Notes, n => n.Contains("Nodes no longer present"));
        Assert.Contains("40 removed", warning); Assert.Contains("24 more removed nodes", warning); Assert.InRange(warning.Length, 1, 4096);
    }

    [Fact]
    public void FullAppliedManifestRefusesPlanningBeforeAcceptingAnUpdate()
    {
        using SourceWorldFixture fixture = new();
        SourceWorkspace workspace = new(fixture.Project);
        var checkout = SourceBlender.Checkout(workspace, Model, Token);
        string export = Export(checkout, change: g => Rename(g, "renamed"));
        string manifest = Path.Combine(checkout.Folder, "manifest.json");
        var json = JsonNode.Parse(File.ReadAllText(manifest))!.AsObject(); json["padding"] = "";
        json["padding"] = new string('x', 4 * 1024 * 1024 - Encoding.UTF8.GetByteCount(json.ToJsonString()) - 128);
        File.WriteAllText(manifest, json.ToJsonString());
        byte[] original = workspace.Read(Model, Token)!, originalManifest = File.ReadAllBytes(manifest);
        Assert.Contains("4 MiB", Assert.Throws<InvalidDataException>(() => SourceBlender.PlanUpdate(workspace, checkout, export, token: Token)).Message);
        Assert.Equal(original, workspace.Read(Model, Token)); Assert.Equal(originalManifest, File.ReadAllBytes(manifest));
        Assert.Empty(Directory.GetDirectories(Path.Combine(checkout.Folder, "sealed")));
    }

    [Fact]
    public void AppliedManifestChargesEscapedPathsBeforeSerializationAndLeavesTheOriginal()
    {
        using SourceWorldFixture fixture = new();
        var checkout = SourceBlender.Checkout(new SourceWorkspace(fixture.Project), Model, Token);
        string manifest = Path.Combine(checkout.Folder, "manifest.json");
        byte[] original = File.ReadAllBytes(manifest);
        string longPath = new('é', 32000);
        var changes = Enumerable.Range(0, 4096).Select(i => (Relative: longPath + i, Content: Array.Empty<byte>())).ToArray();
        var plan = new BlenderUpdatePlan("test", changes, [], checkout.Folder);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Contains("4 MiB", Assert.Throws<InvalidDataException>(() => SourceBlender.RecordApplied(checkout, plan)).Message);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 4 * 1024 * 1024);
        Assert.Equal(original, File.ReadAllBytes(manifest));
    }

    [Fact]
    public void RecordingAnUpdateNeverWritesThroughAPreexistingTemporaryLink()
    {
        using SourceWorldFixture fixture = new();
        var checkout = SourceBlender.Checkout(new SourceWorkspace(fixture.Project), Model, Token);
        string sentinel = Path.Combine(fixture.Project, "outside.txt"); File.WriteAllText(sentinel, "keep");
        File.CreateSymbolicLink(Path.Combine(checkout.Folder, "manifest.json.tmp"), sentinel);
        SourceBlender.RecordApplied(checkout, new("test", [], [], checkout.Folder));
        Assert.Equal("keep", File.ReadAllText(sentinel));
        Assert.False(File.GetAttributes(Path.Combine(checkout.Folder, "manifest.json")).HasFlag(FileAttributes.ReparsePoint));
    }

    /// <summary>"Exports" the checkout's input unchanged into the outbox (below <paramref name="folder"/>), as Blender would after an edit.</summary>
    private static string Export(BlenderCheckout checkout, string folder = "edit", Action<JsonObject>? change = null)
    {
        string input = Path.GetDirectoryName(checkout.Input)!, outbox = Path.Combine(checkout.Outbox, folder);
        Directory.CreateDirectory(Path.Combine(outbox, "textures"));
        var json = JsonNode.Parse(File.ReadAllText(checkout.Input))!.AsObject();
        change?.Invoke(json);
        File.WriteAllText(Path.Combine(outbox, "m1.gltf"), json.ToJsonString());
        File.Copy(Path.Combine(input, "m1.bin"), Path.Combine(outbox, "m1.bin"), true);
        File.Copy(Path.Combine(input, "textures", "rock.png"), Path.Combine(outbox, "textures", "rock.png"), true);
        return Path.Combine(folder, "m1.gltf").Replace('\\', '/');
    }
    private static void Apply(SourceWorkspace workspace, BlenderUpdatePlan plan) => workspace.Apply(plan.Label, plan.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), Token);
    private static void Rename(JsonObject gltf, string name) => gltf["nodes"]![0]!["name"] = name;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeletedCheckoutInputsRequireAnExplicitDecisionBeforeRestoring(bool texture)
    {
        using SourceWorldFixture fixture = new();
        SourceWorkspace workspace = new(fixture.Project);
        var checkout = SourceBlender.Checkout(workspace, Model, Token);
        string export = Export(checkout);
        string relative = texture ? "data/m1/textures/rock.png" : "data/m1/models/m1.bin";
        byte[] expected = texture ? PngEncoder.Encode(new DecodedImage(1, 1, [0, 255, 0, 255]), Token) : File.ReadAllBytes(fixture.Path(relative));
        if (texture) File.WriteAllBytes(Path.Combine(checkout.Outbox, "edit", "textures", "rock.png"), expected);
        File.Delete(fixture.Path(relative));

        var refused = Assert.Throws<BlenderConflictException>(() => SourceBlender.PlanUpdate(workspace, checkout, export, token: Token));
        Assert.Equal([relative], refused.Files);
        Assert.False(File.Exists(fixture.Path(relative))); Assert.False(workspace.IsDirty);
        Assert.Empty(Directory.Exists(Path.Combine(checkout.Folder, "sealed")) ? Directory.GetDirectories(Path.Combine(checkout.Folder, "sealed")) : []);
        var forced = SourceBlender.PlanUpdate(workspace, checkout, export, force: true, token: Token);
        Assert.Null(forced.Expected[relative]);
        Apply(workspace, forced);
        Assert.Equal(expected, workspace.Read(relative, Token));
        Assert.False(File.Exists(fixture.Path(relative))); // Acceptance is still an unsaved source edit.
    }

    [Fact]
    public void ANewAbsentExportTextureNeedsNoOverwriteDecision()
    {
        using SourceWorldFixture fixture = new();
        SourceWorkspace workspace = new(fixture.Project);
        var checkout = SourceBlender.Checkout(workspace, Model, Token);
        string export = Export(checkout, change: json => json["images"]![0]!["uri"] = "textures/newrock.png");
        byte[] png = PngEncoder.Encode(new DecodedImage(1, 1, [0, 255, 0, 255]), Token);
        File.WriteAllBytes(Path.Combine(checkout.Outbox, "edit", "textures", "newrock.png"), png);
        var plan = SourceBlender.PlanUpdate(workspace, checkout, export, token: Token);
        Assert.Null(plan.Expected["data/m1/textures/newrock.png"]);
        Assert.Contains(plan.Changes, change => change.Relative == "data/m1/textures/newrock.png" && change.Content.AsSpan().SequenceEqual(png));
        Assert.False(workspace.IsDirty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnsupportedMaterialOrMorphExportIsRejectedWithoutChangingWorkspace(bool morph)
    {
        using SourceWorldFixture fixture = new();
        SourceWorkspace workspace = new(fixture.Project);
        byte[] before = workspace.Read(Model, Token)!;
        var checkout = SourceBlender.Checkout(workspace, Model, Token);
        string exported = Export(checkout, change: root =>
        {
            if (morph)
                root["meshes"]![0]!["primitives"]![0]!["targets"] = new JsonArray(new JsonObject(), new JsonObject());
            else root["materials"]![0]!["pbrMetallicRoughness"]!["baseColorFactor"] = new JsonArray(.5f, 1f, 1f, 1f);
        });
        Assert.Throws<InvalidDataException>(() => SourceBlender.PlanUpdate(workspace, checkout, exported, force: true, token: Token));
        Assert.Empty(workspace.History); Assert.Empty(workspace.Overlay()); Assert.Equal(before, workspace.Read(Model, Token));
        var plan = SourceBlender.PlanUpdate(workspace, checkout, Export(checkout, "valid", root => Rename(root, "ground2")), token: Token);
        Apply(workspace, plan);
        Assert.Single(workspace.History);
        workspace.Undo(); Assert.Equal(before, workspace.Read(Model, Token));
    }

    [Fact]
    public void AnUpdateNeverSilentlyReplacesEditsMadeSinceTheCheckout()
    {
        using SourceWorldFixture fixture = new();
        SourceWorkspace workspace = new(fixture.Project);
        var checkout = SourceBlender.Checkout(workspace, Model, Token);
        // The model changes in zStudio while Blender is open.
        var edited = JsonNode.Parse(workspace.Read(Model, Token)!)!.AsObject(); edited["nodes"]![0]!["translation"] = new JsonArray(5f, 0f, 0f);
        workspace.Apply("Move ground", [(Model, Encoding.UTF8.GetBytes(edited.ToJsonString()))], Token);
        string export = Export(checkout, change: g => Rename(g, "ground2"));
        var conflict = Assert.Throws<BlenderConflictException>(() => SourceBlender.PlanUpdate(workspace, checkout, export, token: Token));
        Assert.Equal([Model], conflict.Files);
        // With force, the export replaces the edit and says so.
        var forced = SourceBlender.PlanUpdate(workspace, checkout, export, force: true, token: Token);
        Assert.Contains(forced.Notes, n => n.StartsWith("Replaced changes", StringComparison.Ordinal));
        // Undoing the edit makes the checkout current again; an applied update is recorded, so the next export of the same checkout applies too.
        workspace.Undo();
        var plan = SourceBlender.PlanUpdate(workspace, checkout, export, token: Token);
        Apply(workspace, plan); SourceBlender.RecordApplied(checkout, plan);
        checkout = SourceBlender.Find(fixture.Project, checkout.Id);
        string again = Export(checkout, "again", g => Rename(g, "ground3"));
        Apply(workspace, SourceBlender.PlanUpdate(workspace, checkout, again, token: Token));
        Assert.Contains("ground3", Encoding.UTF8.GetString(workspace.Read(Model, Token)!));

        // Force consents to conflicts, not unbounded old destination reads. Admit the actual held
        // lengths across model+buffer before hashing; the incoming export is still tiny and valid.
        string buffer = Path.ChangeExtension(Model, ".bin");
        string bufferPath = SourceProject.Resolve(fixture.Project, buffer);
        byte[] originalBuffer = File.ReadAllBytes(bufferPath);
        long oldBytes = new FileInfo(SourceProject.Resolve(fixture.Project, Model)).Length + originalBuffer.Length;
        SourceWorkspace bounded = new(fixture.Project, null, oldBytes);
        string smallExport = Export(checkout, "bounded", g => Rename(g, "ground4"));
        File.WriteAllBytes(bufferPath, [.. originalBuffer, (byte)0]);
        Assert.Contains("retained-content allowance", Assert.Throws<InvalidDataException>(() =>
            SourceBlender.PlanUpdate(bounded, checkout, smallExport, force: true, token: Token)).Message);
        Assert.Empty(bounded.History); Assert.False(bounded.IsDirty);
        File.WriteAllBytes(bufferPath, originalBuffer);
        var acceptedForce = SourceBlender.PlanUpdate(bounded, checkout, smallExport, force: true, token: Token);
        SourceWorkspace ordinary = new(fixture.Project);
        Apply(ordinary, acceptedForce); ordinary.Undo();
        Assert.Equal(originalBuffer, ordinary.Read(buffer, Token));
    }

    [Fact]
    public void TexturesBlenderWroteBackUnchangedKeepTheProjectsNewerVersion()
    {
        using SourceWorldFixture fixture = new();
        SourceWorkspace workspace = new(fixture.Project);
        var checkout = SourceBlender.Checkout(workspace, Model, Token);
        // Another edit changes the shared texture after the checkout.
        var image = PngDecoder.Decode(workspace.Read("data/m1/textures/rock.png", Token)!, token: Token); image.Rgba[0] ^= 0xFF;
        byte[] newer = PngEncoder.Encode(image, Token);
        workspace.Apply("Repaint rock", [("data/m1/textures/rock.png", newer)], Token);
        var plan = SourceBlender.PlanUpdate(workspace, checkout, Export(checkout, change: g => Rename(g, "ground2")), token: Token);
        Assert.DoesNotContain(plan.Changes, c => c.Relative == "data/m1/textures/rock.png");
        Apply(workspace, plan);
        Assert.Equal(newer, workspace.Read("data/m1/textures/rock.png", Token));
        // A texture Blender did change, over a newer project version, is a conflict.
        string outbox = Path.Combine(checkout.Outbox, "edit", "textures", "rock.png");
        var painted = PngDecoder.Decode(File.ReadAllBytes(outbox), token: Token); painted.Rgba[4] ^= 0xFF; File.WriteAllBytes(outbox, PngEncoder.Encode(painted, Token));
        workspace.Undo();
        Assert.Contains("data/m1/textures/rock.png", Assert.Throws<BlenderConflictException>(() => SourceBlender.PlanUpdate(workspace, checkout, "edit/m1.gltf", token: Token)).Files);
        // An unreadable PNG is refused before anything changes.
        File.WriteAllBytes(outbox, [0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);
        Assert.Contains("not a readable PNG", Assert.Throws<InvalidDataException>(() => SourceBlender.PlanUpdate(workspace, checkout, "edit/m1.gltf", force: true, token: Token)).Message);
    }

    [Fact]
    public void ExportsThatDropEngineAttributesOrReplaceOtherTexturesAskFirst()
    {
        using SourceWorldFixture fixture = new();
        // Another model's texture, which this model's checkout does not hold.
        var image = PngDecoder.Decode(File.ReadAllBytes(fixture.Path("data/m1/textures/rock.png")), token: Token); image.Rgba[0] ^= 0xFF;
        fixture.Write("data/m1/textures/grass.png", PngEncoder.Encode(image, Token));
        SourceWorkspace workspace = new(fixture.Project);
        var checkout = SourceBlender.Checkout(workspace, Model, Token);
        Assert.Contains("\"recoil\"", File.ReadAllText(checkout.Input));
        // Blender's default (Custom Properties off) writes no extras: the update says what would be lost and asks.
        string bare = Export(checkout, "bare", g =>
        {
            foreach (string key in new[] { "nodes", "meshes", "materials", "scenes" })
                foreach (var item in g[key] as JsonArray ?? []) (item as JsonObject)?.Remove("extras");
        });
        var missing = Assert.Throws<BlenderConflictException>(() => SourceBlender.PlanUpdate(workspace, checkout, bare, token: Token));
        Assert.Contains("Custom Properties", missing.Message);
        Assert.Contains(SourceBlender.PlanUpdate(workspace, checkout, bare, force: true, token: Token).Notes, n => n.Contains("Custom Properties", StringComparison.Ordinal));
        // An exported texture named like a project texture outside the checkout would replace it for every model.
        string named = Export(checkout, "named", g => g["images"]![0]!["uri"] = "textures/grass.png");
        File.Copy(Path.Combine(checkout.Outbox, "named", "textures", "rock.png"), Path.Combine(checkout.Outbox, "named", "textures", "grass.png"));
        Assert.Contains("data/m1/textures/grass.png", Assert.Throws<BlenderConflictException>(() => SourceBlender.PlanUpdate(workspace, checkout, named, token: Token)).Files);
        // Both at once: one conflict names both, so "update anyway" never accepts one it did not show.
        string both = Export(checkout, "both", g =>
        {
            g["images"]![0]!["uri"] = "textures/grass.png";
            foreach (string key in new[] { "nodes", "meshes", "materials", "scenes" })
                foreach (var item in g[key] as JsonArray ?? []) (item as JsonObject)?.Remove("extras");
        });
        File.Copy(Path.Combine(checkout.Outbox, "both", "textures", "rock.png"), Path.Combine(checkout.Outbox, "both", "textures", "grass.png"));
        var combined = Assert.Throws<BlenderConflictException>(() => SourceBlender.PlanUpdate(workspace, checkout, both, token: Token));
        Assert.Contains("Custom Properties", combined.Message); Assert.Contains("grass.png", combined.Message);
        Assert.Contains("data/m1/textures/grass.png", combined.Files);
        // Refused updates leave no sealed copy behind; the forced one above left its own.
        Assert.Single(Directory.GetDirectories(Path.Combine(checkout.Folder, "sealed")));
    }

    [Fact]
    public void CheckoutsShowTransparentTexturesAndHideCollisionVolumes()
    {
        using SourceWorldFixture fixture = new();
        // A pickup model written before models carried these hints: a glow texture with graded alpha, and its collision volume.
        byte[] rgba = new byte[4 * 4 * 4];
        for (int i = 0; i < 16; i++) { rgba[i * 4] = 255; rgba[i * 4 + 3] = (byte)(i * 16); }
        fixture.Write("data/m1/textures/glow.png", PngEncoder.Encode(new DecodedImage(4, 4, rgba), Token));
        WorldNode Node(string name, WorldMaterial material)
        {
            ModelBuilder builder = new();
            builder.Add(new([new(0, 0, 0), new(1, 0, 0), new(1, 0, -1), new(0, 0, -1)], [new(0, 0), new(1, 0), new(1, 1), new(0, 1)], [], [], material));
            WorldNode node = new(name, WorldNodeClass.Object3D) { Model = builder.Finish(), Flags = WorldGltf.DefaultCarried }; node.SetPayloadInt(0, 0x28);
            return node;
        }
        var (json, bin) = WorldGltf.Export([Node("box", new() { Texture = new("glow"), Flags = 0x1FF }), Node("bvol", new() { Color = new(63, 15, 254), Flags = 0xFF })], 0xFF,
            new() { Texture = t => ($"../textures/{t.Name}.png", 0) }).Write("ammo.bin", TestContext.Current.CancellationToken);
        Assert.DoesNotContain("alphaMode", Encoding.UTF8.GetString(json));
        fixture.Write("data/m1/models/ammo.gltf", json); fixture.Write("data/m1/models/ammo.bin", bin);
        // m1's build loads it as a pickup (from the model folder its script set), whose collision volume the game switches off.
        fixture.Write("gamegen/support/pickup.gw", "LoadGameGen ammo.gltf pu012\r\n");
        fixture.Write("gamegen/m1.gs", File.ReadAllText(fixture.Path("gamegen/m1.gs")).Replace("# no vehicles", "source support\\pickup.gw", StringComparison.Ordinal));

        var checkout = SourceBlender.Checkout(new SourceWorkspace(fixture.Project), "data/m1/models/ammo.gltf", Token);
        var materials = JsonNode.Parse(File.ReadAllText(checkout.Input))!["materials"]!.AsArray();
        Assert.Equal(["BLEND", "MASK"], materials.Select(m => (string?)m!["alphaMode"]));
        Assert.Equal(["glow~flat", "color_3F0FFE~flat~hidden"], materials.Select(m => (string?)m!["name"]));
        Assert.Equal(0, materials[1]!["pbrMetallicRoughness"]!["baseColorFactor"]![3]!.GetValue<double>());
        // The project's model is unchanged; only the copy Blender opens shows the hints.
        Assert.Equal(json, File.ReadAllBytes(fixture.Path("data/m1/models/ammo.gltf")));
    }

    [Fact]
    public void SealingStaysInsideTheCheckoutAndIdsCanBeFoundAgain()
    {
        using SourceWorldFixture fixture = new();
        // A model whose name has a space and a '+'.
        fixture.Write("data/m1/models/a b+c.gltf", File.ReadAllBytes(fixture.Path(Model)));
        SourceWorkspace workspace = new(fixture.Project);
        var odd = SourceBlender.Checkout(workspace, "data/m1/models/a b+c.gltf", Token);
        Assert.Equal(odd.Id, SourceBlender.Find(fixture.Project, odd.Id).Id);
        Assert.Throws<InvalidDataException>(() => SourceBlender.Find(fixture.Project, ".."));
        // A deep export that names a buffer above its own folder but inside the outbox: the sealed copy keeps the outbox layout.
        var checkout = SourceBlender.Checkout(workspace, Model, Token);
        string deep = Path.Combine(checkout.Outbox, "a", "b", "c", "d", "e");
        Directory.CreateDirectory(Path.Combine(deep, "textures")); Directory.CreateDirectory(Path.Combine(checkout.Outbox, "data"));
        var json = JsonNode.Parse(File.ReadAllText(checkout.Input))!.AsObject();
        json["buffers"]![0]!["uri"] = "../../../../../data/m1.bin";
        File.WriteAllText(Path.Combine(deep, "m1.gltf"), json.ToJsonString());
        File.Copy(Path.Combine(Path.GetDirectoryName(checkout.Input)!, "m1.bin"), Path.Combine(checkout.Outbox, "data", "m1.bin"));
        File.Copy(Path.Combine(Path.GetDirectoryName(checkout.Input)!, "textures", "rock.png"), Path.Combine(deep, "textures", "rock.png"));
        byte[] before = File.ReadAllBytes(fixture.Path("data/m1/models/m1.bin"));
        var plan = SourceBlender.PlanUpdate(workspace, checkout, "a/b/c/d/e/m1.gltf", token: Token);
        Assert.True(File.Exists(Path.Combine(plan.Sealed, "data", "m1.bin")));
        Assert.StartsWith(Path.Combine(checkout.Folder, "sealed"), plan.Sealed);
        Assert.Equal(before, File.ReadAllBytes(fixture.Path("data/m1/models/m1.bin")));
        Assert.False(File.Exists(fixture.Path("data/m1.bin")));
    }
}
