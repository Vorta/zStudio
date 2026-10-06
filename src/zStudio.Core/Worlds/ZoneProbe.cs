using System.Numerics;

namespace Recoil.Zbd.Core.Worlds;

/// <summary>
/// A zone set as the engine keeps it (zTag4): a count and three zone ids. A count of 0 carries no information and passes
/// every test; an id of 0xFF stands for any zone. A polygon's zone word holds one, the count in its low byte.
/// </summary>
public readonly record struct ZoneSet(byte Count, byte Id0, byte Id1, byte Id2)
{
    /// <summary>zTag4::Clear: no information.</summary>
    public static ZoneSet Cleared { get; } = new(0, 0xFF, 0xFF, 0xFF);
    public static ZoneSet FromWord(uint word) => new((byte)word, (byte)(word >> 8), (byte)(word >> 16), (byte)(word >> 24));
    public uint Word => Count | (uint)Id0 << 8 | (uint)Id1 << 16 | (uint)Id2 << 24;
    /// <summary>The ids in use. The engine reads as many as the count says; no shipped zone word counts more than three.</summary>
    public IEnumerable<byte> Ids { get { if (Count > 0) yield return Id0; if (Count > 1) yield return Id1; if (Count > 2) yield return Id2; } }
    /// <summary>VariantTag::CurrentAllowsId (retail 0x476400) with this set current: whether a node of <paramref name="zone"/> passes.</summary>
    public bool Allows(byte zone) => zone == 0xFF || Count == 0 || Ids.Any(id => id == 0xFF || id == zone);
    /// <summary>VariantTag::TagsOverlap (retail 0x476370).</summary>
    public bool Overlaps(ZoneSet other) => Count == 0 || other.Count == 0 || Ids.Any(a => a == 0xFF || other.Ids.Any(b => b == 0xFF || b == a));
    public override string ToString() => Count == 0 ? "none" : string.Join('+', Ids.Select(id => id == 0xFF ? "any" : id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
}

/// <summary>The engine's two vertical probes, which differ in what a node contributes.</summary>
public enum ZoneProbeKind
{
    /// <summary>One point (BuildPickCandidateListBelowPoint, retail 0x443d20): the camera, the renderer and spawning. A node gives the first polygon in stored order that holds the point.</summary>
    Point,
    /// <summary>A vehicle's sample points (BuildPickCandidatesForPointBatch, retail 0x4444b0): a node gives every polygon that faces up and holds the point.</summary>
    Vehicle,
}

/// <summary>A surface under a probe point: its height there, its polygon's zones and soil, and its node and polygon.</summary>
public readonly record struct ZoneHit(float Height, ZoneSet Zones, uint Soil, WorldNode Node, WorldPolygon Polygon);

/// <summary>The surfaces a probe found, in the engine's order, and whether its 32-entry buffer filled up (the engine drops the rest).</summary>
public sealed record ZoneProbeHits(IReadOnlyList<ZoneHit> Hits, bool Full);

/// <summary>
/// The engine's zone probes and zone updates on a compiled world, as the retail executable runs them. Every gameplay probe
/// is a vertical line at a point's x and z from y = 500 down, through the nodes of the point's grid cell and then the
/// world's overflow list (outside the grid, with the world's clamp flag off as in every shipped world, the point probe
/// searches only the overflow list and the vehicle probe nothing). A node takes part when it is active (0x04) and an
/// altitude surface (0x08) and, with its zone gate (0x01000000), when the current zone set allows its zone; a LOD group
/// only when its band starts at the viewer (near range² ≤ 5); a node with siblings only when its cached box holds the
/// point in plan. A polygon holds a point when every edge keeps it on the inner side of an upward winding (within 0.0001).
/// </summary>
public static class ZoneProbe
{
    /// <summary>Every gameplay probe starts at y = 500; a surface higher than that at the point is passed over.</summary>
    public const float Top = 500;
    public const int MaximumHits = 32;
    public const uint ActiveFlag = 0x04, AltitudeFlag = 0x08, BoundsFlag = 0x100, GateFlag = 0x01000000;
    /// <summary>The water soil, which probes skip when asked (Player::SelectProbeSampleHeightFromCandidates).</summary>
    public const uint WaterSoil = 1;
    /// <summary>How far above the camera a surface may lie and still be under it (retail 0.001).</summary>
    public const float CameraWindow = 0.001f;
    /// <summary>How far above a spawn point the ground may lie (Player::SampleGroundAndAlignRootToSurface).</summary>
    public const float SpawnWindow = 4;
    private const double EdgeTolerance = 1e-4;
    private const float NoHit = -300, Lowest = -250;

    /// <summary>The surfaces under (x, z) that a probe of <paramref name="kind"/> finds, with <paramref name="current"/> as the zone set its gate tests.</summary>
    public static ZoneProbeHits Probe(WorldNode world, float x, float z, ZoneSet current, ZoneProbeKind kind = ZoneProbeKind.Point, float top = Top)
    {
        if (world.Class != WorldNodeClass.World) throw new ArgumentException("A world node is required.", nameof(world));
        int columns = world.PayloadInt(0x78), rows = world.PayloadInt(0x7C);
        int column = Cell(((double)x - world.PayloadFloat(0x34)) * world.PayloadFloat(0x64));
        int row = Cell(((double)z - world.PayloadFloat(0x38)) * world.PayloadFloat(0x68));
        Search search = new(x, z, top, current, kind);
        WorldArea? area = null; bool inGrid = false;
        if (column >= 0 && column < columns && row >= 0 && row < rows) { area = Area(column, row); inGrid = true; }
        else if (world.PayloadInt(0x50) != 0)
        {
            // Clamped: the edge cell is searched with the point moved into it, as if the edge cell repeated outward. The
            // engine clamps as below (an empty grid has no edge cell) and takes the difference in 32 bits.
            int clampedColumn = column > columns - 1 ? columns - 1 : column < 0 ? 0 : column, clampedRow = row > rows - 1 ? rows - 1 : row < 0 ? 0 : row;
            search.X += unchecked(clampedColumn - column) * world.PayloadFloat(0x54); search.Z += unchecked(clampedRow - row) * world.PayloadFloat(0x58);
            area = Area(clampedColumn, clampedRow); inGrid = true;
        }
        if (area != null)
            foreach (var node in area.Nodes) search.Visit(node, area.Nodes.Count + 1);
        (search.X, search.Z) = (x, z);
        // A vehicle's point outside the grid with the clamp off is inactive for the overflow list as well.
        if (kind == ZoneProbeKind.Point || inGrid)
            foreach (var node in world.Children) search.Visit(node, world.Children.Count + 1);
        return new(search.Hits, search.Full);

        WorldArea? Area(int c, int r) => c >= 0 && r >= 0 && c < columns && r < rows && (long)r * columns + c < world.Areas.Count ? world.Areas[r * columns + c] : null;
    }

    /// <summary>
    /// The grid cell of a scaled coordinate as the engine converts it ((int)floor(), x87): a value outside the 32-bit range,
    /// or not a number, becomes −2³¹ (outside the grid), where .NET would saturate or give 0.
    /// </summary>
    private static int Cell(double value) => Math.Floor(value) is var cell && cell >= int.MinValue && cell <= int.MaxValue ? (int)cell : int.MinValue;

    /// <summary>
    /// Player::SelectProbeSampleHeightFromCandidates (retail 0x4290f0): the highest hit no more than <paramref name="window"/>
    /// above <paramref name="y"/>; without one, hit 0 (the first found) and the height of the hit nearest to y. Water hits
    /// are passed over entirely when <paramref name="skipWater"/>. Hits at or below y = −250 are never chosen.
    /// </summary>
    public static (int Index, float Height) Select(IReadOnlyList<ZoneHit> hits, float y, float window, bool skipWater)
    {
        if (hits.Count == 0) return (0, y);
        float selected = Lowest, nearest = NoHit, nearestDistance = 10000.9f; int index = 0;
        for (int i = 0; i < hits.Count; i++)
        {
            if (skipWater && hits[i].Soil == WaterSoil) continue;
            float height = hits[i].Height, distance = Math.Abs(height - y);
            if (distance < nearestDistance) { nearestDistance = distance; nearest = height; }
            if (height > selected && height - window <= y) { selected = height; index = i; }
        }
        return (index, selected == Lowest && nearest != NoHit ? nearest : Math.Max(selected, Lowest));
    }

    /// <summary>
    /// The main camera's zones after a probe at its eye (Player::UpdateCameraVariantFromCameraPos 0x406470, or in the
    /// third-person views AdjustThirdPersonCameraBySideProbes 0x406110, which skips water in the submarine; then
    /// UpdateCameraVariantFromAnchor 0x406510). The gate tests the previous zones. The chosen hit's zones, with the player's
    /// own added while there is room for three, replace them, unless the hit has none, the result names zone 0xFF, or
    /// nothing was hit: then the previous zones stay.
    /// </summary>
    public static ZoneSet CameraZones(WorldNode world, Vector3 eye, ZoneSet previous, ZoneSet player, bool skipWater = false)
    {
        var probe = Probe(world, eye.X, eye.Z, previous);
        if (probe.Hits.Count == 0) return previous;
        var hit = probe.Hits[Select(probe.Hits, eye.Y, CameraWindow, skipWater).Index].Zones;
        if (hit.Count == 0) return previous;
        byte[] ids = [hit.Id0, hit.Id1, hit.Id2]; int count = Math.Min((int)hit.Count, 3);
        foreach (byte id in player.Ids)
            if (!ids.Take(count).Contains(id) && count < 3) ids[count++] = id;
        return ids.Take(count).Contains((byte)0xFF) ? previous : new((byte)count, ids[0], ids[1], ids[2]);
    }

    /// <summary>
    /// A vehicle's zones and root zone after its probe (Player::ProbeModalSampleHeights 0x428d60, BuildEnvironmentProbeResult
    /// 0x42cf90), from its first sample point. The gate tests the vehicle's previous zones. The chosen hit's zones replace
    /// them, even with no information (count 0, which passes every test); the root node takes the zone of the top-level
    /// node the vehicle stands on. Without a hit the zones stay and the root zone becomes 0xFF (any).
    /// </summary>
    /// <param name="window">How far above the sample the ground may lie: min(1 − vertical speed × tick, 4) while moving.</param>
    public static (ZoneSet Zones, byte RootZone) VehicleZones(WorldNode world, Vector3 sample, ZoneSet previous, float window, bool skipWater = false)
    {
        var probe = Probe(world, sample.X, sample.Z, previous, ZoneProbeKind.Vehicle);
        if (probe.Hits.Count == 0) return (previous, 0xFF);
        var hit = probe.Hits[Select(probe.Hits, sample.Y, window, skipWater).Index];
        return (hit.Zones, TopLevel(hit.Node) is { } top ? (byte)top.Zone : hit.Zones.Id0);
    }

    /// <summary>
    /// A vehicle's zones where it spawns (Player::SampleGroundAndAlignRootToSurface 0x421830): cleared, then the ground no
    /// more than 4 units above the spawn point, skipping water until the amphibious mode is unlocked. Without ground they
    /// stay cleared (every zone) and the root zone is 0xFF. Mission start then gives the camera the local player's zones.
    /// </summary>
    public static (ZoneSet Zones, byte RootZone) SpawnZones(WorldNode world, Vector3 position, bool amphibious)
    {
        var probe = Probe(world, position.X, position.Z, ZoneSet.Cleared);
        if (probe.Hits.Count == 0) return (ZoneSet.Cleared, 0xFF);
        var hit = probe.Hits[Select(probe.Hits, position.Y, SpawnWindow, !amphibious).Index];
        return (hit.Zones, TopLevel(hit.Node) is { } top ? (byte)top.Zone : hit.Zones.Id0);
    }

    /// <summary>
    /// The zones a camera without the game's override renders with (the renderer's own probe, retail 0x44d600 via
    /// FindBestPickCandidateBelowPoint 0x443c70): no gate, from the eye down, the highest hit (at equal height one with
    /// zones); a hit without zones keeps the previous ones and no hit clears them, so every zone is drawn.
    /// </summary>
    public static ZoneSet ViewZones(WorldNode world, Vector3 eye, ZoneSet previous)
    {
        var hits = Probe(world, eye.X, eye.Z, ZoneSet.Cleared, ZoneProbeKind.Point, eye.Y).Hits;
        if (hits.Count == 0) return ZoneSet.Cleared;
        var best = hits[0];
        foreach (var hit in hits.Skip(1))
            if (hit.Height <= eye.Y && (best.Height > eye.Y || hit.Height > best.Height || hit.Height == best.Height && best.Zones.Count == 0)) best = hit;
        return best.Zones.Count > 0 ? best.Zones : previous;
    }

    /// <summary>gwNodeGetWorldChild: the ancestor that sits directly in the world (a cell or the overflow list).</summary>
    public static WorldNode? TopLevel(WorldNode node)
    {
        for (int depth = 0; depth <= WorldUpdate.MaximumDepth; depth++)
        {
            if (node.Parents.Any(p => p.Class == WorldNodeClass.World) || node.GridColumn >= 0) return node;
            if (node.Parents.Count == 0) return null;
            node = node.Parents[0];
        }
        return null;
    }

    /// <summary>
    /// The most one probe visits: nodes (once per path, as the engine walks a node several parents share) and the vertices
    /// it places. A world with every node in the probe's cell stays far below; a malformed one (a cycle, or nodes shared
    /// along exponentially many paths) is refused beyond it.
    /// </summary>
    public const int MaximumVisits = 16 * GameZWorld.MaximumNodeCapacity, MaximumVertices = 64 * 1024 * 1024;

    private sealed class Search(float x, float z, float top, ZoneSet current, ZoneProbeKind kind)
    {
        public float X = x, Z = z;
        public List<ZoneHit> Hits { get; } = [];
        public bool Full { get; private set; }
        private long visits, vertices;

        /// <summary>
        /// BuildPickCandidateList (0x443f80) / BuildPickCandidatesForPoints (0x444890) for one node and its subtree, depth
        /// first in child order as the engine recurses (walked with a stack of its own, so a deep world cannot overflow
        /// ours). Object3d nodes test their model and visit their children; a LOD group its children when its band starts
        /// at the viewer; a camera (no sibling test) and a light their children under their own transform (0x4441c1,
        /// 0x4443e0, 0x444a52, 0x444d10). Sound nodes and other classes stop the walk.
        /// </summary>
        public void Visit(WorldNode top, int siblings)
        {
            Stack<(WorldNode Node, int Siblings, Matrix4x4 Parent, int Depth)> pending = new([(top, siblings, Matrix4x4.Identity, 0)]);
            while (pending.TryPop(out var item))
            {
                var (node, count, parent, depth) = item;
                if ((node.Flags & ActiveFlag) == 0 || (node.Flags & AltitudeFlag) == 0) continue;
                if ((node.Flags & GateFlag) != 0 && !current.Allows((byte)node.Zone)) continue;
                if (kind == ZoneProbeKind.Point && Hits.Count >= MaximumHits) { Full = true; continue; }
                if (depth > WorldUpdate.MaximumDepth) throw new InvalidDataException($"The node hierarchy below {node.Name} is cyclic or deeper than {WorldUpdate.MaximumDepth} levels.");
                if (++visits > MaximumVisits) throw new InvalidDataException($"The probe would visit more than {MaximumVisits:N0} nodes; the world shares nodes along too many paths.");
                Matrix4x4 matrix;
                switch (node.Class)
                {
                    case WorldNodeClass.Object3D:
                        matrix = WorldUpdate.LocalMatrix(node) is { } local ? local * parent : parent;
                        if (count > 1 && Outside(node, matrix)) continue;
                        if (node.Model is { } model) Test(node, model, matrix);
                        break;
                    case WorldNodeClass.Lod:
                        if (node.PayloadFloat(4) > 5 || count > 1 && Outside(node, parent)) continue;
                        matrix = parent;
                        break;
                    case WorldNodeClass.Camera or WorldNodeClass.Light:
                        // A light keeps the bounds it was created with, tested like any node's; a camera's are not tested.
                        if (node.Class == WorldNodeClass.Light && count > 1 && Outside(node, parent)) continue;
                        // Translation (+0x14) and local Euler angles, turned Y, X, Z as the engine's matrix stack applies them:
                        // a camera's at +0x20 (MatApplyLocalTRS, 0x4441c1), a light's orientation at +0x08 (0x4443e0, 0x444d10;
                        // its +0x20 holds the world angles the update derives, which already include the parents' turns).
                        int angles = node.Class == WorldNodeClass.Light ? 0x08 : 0x20;
                        var at = new Vector3(node.PayloadFloat(0x14), node.PayloadFloat(0x18), node.PayloadFloat(0x1C));
                        var turn = new Vector3(node.PayloadFloat(angles), node.PayloadFloat(angles + 4), node.PayloadFloat(angles + 8));
                        matrix = Matrix4x4.CreateFromYawPitchRoll(turn.Y, turn.X, turn.Z) * Matrix4x4.CreateTranslation(at) * parent;
                        break;
                    default: continue;
                }
                for (int i = node.Children.Count - 1; i >= 0; i--) pending.Push((node.Children[i], node.Children.Count, matrix, depth + 1));
            }
        }

        /// <summary>IsPickQueryPointOutsideViewBBoxXZ (0x4472c0) / PickTestBBox2D (0x4473e0): no cached box counts as outside.</summary>
        private bool Outside(WorldNode node, Matrix4x4 matrix)
        {
            if ((node.Flags & BoundsFlag) == 0) return true;
            var box = WorldBox.Of(node.CachedBounds.Corners().Select(c => Vector3.Transform(c, matrix)));
            return !(X >= box.Min.X && X <= box.Max.X && Z >= box.Min.Z && Z <= box.Max.Z);
        }

        /// <summary>zDi::BuildPickCandidateForQueryPoint (0x484960) / PickTestMeshAtQueryXZ (0x484e00) with AddFaceToPlayerProbeSampleBuckets (0x484b70).</summary>
        private void Test(WorldNode node, WorldModel model, Matrix4x4 matrix)
        {
            if ((this.vertices += model.Vertices.Count) > MaximumVertices) throw new InvalidDataException($"The probe would place more than {MaximumVertices:N0} vertices; the world shares models along too many paths.");
            var vertices = model.Vertices.ToArray();
            if ((model.Flags & 0x08) != 0 && model.MorphFactor != 0)
                for (int i = 0; i < Math.Min(model.Morphs.Count, vertices.Length); i++) vertices[i] += model.Morphs[i] * model.MorphFactor;
            if (!matrix.IsIdentity) for (int i = 0; i < vertices.Length; i++) vertices[i] = Vector3.Transform(vertices[i], matrix);
            foreach (var polygon in model.Polygons)
            {
                if (polygon.Vertices.Length < 3 || polygon.Vertices.Any(v => v < 0 || v >= vertices.Length)) continue;
                var points = polygon.Vertices.Select(v => vertices[v]).ToArray();
                var normal = Vector3.Normalize(Vector3.Cross(points[1] - points[0], points[2] - points[0]));
                if (kind == ZoneProbeKind.Vehicle && !(normal.Y > 0)) continue;
                if (!Holds(points)) continue;
                float height = normal.Y == 0 ? points[0].Y : points[0].Y - ((X - points[0].X) * normal.X + (Z - points[0].Z) * normal.Z) / normal.Y;
                if (!(height <= top)) continue;
                // The vehicle's buffer drops a surface found once it holds 32 (AddFaceToPlayerProbeSampleBuckets, 0x484b70).
                if (kind == ZoneProbeKind.Vehicle && Hits.Count >= MaximumHits) { Full = true; return; }
                Hits.Add(new(height, ZoneSet.FromWord(polygon.Zone), polygon.Material?.Soil ?? 0, node, polygon));
                if (kind == ZoneProbeKind.Point) return;
            }
        }

        /// <summary>TryGetPolygonHitAtQueryXZ (0x4856d0): every edge keeps the point on its inner side.</summary>
        private bool Holds(Vector3[] points)
        {
            for (int i = 0; i < points.Length; i++)
            {
                var previous = points[i == 0 ? points.Length - 1 : i - 1]; var currentPoint = points[i];
                double edge = ((double)X - previous.X) * ((double)currentPoint.Z - previous.Z) + ((double)Z - previous.Z) * ((double)previous.X - currentPoint.X);
                if (edge <= -EdgeTolerance) return false;
            }
            return true;
        }
    }
}
