using Recoil.Zbd.Automation;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using System.Text.Json.Nodes;

namespace Recoil.Zbd.Desktop;

public partial class MainWindow
{
    private async Task<StudioResult> InspectAssetAsync(DocumentModel doc, AssetRecord asset, CancellationToken token)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, doc.Lifetime.Token);
        long revision = doc.Revision;
        // Freeze the bounded view on the owning dispatcher; copying complete payloads
        // just to inspect a summary would reintroduce size-dependent allocations.
        JsonNode? edited = asset.Kind == AssetKind.Animation ? doc.AnimationEdits?.Package.Entries[asset.Index].ToPreviewJson(cancellation.Token) : null;
        var modelSnapshot = doc.ContentEdits != null ? doc.PreviewDocument : doc.ResourceEdits?.Current.Document ?? doc.ModelEdits?.Current.World;
        try
        {
            var original = doc.OriginalAsset(asset);
            var source = original == null ? null : await LoadAssetPropertiesAsync(doc.Document, original, cancellation.Token);
            ValidateContext();
            if (modelSnapshot != null)
            {
                var editedAsset = modelSnapshot.Assets.SingleOrDefault(a => a.Kind == asset.Kind && a.Index == asset.Index);
                if (editedAsset != null) edited = await LoadAssetPropertiesAsync(modelSnapshot, editedAsset, cancellation.Token);
            }
            ValidateContext();
            return Result(new JsonObject { ["document"] = doc.SessionId.ToString(), ["Revision"] = revision, ["source"] = source, ["edited"] = edited });
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && doc.IsDisposed)
        {
            throw new StudioCommandException("stale_document", "The inspected document was closed. Read zstudio_state before retrying.");
        }

        void ValidateContext()
        {
            token.ThrowIfCancellationRequested();
            if (doc.IsDisposed || !ViewModel.Documents.Contains(doc))
                throw new StudioCommandException("stale_document", "The inspected document is no longer open. Read zstudio_state before retrying.");
            cancellation.Token.ThrowIfCancellationRequested();
            if (doc.Revision != revision)
                throw new StudioCommandException("revision_conflict", "The inspected document changed. Read its current revision and retry inspection.");
        }
    }
}
