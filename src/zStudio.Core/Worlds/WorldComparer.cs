using System.Numerics;

namespace Recoil.Zbd.Core.Worlds;

/// <summary>A difference between two worlds, at a node path.</summary>
public sealed record WorldDifference(string Path, string Field, string Expected, string Actual);

/// <summary>
/// Compares two worlds by meaning: nodes are matched by name path (repeated names in order), and each pair's class,
/// carried and derived flags, zone, local transform, class data, model geometry and grid cell are compared, along
/// with the world, its lights and the texture names. Node slots, stored pointers and polygon order do not matter.
/// </summary>
public static class WorldComparer
{
    public static List<WorldDifference> Compare(GameZWorld expected, GameZWorld actual, int limit = 10_000)
    {
        List<WorldDifference> differences = [];
        void Add(string path, string field, object? a, object? b) { if (differences.Count < limit) differences.Add(new(path, field, $"{a}", $"{b}")); }
        var worldA = expected.Nodes.FirstOrDefault(n => n.Class == WorldNodeClass.World); var worldB = actual.Nodes.FirstOrDefault(n => n.Class == WorldNodeClass.World);
        if (worldA == null || worldB == null) { Add("", "world", worldA?.Name, worldB?.Name); return differences; }
        CompareClassData(worldA, worldB, "world");
        Match("world", Members(worldA), Members(worldB), true);
        Match("", expected.Nodes.Where(n => n.Parents.Count == 0 && n.Class != WorldNodeClass.World).ToList(), actual.Nodes.Where(n => n.Parents.Count == 0 && n.Class != WorldNodeClass.World).ToList(), false);
        var texturesA = expected.Textures.Select(t => t.Name.ToLowerInvariant()).ToHashSet(); var texturesB = actual.Textures.Select(t => t.Name.ToLowerInvariant()).ToHashSet();
        foreach (var t in texturesA.Except(texturesB).Order()) Add("textures", "missing", t, "");
        foreach (var t in texturesB.Except(texturesA).Order()) Add("textures", "extra", "", t);
        return differences;

        static List<WorldNode> Members(WorldNode world) => [.. world.Children, .. world.Areas.SelectMany(a => a.Nodes)];

        void Match(string path, List<WorldNode> a, List<WorldNode> b, bool worldChildren)
        {
            foreach (var group in a.GroupBy(n => n.Name))
            {
                var others = b.Where(n => n.Name == group.Key).ToList(); var mine = group.ToList();
                if (mine.Count != others.Count) Add($"{path}/{group.Key}", "count", mine.Count, others.Count);
                // Repeated names pair up by their structure, their children's names and their position, then by order.
                var ordered = others.OrderBy(PairKey, StringComparer.Ordinal).ToList(); int k = 0;
                foreach (var node in mine.OrderBy(PairKey, StringComparer.Ordinal))
                {
                    if (k >= ordered.Count) break;
                    CompareNode($"{path}/{group.Key}", node, ordered[k++], worldChildren, 0);
                }
            }
            foreach (var name in b.Select(n => n.Name).Distinct().Except(a.Select(n => n.Name))) Add($"{path}/{name}", "extra", "", name);
        }

        void CompareNode(string path, WorldNode a, WorldNode b, bool worldChild, int depth)
        {
            if (depth > 256) return;
            if (a.Class != b.Class) { Add(path, "class", a.Class, b.Class); return; }
            uint carried = WorldGltf.CarriedFlags;
            if ((a.Flags & carried) != (b.Flags & carried)) Add(path, "flags.carried", $"{a.Flags & carried:X8}", $"{b.Flags & carried:X8}");
            if ((a.Flags & ~carried) != (b.Flags & ~carried)) Add(path, "flags.derived", $"{a.Flags & ~carried:X8}", $"{b.Flags & ~carried:X8}");
            if ((a.Zone & 0xFF) != (b.Zone & 0xFF)) Add(path, "zone", a.Zone & 0xFF, b.Zone & 0xFF);
            if (worldChild && (a.GridColumn, a.GridRow) != (b.GridColumn, b.GridRow)) Add(path, "cell", (a.GridColumn, a.GridRow), (b.GridColumn, b.GridRow));
            if (a.Class == WorldNodeClass.Object3D)
            {
                var ma = WorldUpdate.LocalMatrix(a) ?? Matrix4x4.Identity; var mb = WorldUpdate.LocalMatrix(b) ?? Matrix4x4.Identity;
                if (!Close(ma, mb)) Add(path, "matrix", Format(ma), Format(mb));
                if ((a.PayloadInt(0) & 0x3F) != (b.PayloadInt(0) & 0x3F) && !(Close(ma, Matrix4x4.Identity) && Close(mb, Matrix4x4.Identity))) Add(path, "object.flags", $"{a.PayloadInt(0):X}", $"{b.PayloadInt(0):X}");
                for (int o = 0x18; o < 0x30; o += 4) if (a.PayloadFloat(o) != b.PayloadFloat(o)) { Add(path, "object.trs", o, $"{a.PayloadFloat(o)}/{b.PayloadFloat(o)}"); break; }
            }
            else CompareClassData(a, b, path);
            if ((a.Model == null) != (b.Model == null)) Add(path, "model", a.Model != null, b.Model != null);
            else if (a.Model != null && b.Model != null) CompareModel(path, a.Model, b.Model);
            if (a.Children.Count != b.Children.Count) Add(path, "children", string.Join(",", a.Children.Select(c => c.Name)), string.Join(",", b.Children.Select(c => c.Name)));
            Match(path, a.Children, b.Children, false);
        }

        void CompareModel(string path, WorldModel a, WorldModel b)
        {
            if (a.Mode != b.Mode || a.Flags != b.Flags) Add(path, "model.mode", $"{a.Mode}:{a.Flags:X}", $"{b.Mode}:{b.Flags:X}");
            if (a.Points.Count != b.Points.Count) Add(path, "model.points", a.Points.Count, b.Points.Count);
            if (a.Morphs.Count != b.Morphs.Count) Add(path, "model.morphs", a.Morphs.Count, b.Morphs.Count);
            if (a.BoundsCentre != b.BoundsCentre || a.BoundsRadius != b.BoundsRadius) Add(path, "model.sphere", $"{a.BoundsCentre} {a.BoundsRadius}", $"{b.BoundsCentre} {b.BoundsRadius}");
            // Polygons compare as multisets: a model may hold the same polygon twice.
            var pa = Polygons(a); var pb = Polygons(b);
            var onlyA = Remaining(pa, pb); var onlyB = Remaining(pb, pa);
            if (onlyA.Count > 0 || onlyB.Count > 0)
                Add(path, "model.polygons", $"{pa.Count}: {Bounded(onlyA.FirstOrDefault())}", $"{pb.Count} ({pa.Count - onlyA.Count} identical): {Bounded(onlyB.FirstOrDefault())}");
        }

        void CompareClassData(WorldNode a, WorldNode b, string path)
        {
            // Stored pointers and runtime counters are not compared.
            HashSet<int> skip = a.Class switch
            {
                WorldNodeClass.World => [0x04, 0x08, 0x0C, 0x80, 0x90, 0x94, 0x98, 0x9C, 0xA0, 0xA4],
                WorldNodeClass.Light => [0xDC, 0xE0],
                WorldNodeClass.Camera => [0, 4, 8, 12],
                _ => [],
            };
            for (int o = 0; o + 4 <= a.Payload.Length; o += 4)
                if (!skip.Contains(o) && BitConverter.ToUInt32(a.Payload, o) != BitConverter.ToUInt32(b.Payload, o))
                    Add(path, $"data+{o}", BitConverter.ToSingle(a.Payload, o), BitConverter.ToSingle(b.Payload, o));
            if (a.Class == WorldNodeClass.Camera)
                foreach (var (x, y, field) in new[] { (a.CameraWorld, b.CameraWorld, "camera.world"), (a.CameraWindow, b.CameraWindow, "camera.window"), (a.CameraHorizon, b.CameraHorizon, "camera.horizon") })
                    if (x?.Name != y?.Name) Add(path, field, x?.Name, y?.Name);
            if (a.Class == WorldNodeClass.World)
            {
                if (!a.WorldLights.Select(l => l.Name).SequenceEqual(b.WorldLights.Select(l => l.Name))) Add(path, "world.lights", string.Join(",", a.WorldLights.Select(l => l.Name)), string.Join(",", b.WorldLights.Select(l => l.Name)));
                if (a.Areas.Count != b.Areas.Count) Add(path, "world.areas", a.Areas.Count, b.Areas.Count);
                else for (int i = 0; i < a.Areas.Count; i++)
                        if (!a.Areas[i].Nodes.Select(n => n.Name).Order().SequenceEqual(b.Areas[i].Nodes.Select(n => n.Name).Order())) Add(path, $"world.area{i}", string.Join(",", a.Areas[i].Nodes.Select(n => n.Name).Order()), string.Join(",", b.Areas[i].Nodes.Select(n => n.Name).Order()));
            }
        }
    }

    private static string PairKey(WorldNode node)
    {
        var at = node.Class == WorldNodeClass.Object3D && WorldUpdate.LocalMatrix(node) is { } m ? Round(m.Translation) : Vector3.Zero;
        return $"{Signature(node)}|{string.Join(",", node.Children.Select(c => c.Name))}|{at}";
    }
    private static string Signature(WorldNode node, int depth = 0) => depth > 6 ? "" :
        $"{node.Class}:{node.Model?.Polygons.Count}:{node.Children.Count}[{string.Join(",", node.Children.Select(c => Signature(c, depth + 1)))}]";
    private static List<string> Polygons(WorldModel m) => m.Polygons.Select(p => string.Join(";", p.Vertices.Select((v, i) => $"{Round(m.Vertices[v])}|{(p.Uvs.Length > 0 ? p.Uvs[i].ToString() : "")}"))
        + $"#{p.Material?.Texture?.Name}{p.Material?.Color}{p.Material?.Flags & 0xFF:X}s{p.Material?.Soil}p{p.Priority}f{p.Flags & 0x100:X}z{p.Zone:X}n{p.Normals.Length > 0}").ToList();
    /// <summary>The entries of <paramref name="a"/> left after removing one match in <paramref name="b"/> for each.</summary>
    private static List<string> Remaining(List<string> a, List<string> b)
    {
        Dictionary<string, int> counts = [];
        foreach (var s in b) counts[s] = counts.GetValueOrDefault(s) + 1;
        List<string> left = [];
        foreach (var s in a) if (counts.GetValueOrDefault(s) > 0) counts[s]--; else left.Add(s);
        return left;
    }
    private static string Bounded(string? text) => text == null ? "" : text.Length > 400 ? text[..400] + "…" : text;
    private static Vector3 Round(Vector3 v) => new(MathF.Round(v.X, 3), MathF.Round(v.Y, 3), MathF.Round(v.Z, 3));
    private static bool Close(Matrix4x4 a, Matrix4x4 b)
    {
        for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) if (Math.Abs(a[r, c] - b[r, c]) > 1e-3f * (1 + Math.Abs(a[r, c]))) return false;
        return true;
    }
    private static string Format(Matrix4x4 m) => $"[{m.M11:G4} {m.M12:G4} {m.M13:G4}; {m.M21:G4} {m.M22:G4} {m.M23:G4}; {m.M31:G4} {m.M32:G4} {m.M33:G4}; {m.M41:G6} {m.M42:G6} {m.M43:G6}]";
}
