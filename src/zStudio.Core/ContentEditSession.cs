using System.Security.Cryptography;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core;

public sealed record ContentSnapshot(IReadOnlyDictionary<string, ZbdDocument> Documents, object State);
public sealed record PreparedContentEdit(ContentSnapshot Before, ContentSnapshot After, IReadOnlyDictionary<string, ZbdDocument> Baselines, bool IdentityOrderChanged = false);
public sealed record ContentSaveResult(IReadOnlyList<string> SavedPaths, IReadOnlyList<string> Errors, IReadOnlyList<string> RemainingPaths);

/// <summary>Shared snapshot history and verified publication for script and texture editing.</summary>
public abstract class ContentEditSession
{
    private readonly List<ContentSnapshot> undo = [], redo = [];
    private readonly Dictionary<string, ZbdDocument> sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Path, ReadOnlyMemory<byte> Bytes, FileStamp Stamp, bool NeedsCreate)> saved = new(StringComparer.OrdinalIgnoreCase);
    private bool saving;
    public string SourcePath { get; }
    public ContentSnapshot Current { get; private set; }
    public IEnumerable<ZbdDocument> Documents => sources.Select(p => Current.Documents.TryGetValue(p.Key, out var doc) ? doc : p.Value);
    public bool IsDirty => saved.Values.Any(s => s.NeedsCreate) || Documents.Any(d => !d.Bytes.Span.SequenceEqual(saved[d.Path].Bytes.Span));
    public bool CanUndo => !saving && undo.Count > 0;
    public bool CanRedo => !saving && redo.Count > 0;
    public bool HasHistory => undo.Count > 0 || redo.Count > 0;
    public bool HasAcceptedEdits { get; private set; }
    public bool HasExternalChanges() => saved.Values.Any(s => s.NeedsCreate ? File.Exists(s.Path) || Directory.Exists(s.Path) : FileStamp.Read(s.Path) != s.Stamp);
    public string TargetPath(string source) => saved[source].Path;
    public IEnumerable<ZbdDocument> PublishedDocuments
    {
        get
        {
            foreach (var doc in Documents)
            {
                yield return doc;
                var target = saved[doc.Path];
                if (doc.Probe.Family != FormatFamily.TexturePack || doc.Path.Equals(target.Path, StringComparison.OrdinalIgnoreCase)) continue;
                // A Save As target is another view of this working snapshot. Share immutable
                // bytes/content while giving its rows the target file's identity.
                var alias = new ZbdDocument(target.Path, target.Stamp, doc.Probe, doc.Bytes);
                foreach (var item in doc.Metadata) alias.Metadata[item.Key] = item.Value?.DeepClone();
                alias.Diagnostics.AddRange(doc.Diagnostics);
                foreach (var asset in doc.Assets)
                { var row = alias.Add(asset.Kind, asset.Index, asset.Name, asset.Offset, asset.Length, (System.Text.Json.Nodes.JsonObject)asset.Metadata.DeepClone(), asset.Content); row.Summary = asset.Summary; }
                yield return alias;
            }
        }
    }
    public event Action? Changed;
    public event Action<IEnumerable<string>>? BeforeEdit;
    // Verification and destination checks remain outside this publication seam. The staged file stays held against writes
    // and renames from its check against the verified bytes until it is in place (VerifiedDocumentSave.Seal).
    internal Action<SealedFile, string, bool> PublishFile { get; set; } = static (staged, target, createNew) => staged.MoveTo(target, replace: !createNew);
    protected ContentEditSession(ZbdDocument source, object state)
    {
        if (source.Diagnostics.Any(d => d.Severity == "Error")) throw new InvalidDataException("An intact document is required for editing.");
        SourcePath = source.Path; sources[source.Path] = source; saved[source.Path] = (source.Path, source.Bytes, source.Stamp, false);
        Current = new(new Dictionary<string, ZbdDocument>(StringComparer.OrdinalIgnoreCase) { [source.Path] = source }, state);
    }
    public void Accept(PreparedContentEdit edit)
    {
        if (saving || !ReferenceEquals(edit.Before, Current)) throw new InvalidOperationException("The document changed while preparing the edit.");
        if (!edit.IdentityOrderChanged && edit.After.Documents.Count == Current.Documents.Count && edit.After.Documents.All(p => Current.Documents.TryGetValue(p.Key, out var previous) && p.Value.Bytes.Span.SequenceEqual(previous.Bytes.Span))) return;
        if (edit.After.Documents.Keys.Any(p => !sources.ContainsKey(p) && !edit.Baselines.ContainsKey(p))) throw new InvalidOperationException("Missing prepared baseline.");
        BeforeEdit?.Invoke(edit.After.Documents.Keys.Concat(saved.Values.Select(s => s.Path)));
        foreach (var p in edit.Baselines)
            if (!sources.ContainsKey(p.Key)) { sources.Add(p.Key, p.Value); saved.Add(p.Key, (p.Key, p.Value.Bytes, p.Value.Stamp, false)); }
        undo.Add(Current); Trim(undo); redo.Clear(); Current = edit.After; HasAcceptedEdits = true; Changed?.Invoke();
    }
    public void UndoRedo(bool forward)
    {
        if (saving) throw new InvalidOperationException("A save is in progress.");
        var from = forward ? redo : undo; var to = forward ? undo : redo;
        if (from.Count == 0) return;
        BeforeEdit?.Invoke(sources.Keys.Concat(saved.Values.Select(s => s.Path)));
        to.Add(Current); Trim(to); Current = from[^1]; from.RemoveAt(from.Count - 1); Changed?.Invoke();
    }
    private static void Trim(List<ContentSnapshot> history)
    {
        long size = history.Sum(s => s.Documents.Values.Sum(d => (long)d.Bytes.Length));
        while (history.Count > 1 && (history.Count > 128 || size > 256L * 1024 * 1024))
        { size -= history[0].Documents.Values.Sum(d => (long)d.Bytes.Length); history.RemoveAt(0); }
    }
    public async Task<ContentSaveResult> SaveAsync(IReadOnlyDictionary<string, string>? destinations = null, CancellationToken token = default)
    {
        if (saving) throw new InvalidOperationException("A save is in progress.");
        var documents = Documents.OrderBy(d => d.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (destinations != null && (destinations.Count != documents.Length || documents.Any(d => !destinations.ContainsKey(d.Path)))) throw new InvalidDataException("Save As must specify a new destination for every affected file.");
        var targets = documents.ToDictionary(d => d.Path, d => Path.GetFullPath(destinations == null ? saved[d.Path].Path : destinations[d.Path]), StringComparer.OrdinalIgnoreCase);
        if (targets.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Count) throw new InvalidDataException("Save destinations must be distinct.");
        if (targets.Any(p => sources.ContainsKey(p.Value) && !p.Key.Equals(p.Value, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("A save destination cannot use another record's source identity in this batch, even if that source file is missing.");
        BeforeEdit?.Invoke(sources.Keys.Concat(saved.Values.Select(s => s.Path)).Concat(targets.Values));
        saving = true; List<(ZbdDocument Doc, string Target, string Temp, bool CreateNew)> staged = []; List<string> completed = [], errors = [];
        try
        {
            foreach (var doc in documents)
            {
                token.ThrowIfCancellationRequested(); string target = targets[doc.Path];
                bool createNew = destinations != null || saved[doc.Path].NeedsCreate;
                VerifiedDocumentSave.ValidateDestination(target);
                if (!createNew) await VerifiedDocumentSave.CheckBaselineAsync(target, saved[doc.Path].Bytes, token);
                else if (File.Exists(target)) throw new IOException("Save As requires new files: " + target);
                if (!createNew && doc.Bytes.Span.SequenceEqual(saved[doc.Path].Bytes.Span)) continue;
                string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp"; staged.Add((doc, target, temp, createNew));
                await VerifiedDocumentSave.StageAsync(doc, temp, token, target);
            }
            // Once every output is verified, retain the complete Save As intent.
            // A partial publication must never send a later Save back to a source.
            foreach (var item in staged.Where(s => s.CreateNew))
            { saved[item.Doc.Path] = (item.Target, saved[item.Doc.Path].Bytes, new(0, DateTime.MinValue), true); HasAcceptedEdits = true; }
            foreach (var item in staged)
            {
                try
                {
                    token.ThrowIfCancellationRequested(); VerifiedDocumentSave.ValidateDestination(item.Target);
                    using SealedFile file = VerifiedDocumentSave.Seal(item.Temp, item.Doc.Bytes);
                    if (!item.CreateNew) await VerifiedDocumentSave.CheckBaselineAsync(item.Target, saved[item.Doc.Path].Bytes, token);
                    PublishFile(file, item.Target, item.CreateNew);
                    saved[item.Doc.Path] = (item.Target, item.Doc.Bytes, FileStamp.ReadHolding(item.Target, item.Doc.Bytes.Span), false); completed.Add(item.Target);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException) { errors.Add(item.Target + ": " + ex.Message); break; }
            }
            if (completed.Count > 0) HasAcceptedEdits = true;
            return new(completed, errors, staged.Where(item => !completed.Contains(item.Target, StringComparer.OrdinalIgnoreCase)).Select(item => item.Target).ToArray());
        }
        finally
        {
            foreach (var item in staged) { try { if (File.Exists(item.Temp)) File.Delete(item.Temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
            saving = false; Changed?.Invoke();
        }
    }
}

internal static class VerifiedDocumentSave
{
    internal static void ValidateDestination(string path)
    {
        if (PickupPlacementEditSession.IsProtectedPath(path)) throw new IOException("Save outside protected reference datasets.");
        if (Directory.Exists(path)) throw new IOException("The save destination is a directory. Choose a new file path.");
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Cannot save through a file link.");
        for (var d = new DirectoryInfo(Path.GetDirectoryName(path)!); d != null; d = d.Parent)
            if (d.Exists && d.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Cannot save through directory links.");
    }
    internal static async Task CheckBaselineAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        await using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length != bytes.Length || !CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(file, token), SHA256.HashData(bytes.Span)))
            throw new IOException("The file changed outside zStudio. Reload or choose a new Save As destination.");
    }
    /// <summary>
    /// Holds a staged save, checked against the verified <paramref name="bytes"/>, until it is in place: no other program can
    /// write or rename it after its verification (see <see cref="SealedFile"/>).
    /// </summary>
    internal static SealedFile Seal(string temp, ReadOnlyMemory<byte> bytes)
    {
        try { return SealedFile.Open(temp, Sources.JournalDigest.OfContent(bytes.Span)); }
        catch (IOException ex) { throw new IOException("The verified save was not put in place: " + ex.Message, ex); }
    }
    internal static async Task StageAsync(ZbdDocument document, string temp, CancellationToken token, string? destination = null)
    {
        FormatRegistry.ValidateDocumentSize(document.Bytes.Length);
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        await using (FileStream output = new(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
        { await output.WriteAsync(document.Bytes, token); await output.FlushAsync(token); output.Flush(true); }
        byte[] bytes = await Sources.SourceRead.AllAsync(temp, document.Bytes.Length, token);
        await Task.Run(() =>
        {
            if (!document.Bytes.Span.SequenceEqual(bytes)) throw new IOException("Saved file byte verification failed.");
            var check = FormatRegistry.Default.OpenBytes(destination ?? document.Path, bytes, token: token);
            if (check.Probe.Family != document.Probe.Family || check.Diagnostics.Any(d => d.Severity == "Error") || check.Assets.Count != document.Assets.Count)
                throw new InvalidDataException("Saved file failed shared-reader verification.");
        }, token);
    }
}
