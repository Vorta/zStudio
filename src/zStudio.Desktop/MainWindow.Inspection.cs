using zStudio.Automation;
using zStudio.Core;
using System.Text.Json.Nodes;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private async Task<StudioResult> InspectAssetAsync(DocumentModel doc, AssetRecord asset, CancellationToken token)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, doc.Lifetime.Token);
        long revision = doc.Revision;
        try
        {
            var original = doc.OriginalAsset(asset);
            var source = original == null ? null : await LoadAssetPropertiesAsync(doc.Document, original, cancellation.Token);
            ValidateContext();
            return Result(new JsonObject { ["document"] = doc.SessionId.ToString(), ["Revision"] = revision, ["source"] = source });
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
