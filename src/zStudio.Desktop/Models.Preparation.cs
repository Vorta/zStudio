using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Desktop;

/// <summary>Non-WPF editor state prepared from the exact document that will be published.</summary>
internal sealed record PreparedDocument(ZbdDocument Document, ResourceEditSession? Resources, ModelEditSession? Models = null)
{
    internal static PreparedDocument Create(ZbdDocument document, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ResourceEditSession? resources = null;
        if (document.Probe.Family is FormatFamily.Archive or FormatFamily.Zrd && !document.Diagnostics.Any(d => d.Severity == "Error"))
            resources = new(document, token);
        token.ThrowIfCancellationRequested();
        // Measuring a whole decoded world for edit admission takes up to a second; never on the dispatcher.
        ModelEditSession? models = EditsModels(document) ? new(document, token) : null;
        token.ThrowIfCancellationRequested();
        return new(document, resources, models);
    }
    /// <summary>Compiled GameZ files whose models can be replaced (source worlds change through their build instead).</summary>
    internal static bool EditsModels(ZbdDocument document) =>
        document.GameZLayout != null && document.Probe.Version is 15 or 27 && !document.Diagnostics.Any(d => d.Severity == "Error");
}
