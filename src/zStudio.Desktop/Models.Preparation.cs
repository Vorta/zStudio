using zStudio.Core;

namespace zStudio.Desktop;

/// <summary>Inspection state prepared off the dispatcher from the exact document that will be published.</summary>
internal sealed record PreparedDocument(AssetDocument Document, ResourceMembers? Resources)
{
    internal static PreparedDocument Create(AssetDocument document, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ResourceMembers? resources = null;
        if (document.Probe.Family is FormatFamily.Archive or FormatFamily.Zrd && !document.Diagnostics.Any(d => d.Severity == "Error"))
            resources = new(document, token);
        token.ThrowIfCancellationRequested();
        return new(document, resources);
    }
}
