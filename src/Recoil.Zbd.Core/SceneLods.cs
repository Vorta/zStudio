namespace Recoil.Zbd.Core;

/// <summary>Manual detail ranks within each parent's LOD distance bands, independent of camera distance.</summary>
public sealed class SceneLods
{
    private readonly GameScene scene;
    private readonly Dictionary<int, float[]> bands = [];
    public SceneLods(GameScene scene)
    {
        this.scene = scene;
        foreach (var parent in scene.Nodes)
        {
            float[] near = parent.Children.Where(Valid).Select(i => scene.Nodes[i]).Where(n => n.Class == "lod")
                .Select(n => n.Data.Float("range_near_sq")).Where(float.IsFinite).Distinct().Order().ToArray();
            if (near.Length > 0) bands[parent.Index] = near;
        }
    }
    private bool Valid(int index) => index >= 0 && index < scene.Nodes.Count;
    public bool Includes(int parent, int child, int level)
    {
        if (!Valid(child) || scene.Nodes[child].Class != "lod") return true;
        var node = scene.Nodes[child];
        float distance = bands.TryGetValue(parent, out var values) ? values[Math.Clamp(level, 0, values.Length - 1)] : node.Data.Float("range_near_sq");
        // Manual selection chooses one authored band even when fade ranges overlap.
        // Several sibling nodes with the same near boundary can be parts of that band.
        return distance == node.Data.Float("range_near_sq") && distance < node.Data.Float("range_far_sq", float.MaxValue);
    }
    public int Count(IEnumerable<int>? roots = null)
    {
        if (roots == null) return Math.Max(1, bands.Values.Select(v => v.Length).DefaultIfEmpty(1).Max());
        HashSet<int> seen = []; Stack<int> pending = new(roots); int count = 1;
        while (pending.TryPop(out int index))
        {
            if (!Valid(index) || !seen.Add(index)) continue;
            if (bands.TryGetValue(index, out var values)) count = Math.Max(count, values.Length);
            foreach (int child in SceneBuilder.Children(scene.Nodes[index])) pending.Push(child);
        }
        return count;
    }
    /// <summary>Expand a raw model under an LOD to its variant group so the picker can show sibling variants.</summary>
    public static int? PreviewRoot(GameScene scene, AssetRecord asset)
    {
        int? index = asset.Content is GameNode node ? node.Index : asset.Kind == AssetKind.Model ? scene.Nodes.FirstOrDefault(n => n.ModelIndex == asset.Index)?.Index : null;
        if (index == null) return null;
        int current = index.Value; HashSet<int> seen = [];
        while (current >= 0 && current < scene.Nodes.Count && seen.Add(current))
        {
            var n = scene.Nodes[current];
            if (n.Class == "lod") return n.Parents.FirstOrDefault(p => p >= 0 && p < scene.Nodes.Count, current);
            if (n.Parents.Length != 1) break;
            current = n.Parents[0];
        }
        return asset.Content is GameNode ? index : null;
    }
    public static string[] Choices(int count) => Enumerable.Range(0, Math.Max(1, count))
        .Select(i => i == 0 ? "LOD 0 · Highest detail" : $"LOD {i} · Lower detail").ToArray();
}
