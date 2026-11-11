using System.Text.Json.Nodes;
using zStudio.Core.Worlds;

namespace zStudio.Core;

public sealed partial class MissionSceneContext
{
    internal IReadOnlyDictionary<int, GameNode>? ValveStartupBefore { get; init; }
    internal IReadOnlyDictionary<int, GameNode>? ValveLoadBefore { get; init; }
    /// <summary>Startup-only fields are restored on a private scene; placement, zones and authored fields remain current.</summary>
    public MissionSceneContext ValveStartupBaseline(CancellationToken token = default)
    {
        GameScene scene = new(); scene.Models.AddRange(Scene.Models); scene.Materials.AddRange(Scene.Materials); scene.Textures.AddRange(Scene.Textures);
        var placed = Actors.Select(a => a.Root).ToHashSet();
        foreach (var node in Scene.Nodes)
        {
            token.ThrowIfCancellationRequested();
            var startup = ValveStartupBefore?.GetValueOrDefault(node.Index); var load = ValveLoadBefore?.GetValueOrDefault(node.Index);
            if (startup == null && load == null) { scene.Nodes.Add(node); continue; }
            var data = (JsonObject)node.Data.DeepClone(); var metadata = (JsonObject)node.Metadata.DeepClone();
            int? model = node.ModelIndex;
            Restore(startup, false); Restore(load, placed.Contains(node.Index));
            scene.Nodes.Add(node with { Data = data, Metadata = metadata, ModelIndex = model });
            void Restore(GameNode? before, bool keepPlacement)
            {
                if (before == null) return;
                foreach (string field in new[] { "transform", "rotate", "scale", "translate", "opacity", "flags" })
                {
                    if (keepPlacement && field is "transform" or "rotate" or "scale" or "translate") continue;
                    if (keepPlacement && field == "flags")
                    { data[field] = (before.Data.UInt(field) & ~WorldGltf.ObjectTransformFlags) | (data.UInt(field) & WorldGltf.ObjectTransformFlags); continue; }
                    if (before.Data[field] is { } value) data[field] = value.DeepClone(); else data.Remove(field);
                }
                if (!keepPlacement) metadata["flags"] = (metadata.UInt("flags") & ~4u) | (before.Metadata.UInt("flags") & 4u);
                model = before.ModelIndex;
            }
        }
        return new(scene, SourceNodes.ToList(), Actors.ToList(), [.. DormantRoots], Diagnostics, Layout, originalNodeCount, token, previewDiagnostics: PreviewDiagnostics)
        { ValveLoadBefore = ValveLoadBefore, AiNetworks = AiNetworks, StartupAnimations = StartupAnimations, AnimationEntryRoots = AnimationEntryRoots, PlayerStart = PlayerStart, LocalPlayer = LocalPlayer };
    }
}
