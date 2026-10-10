using Recoil.Zbd.Automation;

namespace Recoil.Zbd.Desktop;

/// <summary>Charge escaped document metadata before constructing any state rows or JSON.</summary>
internal static class StateDocumentPage
{
    internal const int MaximumRows = 64, MaximumBytes = 1024 * 1024;
    internal sealed record Selection(DocumentModel[] Documents, int Total, int Offset, int? NextOffset);

    internal static Selection Select(IList<DocumentModel> documents, int offset, int limit)
    {
        if (offset < 0 || limit is < 1 or > MaximumRows)
            throw new StudioCommandException("invalid_argument", "Use offset >= 0 and limit 1–64.");
        List<DocumentModel> rows = []; long bytes = 0;
        for (int i = offset; i < documents.Count && rows.Count < limit; i++)
        {
            long cost = MaximumRowBytes(documents[i]);
            if (cost > MaximumBytes)
                throw new StudioCommandException("too_large", "A document's state metadata exceeds the supported page size.");
            if (bytes + cost > MaximumBytes) break;
            rows.Add(documents[i]); bytes += cost;
        }
        int? next = (long)offset + rows.Count < documents.Count ? offset + rows.Count : null;
        return new([.. rows], documents.Count, offset, next);
    }

    internal static long MaximumRowBytes(DocumentModel document)
    {
        // All remaining fields are fixed names, enums, numbers, booleans and count-only content state.
        long bytes = 2048 + Text(document.Path) + Text(document.LastSavedCopy) + Text(document.SelectedAsset?.Record.Id.File)
            + Text(document.Document.Probe.Description);
        if (document.SourceWorld is not { } world) return bytes;
        bytes += 4096 + Text(world.Mission) + Text(world.Root) + Text(world.ScriptPath) + Text(world.DefinitionsPath);
        // Workspace undo/redo are 128 characters each; history and dirty-file rows are omitted.
        bytes += 2 * 128 * 6;
        foreach (string note in document.SourceEditNotes.Take(16)) bytes += 8 + 6L * Math.Min(note.Length, 512);
        if (document.SourceBuild is { } build)
            foreach (var output in build.Outputs)
            {
                bytes += 128 + Text(output.Path) + Text(output.Status) + 6L * Math.Min(output.Error?.Length ?? 0, 512);
                if (bytes > MaximumBytes) break;
            }
        return bytes;
    }

    private static long Text(string? text) => 8 + 6L * (text?.Length ?? 0);
}
