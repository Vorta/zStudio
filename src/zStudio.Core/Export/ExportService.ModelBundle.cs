using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core.Export;

public sealed record ModelBundleResult(string Directory, string Manifest, int Models, int Placements);

public sealed partial class ExportService
{
    /// <summary>All authored descendants, including inactive LODs and explicitly labeled collision helpers.</summary>
    public Task<ModelBundleResult> ExportModelBundleAsync(ZbdDocument world, int rootIndex, string destination, string? preferredTexturePack = null, CancellationToken token = default)
        => ExportModelBundleAsync(world, rootIndex, destination, preferredTexturePack, token, new ModelBundleBudget(token));

    internal async Task<ModelBundleResult> ExportModelBundleAsync(ZbdDocument world, int rootIndex, string destination, string? preferredTexturePack,
        CancellationToken token, ModelBundleBudget budget)
    {
        token.ThrowIfCancellationRequested();
        var scene = world.Scene ?? throw new InvalidDataException("A GameZ scene is required.");
        if (rootIndex < 0 || rootIndex >= scene.Nodes.Count) throw new InvalidDataException("Missing root node index.");
        budget.Reserve(4096L + 6L * world.Path.Length, 0);
        List<ScenePlacement> placements = []; List<(GameNode Node, Matrix4x4 Local, Matrix4x4 World)> instances = []; HashSet<int> active = [];
        try { Visit(rootIndex, Matrix4x4.Identity, 0); }
        catch (InvalidDataException ex) when (budget.Work.Exhausted)
        { throw new InvalidDataException("Model bundle traversal exceeds its work budget. Select a smaller subtree.", ex); }
        int[] models = placements.Select(p => p.ModelIndex).Distinct().Order().ToArray();
        if (models.Length == 0) throw new InvalidDataException("This node hierarchy has no model geometry.");
        if (models.Length > 1024 || placements.Count > 10000) throw new InvalidDataException("Model bundle exceeds its bounded selection limit.");
        var owner = world.Assets.SingleOrDefault(a => a.Content is MechAssembly member && rootIndex >= member.RootNode && rootIndex < member.RootNode + member.NodeCount);
        var rootAsset = owner ?? world.Assets.Single(a => a.Kind == AssetKind.Node && a.Index == rootIndex);
        foreach (int index in models)
        {
            token.ThrowIfCancellationRequested();
            int count = placements.Count(p => p.ModelIndex == index);
            budget.Reserve(512L + 64L * count, count);
        }
        CheckObjExpansion(scene, new(placements, []), token);
        // The full selection is admitted before any per-occurrence JSON list is copied or output is created.
        JsonArray nodes = [];
        foreach (var (node, local, worldTransform) in instances)
        {
            token.ThrowIfCancellationRequested();
            nodes.Add(new JsonObject { ["nodeIndex"] = node.Index, ["name"] = node.Name, ["modelIndex"] = node.ModelIndex, ["parents"] = JsonData.Integers(node.Parents), ["children"] = JsonData.Integers(node.Children),
                ["collisionHelper"] = node.Name.Equals("bvol", StringComparison.OrdinalIgnoreCase), ["localTransform"] = Matrix(local), ["assembledTransform"] = Matrix(worldTransform) });
        }
        JsonArray modelRows = [];
        foreach (int index in models)
        {
            token.ThrowIfCancellationRequested();
            modelRows.Add(new JsonObject { ["modelIndex"] = index, ["memberIndex"] = owner?.Index, ["localModelIndex"] = owner?.Content is MechAssembly member ? index - member.FirstModel : null,
                ["obj"] = $"local/model_{index}.obj", ["modelType"] = scene.Models[index].Metadata.Int("model_type"), ["nodes"] = JsonData.Integers(placements.Where(p => p.ModelIndex == index).Select(p => p.NodeIndex)) });
        }
        var manifest = new JsonObject { ["version"] = 1, ["source"] = world.Path, ["sourceSha256"] = Convert.ToHexStringLower(SHA256.HashData(world.Bytes.Span)), ["rootNode"] = rootIndex,
            ["coordinates"] = "Game-local +Y up, -Z forward. OBJ UV origin is bottom-left; zStudio restores game V on import. Local OBJ files have no node transforms baked in.",
            ["memberIndex"] = owner?.Index, ["models"] = modelRows, ["nodes"] = nodes, ["notes"] = "All authored descendants and LOD variants. Collision helpers are labeled, not replacement targets. Names are labels; indices and source SHA-256 identify records." };
        using DirectoryLease directories = new();
        destination = ValidateExportDirectory(destination, directories);
        directories.Hold(destination, create: true);
        string folder = "models_" + rootIndex + "_" + SafeName(scene.Nodes[rootIndex].Name), target = Path.Combine(destination, folder); int suffix = 2;
        while (directories.Exists(target)) target = Path.Combine(destination, folder + "_" + suffix++);
        directories.CreateDirectory(target);
        var textureLookup = resolver.BeginTextureLookup(world.Path, preferredTexturePack);
        await ExportObj(world, rootAsset, target, "assembled", preferredTexturePack, 0, token, directories, new(placements, []), textureLookup).ConfigureAwait(false);
        foreach (int index in models)
        {
            var asset = owner ?? world.Assets.Single(a => a.Kind == AssetKind.Model && a.Index == index);
            await ExportObj(world, asset, target, $"local/model_{index}", preferredTexturePack, 0, token, directories, new([new(-1, index, asset.Name, Matrix4x4.Identity)], []), textureLookup).ConfigureAwait(false);
        }
        await WriteJson(target, "manifest.json", manifest, token, directories).ConfigureAwait(false);
        return new(target, Path.Combine(target, "manifest.json"), models.Length, placements.Count);
        void Visit(int index, Matrix4x4 parent, int depth)
        {
            token.ThrowIfCancellationRequested();
            budget.Work.Reserve(1);
            if (index < 0 || index >= scene.Nodes.Count || depth > 256 || !active.Add(index)) throw new InvalidDataException("Invalid or cyclic node hierarchy.");
            try
            {
                if (instances.Count >= 10000) throw new InvalidDataException("Model bundle exceeds 10,000 node instances.");
                var node = scene.Nodes[index];
                long references = (long)node.Parents.Length + node.Children.Length;
                budget.Reserve(2048L + 6L * node.Name.Length + 64L * references, references);
                var local = SceneBuilder.LocalTransform(node); var worldTransform = local * parent;
                instances.Add((node, local, worldTransform));
                if (node.ModelIndex is int model && model >= 0) { if (model >= scene.Models.Count) throw new InvalidDataException("Missing model dependency."); placements.Add(new(index, model, node.Name, worldTransform)); }
                foreach (int child in SceneBuilder.Children(node, budget.Work)) Visit(child, worldTransform, depth + 1);
            }
            finally { active.Remove(index); }
        }
        static JsonArray Matrix(Matrix4x4 m) => new(new[] { m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44 }.Select(v => JsonData.Number(v)).ToArray());
    }

    /// <summary>Full manifest admission, including escaped text, indented scalar arrays and repeated identities.</summary>
    internal sealed class ModelBundleBudget(CancellationToken token, long maximumBytes = ModelBundleBudget.MaximumBytes,
        long maximumReferences = ModelBundleBudget.MaximumReferences)
    {
        internal const long MaximumBytes = 32L * 1024 * 1024, MaximumReferences = 262_144;
        private readonly long byteLimit = maximumBytes is >= 0 and <= MaximumBytes ? maximumBytes : throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        private readonly long referenceLimit = maximumReferences is >= 0 and <= MaximumReferences ? maximumReferences : throw new ArgumentOutOfRangeException(nameof(maximumReferences));
        private long bytes, references;
        internal Worlds.LookupWorkBudget Work { get; } = new(SceneBuilder.MaximumTraversalWork, token);
        internal void Reserve(long byteCount, long referenceCount)
        {
            token.ThrowIfCancellationRequested();
            if (byteCount > byteLimit - bytes || referenceCount > referenceLimit - references)
                throw new InvalidDataException("Model bundle manifest exceeds its metadata budget (32 MiB or 262,144 repeated references). Select a smaller subtree.");
            bytes += byteCount; references += referenceCount;
        }
    }
}
