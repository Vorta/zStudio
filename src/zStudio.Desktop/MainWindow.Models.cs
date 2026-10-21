using System.IO;
using zStudio.Automation;
using zStudio.Core;
using zStudio.Core.Export;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private void RegisterModelCommands(StudioCommands r)
    {
        RegisterJob(r, "model_bundle_export", "Export all authored models under a node or an animation root as assembled and local OBJ/MTL/PNG files with stable indices and transforms. Source worlds fingerprint accepted mission/dependency content; compiled worlds use the archive SHA-256.",
            [.. AssetParameters, P("destination", "string", "Full path of an export folder outside the source tree.", true), new("rootNode", "integer", "Explicit GameZ root node, required for ambiguous animation roots.", Minimum: 0, Maximum: int.MaxValue), P("texturePack", "string", "Optional preferred texture pack.")], true,
            async (a, token) => Result(await ExportModelBundleAsync(TargetDocument(a), TargetAsset(TargetDocument(a), a), FullPath(a, "destination"), a.ContainsKey("rootNode") ? Int(a,"rootNode") : null, Text(a,"texturePack") is { Length: > 0 } pack ? pack : null, token)));
    }
    private async Task<ModelBundleResult> ExportModelBundleAsync(DocumentModel doc, AssetRecord asset, string destination, int? explicitRoot, string? pack, CancellationToken token)
    {
        if (operation != null) throw new StudioCommandException("busy", "An export or validation is already running.");
        var resolver = ViewModel.Resolver ?? throw new StudioCommandException("no_workspace", "Open a root first.");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, doc.Lifetime.Token); operation = cancellation;
        try
        {
            var world = doc.Document; int root;
            if (asset.Kind == AssetKind.Animation)
            {
                var context = await doc.GetAnimationContextAsync(resolver, cancellation.Token, difficulty: ViewModel.Difficulty); world = (AssetDocument)context.World;
                string name = doc.Document.Animations!.Entries[asset.Index].RootName;
                var roots = world.Scene!.Nodes.Where(n => n.Name == name).ToArray();
                root = explicitRoot ?? (roots.Length == 1 ? roots[0].Index : throw new InvalidDataException("Animation root is missing or ambiguous. Supply an explicit root node index."));
            }
            else root = explicitRoot ?? (asset.Kind == AssetKind.Node ? asset.Index : SceneLods.PreviewRoot(world.Scene ?? throw new InvalidDataException("Select an animation or GameZ model/node."), asset) ?? throw new InvalidDataException("Select a node-bound model."));
            var result = await Task.Run(() => new ExportService(resolver).ExportModelBundleAsync(world, root, destination, pack, cancellation.Token), cancellation.Token);
            ViewModel.Status = $"Exported {result.Models} models to {result.Directory}"; return result;
        }
        finally { operation = null; }
    }
}
