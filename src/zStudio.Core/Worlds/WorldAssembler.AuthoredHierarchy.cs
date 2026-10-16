using System.Collections.ObjectModel;

namespace zStudio.Core.Worlds;

/// <summary>An imported source node, with source identity separate from its optional compiled slot.</summary>
public sealed record SourceAuthoredNode(int Id, int? CompiledNode, string Name, string File, int SourceNode,
    bool Group, IReadOnlyList<int> Children, WorldNodeProvenance Origin);

/// <summary>The database's authored roots beneath an actual compiled world.</summary>
public sealed record SourceAuthoredRoot(int World, IReadOnlyList<int> Children);
public sealed record SourceCompiledEdge(int Parent, int Child);

/// <summary>Frozen topology captured from the shared glTF import, before script compilation deletes groups.</summary>
public sealed record SourceAuthoredHierarchy(IReadOnlyList<SourceAuthoredNode> Nodes, IReadOnlyList<SourceAuthoredRoot> Roots)
{
    public static SourceAuthoredHierarchy Empty { get; } = new([], []);
    /// <summary>Automatic database attachments, excluding pairs explicitly attached by a script.</summary>
    public IReadOnlyList<SourceCompiledEdge> FlattenedEdges { get; init; } = [];
    public IReadOnlyList<SourceCompiledEdge> ScriptEdges { get; init; } = [];
}

public sealed partial class WorldAssembler
{
    private sealed record AuthoredCapture(WorldNode Node, WorldNode[] Children, bool Group);
    private readonly List<AuthoredCapture> authoredNodes = [];
    private readonly List<(WorldNode World, WorldNode[] Children)> authoredRoots = [];
    private long authoredEdges;
    private readonly HashSet<(WorldNode Parent, WorldNode Child)> flattenedEdges = [], explicitEdges = [];
    internal long AuthoredHierarchyFootprint => 128L * authoredNodes.Count + 4L * authoredEdges + 48L * authoredRoots.Count + 32L * (flattenedEdges.Count + explicitEdges.Count);

    private void CaptureAttachment(WorldNode parent, WorldNode child, bool automatic)
    {
        var edges = automatic ? flattenedEdges : explicitEdges;
        if (edges.Count >= 800_000) throw new InvalidDataException("The source attachment hierarchy exceeds its presentation limit.");
        edges.Add((parent, child));
    }

    private void CaptureAuthoredHierarchy(WorldNode world, IReadOnlyList<WorldNode> roots, HashSet<WorldNode> groups)
    {
        HashSet<WorldNode> seen = new(ReferenceEqualityComparer.Instance);
        Stack<WorldNode> pending = new(roots.Reverse());
        if ((authoredEdges += roots.Count) > 800_000) throw new InvalidDataException("The authored hierarchy exceeds its edge limit.");
        authoredRoots.Add((world, roots.ToArray()));
        while (pending.TryPop(out var node))
        {
            token.ThrowIfCancellationRequested();
            if (!seen.Add(node)) continue;
            if (authoredNodes.Count >= 200_000 || (authoredEdges += node.Children.Count) > 800_000)
                throw new InvalidDataException("The authored hierarchy exceeds its presentation limit.");
            var children = node.Children.ToArray();
            authoredNodes.Add(new(node, children, groups.Contains(node)));
            for (int i = children.Length - 1; i >= 0; i--) pending.Push(children[i]);
        }
    }

    /// <summary>Seal captured relationships with the final slot map. Deleted groups never borrow reused slots.</summary>
    public SourceAuthoredHierarchy AuthoredHierarchy(IReadOnlyDictionary<WorldNode, int> slots)
    {
        Dictionary<WorldNode, int> ids = new(ReferenceEqualityComparer.Instance);
        foreach (var capture in authoredNodes) { token.ThrowIfCancellationRequested(); ids.TryAdd(capture.Node, ids.Count); }
        List<SourceAuthoredNode> nodes = [];
        foreach (var capture in authoredNodes)
        {
            token.ThrowIfCancellationRequested();
            if (ids[capture.Node] != nodes.Count) continue;
            var origin = Origin(capture.Node);
            nodes.Add(new(nodes.Count, slots.TryGetValue(capture.Node, out int slot) ? slot : null,
                origin.ModelNodeName ?? capture.Node.Name, origin.ModelFile ?? "", origin.ModelNode, capture.Group,
                Array.AsReadOnly(capture.Children.Select(n => ids[n]).ToArray()), origin));
        }
        var roots = authoredRoots.Where(r => slots.ContainsKey(r.World)).Select(r =>
            new SourceAuthoredRoot(slots[r.World], Array.AsReadOnly(r.Children.Select(n => ids[n]).ToArray()))).ToArray();
        List<SourceCompiledEdge> flattened = [];
        foreach (var edge in flattenedEdges)
        {
            token.ThrowIfCancellationRequested();
            if (!explicitEdges.Contains(edge) && slots.TryGetValue(edge.Parent, out int parent) && slots.TryGetValue(edge.Child, out int child))
                flattened.Add(new(parent, child));
        }
        List<SourceCompiledEdge> scripted = [];
        long scanned = 0;
        // Inspect final live parent adjacency once, rather than extending mutating Unlink's scan/shift work.
        // A removed script pair must not publish merely because its two endpoints survived elsewhere.
        // World partitioning moves children into cell lists but retains their world parent relationship.
        foreach (var childNode in slots.Keys)
        {
            token.ThrowIfCancellationRequested();
            HashSet<WorldNode> seenParents = new(ReferenceEqualityComparer.Instance);
            foreach (var parentNode in childNode.Parents)
            {
                token.ThrowIfCancellationRequested();
                if (++scanned > 800_000) throw new InvalidDataException("The source attachment hierarchy exceeds its presentation limit.");
                if (seenParents.Add(parentNode) && explicitEdges.Contains((parentNode, childNode)) && slots.TryGetValue(parentNode, out int parent))
                    scripted.Add(new(parent, slots[childNode]));
            }
        }
        return new(new ReadOnlyCollection<SourceAuthoredNode>(nodes), Array.AsReadOnly(roots)) { FlattenedEdges = flattened.AsReadOnly(), ScriptEdges = scripted.AsReadOnly() };
    }
}
