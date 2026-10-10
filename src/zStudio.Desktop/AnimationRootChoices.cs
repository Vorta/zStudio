using System.Numerics;
using Recoil.Zbd.Core;

namespace Recoil.Zbd.Desktop;

/// <summary>One first-occurrence actor index per dialog; only the matching page is retained.
/// Duplicate actors remain distinct in the mission. The chooser describes the same first actor as before.</summary>
internal sealed class AnimationRootChoices
{
    internal sealed record Choice(int Index, string Label);
    private readonly Dictionary<int, MissionActor> actors = [];
    private readonly GameScene scene;
    private readonly Func<int, Matrix4x4> worldTransform;
    public AnimationRootChoices(GameScene scene, IEnumerable<MissionActor> actors, Func<int, Matrix4x4> worldTransform, CancellationToken token = default)
    {
        this.scene = scene; this.worldTransform = worldTransform;
        foreach (var actor in actors) { token.ThrowIfCancellationRequested(); this.actors.TryAdd(actor.Root, actor); }
        token.ThrowIfCancellationRequested();
    }
    public IReadOnlyList<Choice> Page(string query, CancellationToken token = default)
    {
        List<Choice> result = [];
        foreach (var node in scene.Nodes)
        {
            token.ThrowIfCancellationRequested();
            string label = $"#{node.Index} · {node.Name} ({node.Class})";
            if (actors.TryGetValue(node.Index, out var actor)) label += $" · {actor.PlacementSource} · {worldTransform(node.Index).Translation}";
            if (!label.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(new(node.Index, label));
            if (result.Count == 500) break;
        }
        token.ThrowIfCancellationRequested(); return result;
    }
}
