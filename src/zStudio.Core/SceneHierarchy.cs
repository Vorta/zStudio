namespace Recoil.Zbd.Core;

public sealed record SceneHierarchyEdge(int Node, string Kind);

/// <summary>Bounded, ordered graph index over parsed scene data. Never changes parenting or poses.</summary>
public sealed class SceneHierarchy
{
    public const int MaximumDepth = 256;
    public GameScene Scene { get; }
    public IReadOnlyList<int> Roots { get; }
    public IReadOnlyList<int> UnlinkedRoots { get; }
    private readonly SceneHierarchyEdge[][] edges;
    private readonly int[][] parents;
    private readonly int[] predecessor;

    public SceneHierarchy(GameScene scene, CancellationToken token = default)
    {
        Scene = scene;
        edges = new SceneHierarchyEdge[scene.Nodes.Count][];
        var incoming = Enumerable.Range(0, scene.Nodes.Count).Select(_ => new List<int>()).ToArray();
        for (int index = 0; index < scene.Nodes.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var node = scene.Nodes[index]; var ordinary = node.Children.ToHashSet();
            edges[index] = SceneBuilder.Children(node).Select(child => new SceneHierarchyEdge(child,
                ordinary.Contains(child) ? "child" : "world partition")).ToArray();
            foreach (var edge in edges[index])
                if (edge.Node >= 0 && edge.Node < scene.Nodes.Count) incoming[edge.Node].Add(index);
        }
        parents = incoming.Select(p => p.Distinct().ToArray()).ToArray();
        var roots = scene.Nodes.Where(n => n.Class == "world").Select(n => n.Index).ToArray();
        if (roots.Length == 0) roots = Enumerable.Range(0, scene.Nodes.Count).Where(i => parents[i].Length == 0).ToArray();
        Roots = roots;
        predecessor = Enumerable.Repeat(-2, scene.Nodes.Count).ToArray();
        foreach (int root in roots) Visit(root);
        List<int> unlinked = [];
        // Visit disconnected roots before cycles, so source ordering cannot put a
        // child above its disconnected parent. Remaining components are cycles.
        foreach (int index in Enumerable.Range(0, scene.Nodes.Count).Where(i => parents[i].Length == 0)
            .Concat(Enumerable.Range(0, scene.Nodes.Count)))
            if (predecessor[index] == -2) { unlinked.Add(index); Visit(index); }
        UnlinkedRoots = unlinked;

        void Visit(int root)
        {
            if (predecessor[root] != -2) return;
            predecessor[root] = -1; Queue<int> queue = new(); queue.Enqueue(root);
            while (queue.TryDequeue(out int node))
            {
                token.ThrowIfCancellationRequested();
                foreach (var edge in edges[node])
                    if (edge.Node >= 0 && edge.Node < edges.Length && predecessor[edge.Node] == -2)
                    { predecessor[edge.Node] = node; queue.Enqueue(edge.Node); }
            }
        }
    }
    public IReadOnlyList<SceneHierarchyEdge> Children(int node) => edges[node];
    public IReadOnlyList<int> Parents(int node) => parents[node];
    public IReadOnlyList<int> PathTo(int node)
    {
        if (node < 0 || node >= predecessor.Length) return [];
        List<int> path = [];
        for (int current = node; current >= 0; current = predecessor[current]) path.Add(current);
        path.Reverse(); return path;
    }
}
