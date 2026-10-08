using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Desktop;

/// <summary>Non-WPF editor state prepared from the exact document that will be published.</summary>
internal sealed record PreparedDocument(ZbdDocument Document, ResourceEditSession? Resources)
{
    internal static PreparedDocument Create(ZbdDocument document, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ResourceEditSession? resources = null;
        if (document.Probe.Family is FormatFamily.Archive or FormatFamily.Zrd && !document.Diagnostics.Any(d => d.Severity == "Error"))
            resources = new(document, token);
        token.ThrowIfCancellationRequested();
        return new(document, resources);
    }
}
