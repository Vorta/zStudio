using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Gltf;

namespace Recoil.Zbd.Core.Worlds;

/// <summary>One load/authoring operation's allowance, shared by all terrain source documents and lookups.</summary>
internal sealed class TerrainPlacementBudget(CancellationToken token, long maximumWork = GltfDocument.MaximumEntries,
    long maximumText = GltfDocument.MaximumMetadataBytes, Action? visited = null)
{
    private long work, text;
    internal CancellationToken Token => token;
    internal long NodeVisits { get; private set; }
    internal long Lookups { get; private set; }
    internal void Node()
    {
        Take(); NodeVisits++; visited?.Invoke(); token.ThrowIfCancellationRequested();
    }
    internal void Lookup(string name) { Take(); Text(name.Length); Lookups++; }
    internal void Take()
    {
        token.ThrowIfCancellationRequested();
        if (work >= maximumWork) throw Refusal();
        work++;
    }
    internal void Text(long count)
    {
        token.ThrowIfCancellationRequested();
        if (count < 0 || count > maximumText - text) throw Refusal();
        text += count;
    }
    internal void Scalar(JsonNode? value)
    {
        // Measure a cold JSON scalar before GetString/number parsing can materialize its text.
        if (value is not JsonValue scalar) return;
        if (scalar.TryGetValue(out JsonElement element)) Text(JsonMarshal.GetRawUtf8Value(element).Length);
        else if (scalar.TryGetValue(out string? text)) Text(text?.Length ?? 0);
    }
    internal void CheckCancellation() => token.ThrowIfCancellationRequested();
    private static InvalidDataException Refusal() => new("Terrain placement and name lookup exceeds its aggregate traversal or text budget; reduce the source hierarchy or the number of terrain source files.");
}

/// <summary>
/// Immutable-document scene placements, indexed once by exact engine name. Two occurrences remain ambiguous even
/// when they share a mesh, instance mark or node identity. Only the current hierarchy path occupies the traversal stack.
/// </summary>
internal sealed class TerrainPlacementIndex
{
    internal readonly record struct Placement(GltfNode Node, Matrix4x4 World, bool InheritedAppearance, uint Zone, bool ExplicitZone, uint? Flags);
    private readonly Dictionary<string, Placement?> named = new(StringComparer.Ordinal);
    private readonly TerrainPlacementBudget budget;
    private readonly record struct Frame(Placement At, bool Appearance, int NextChild);

    internal TerrainPlacementIndex(GltfDocument document, TerrainPlacementBudget budget, WorldZoneProfile? zones = null)
    {
        this.budget = budget;
        Dictionary<GltfNode, WorldNodeZone>? assigned = null;
        if (zones != null)
        {
            var layout = WorldGltf.ZoneLayout.Read(document, budget.Token);
            WorldGltf.ValidateZoneProfile(layout, zones, "The terrain surface", budget.Token);
            assigned = new(ReferenceEqualityComparer.Instance);
            for (int i = 0; i < layout.Nodes.Count; i++)
            {
                budget.Node(); assigned.Add(layout.Nodes[i], zones.Nodes[i]);
            }
        }
        Stack<Frame> path = new();
        HashSet<GltfNode> active = new(ReferenceEqualityComparer.Instance);
        foreach (var root in document.Roots)
        {
            Enter(root, Matrix4x4.Identity, false, zones?.LoadRoot?.Word & 255 ?? 0xFF, zones?.LoadRoot is { Inherit: false });
            while (path.TryPop(out var frame))
            {
                budget.CheckCancellation();
                if (frame.NextChild == frame.At.Node.Children.Count) { active.Remove(frame.At.Node); continue; }
                var child = frame.At.Node.Children[frame.NextChild];
                path.Push(frame with { NextChild = frame.NextChild + 1 });
                Enter(child, frame.At.World, frame.Appearance, frame.At.Zone, frame.At.ExplicitZone);
            }
        }

        void Enter(GltfNode node, Matrix4x4 parent, bool inheritedAppearance, uint inheritedZone, bool explicitZone)
        {
            // Admission precedes matrix multiplication, name decoding, dictionary growth and stack retention.
            budget.Node();
            if (path.Count > GltfDocument.MaximumDepth || !active.Add(node))
                throw new InvalidDataException("The terrain source node hierarchy is cyclic or too deep.");
            var extras = node.Extras?[WorldGltf.Key] as JsonObject;
            budget.Text(node.Name.Length);
            budget.Scalar(extras?["name"]); budget.Scalar(extras?["zone"]); budget.Scalar(extras?["zoneWord"]); budget.Scalar(extras?["flags"]);
            string name = WorldGltf.EngineName(node);
            uint? ownZone = WorldGltf.StatedZone(extras);
            uint? flags = extras?["flags"] is JsonValue f && f.TryGetValue(out string? hex)
                && uint.TryParse(hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex.AsSpan(2) : hex.AsSpan(),
                    System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint parsed)
                && (parsed & WorldGltf.CarriedFlags) != WorldGltf.DefaultCarried ? parsed & WorldGltf.CarriedFlags : null;
            if (assigned != null)
            {
                var zone = assigned[node];
                ownZone = zone.Inherit ? null : zone.Word & 255;
                uint carried = flags ?? WorldGltf.DefaultCarried;
                carried = zone.Gate ? carried | WorldGltf.ZoneGate : carried & ~WorldGltf.ZoneGate;
                flags = carried == WorldGltf.DefaultCarried ? null : carried;
            }
            Placement placement = new(node, (node.Matrix ?? Matrix4x4.Identity) * parent, inheritedAppearance,
                ownZone ?? inheritedZone, ownZone != null || explicitZone, flags);
            if (!named.TryAdd(name, placement)) named[name] = null;
            path.Push(new(placement, inheritedAppearance || extras?.ContainsKey("appearance") == true, 0));
        }
    }

    internal bool TryGet(string name, out Placement placement, out bool ambiguous)
    {
        budget.Lookup(name);
        ambiguous = named.TryGetValue(name, out var value) && value == null;
        placement = value.GetValueOrDefault();
        return value.HasValue;
    }

    internal IReadOnlyList<string> MeshNames()
    {
        List<string> result = [];
        foreach (var (name, placement) in named)
        {
            budget.Lookup(name);
            if (placement is { Node.Mesh: not null }) result.Add(name);
        }
        return result;
    }
}
