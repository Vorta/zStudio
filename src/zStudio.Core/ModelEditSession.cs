using System.Security.Cryptography;
using Recoil.Zbd.Core.Export;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core;

public sealed record ModelEditSnapshot(ZbdDocument World, IReadOnlyDictionary<string, ZbdDocument> Textures, IReadOnlyDictionary<string, ZbdDocument>? Baselines = null);
public sealed record ModelSaveResult(IReadOnlyList<string> SavedPaths, IReadOnlyList<string> Errors);

/// <summary>Immutable accepted snapshots and one undo step per validated import batch.</summary>
public sealed class ModelEditSession
{
    private readonly Stack<ModelEditSnapshot> undo = [], redo = [];
    private readonly Dictionary<string, (string Target, byte[] Bytes, bool PendingCopy)> saved = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ZbdDocument> originalTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileStamp> observedStamps = new(StringComparer.OrdinalIgnoreCase);
    private bool saving;
    public ModelEditSnapshot Current { get; private set; }
    public bool CanUndo => !saving && undo.Count > 0;
    public bool CanRedo => !saving && redo.Count > 0;
    public bool HasModelImports => originalTextures.Count > 0;
    public bool IsDirty => Documents.Any(d => !saved.TryGetValue(d.Path, out var s) || s.PendingCopy || !d.Bytes.Span.SequenceEqual(s.Bytes));
    public event Action? Changed;
    public event Action? EditAccepted;
    public event Action<IEnumerable<string>>? BeforeEdit;
    public IEnumerable<ZbdDocument> Documents => originalTextures.Keys.Union(Current.Textures.Keys, StringComparer.OrdinalIgnoreCase).Select(p => Current.Textures.TryGetValue(p, out var d) ? d : originalTextures[p]).Append(Current.World);
    public ModelEditSession(ZbdDocument world) { Current = new(world, new Dictionary<string,ZbdDocument>(StringComparer.OrdinalIgnoreCase)); saved[world.Path] = (world.Path, world.Bytes.ToArray(), false); observedStamps[world.Path] = world.Stamp; }
    internal Action<SealedFile, string, bool> PublishFile { get; set; } = static (file, target, replace) => file.MoveTo(target, replace);
    public bool HasExternalChanges() => observedStamps.Any(p => FileStamp.Read(p.Key) != p.Value);
    public Task<ModelEditSnapshot> PrepareAsync(ModelImportBatch batch, AssetResolver resolver, CancellationToken token = default) => PrepareAsync(batch, resolver, FormatRegistry.MaximumDocumentBytes, token);
    internal async Task<ModelEditSnapshot> PrepareAsync(ModelImportBatch batch, AssetResolver resolver, long maximumTextureBytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        EditBufferBudget budget = new(maximumTextureBytes);
        var baseline = Current;
        if (!Convert.ToHexString(SHA256.HashData(baseline.World.Bytes.Span)).Equals(batch.SourceSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The replacement manifest does not match this GameZ snapshot. Export a fresh bundle and verify model indices.");
        string? sourceDirectory = Path.GetDirectoryName(baseline.World.Path);
        var paths = resolver.TexturePacks(baseline.World.Path, token).Where(p => Path.GetDirectoryName(p)!.Equals(sourceDirectory, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (paths.Length == 0) throw new InvalidDataException("No mission texture packs found; replacement requires every local variant.");
        Dictionary<string,ZbdDocument> textures = new(StringComparer.OrdinalIgnoreCase), baselines = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            token.ThrowIfCancellationRequested();
            var source = baseline.Textures.TryGetValue(path, out var edited) ? edited : await resolver.OpenCachedAsync(path, budget.Remaining, token);
            budget.Document(source);
            baselines[path] = source;
            textures[path] = await Task.Run(() => FormatRegistry.Default.OpenBytes(path,
                TexturePackWriter.Append(source, batch.TextureName, batch.Texture, budget.Remaining, token), source.Stamp, token), token);
            budget.Document(textures[path]);
        }
        var world = await Task.Run(() => FormatRegistry.Default.OpenBytes(baseline.World.Path,
            ModelReplacementWriter.Replace(baseline.World, batch.Models, batch.TextureName, token), baseline.World.Stamp, token), token);
        return new(world, textures, baselines);
    }
    public void Accept(ModelEditSnapshot snapshot)
    {
        if (saving) throw new InvalidOperationException("A model save is in progress.");
        var baselines = snapshot.Baselines ?? throw new InvalidOperationException("Missing prepared source snapshot.");
        // Model replacements own GameZ and texture packs. ZAR resources belong
        // to the coordinate/resource services; never accept a cross-family
        // snapshot that could alias their publication or save baselines.
        if (snapshot.World.Probe.Family != FormatFamily.GameZ || snapshot.Textures.Values.Any(d => d.Probe.Family != FormatFamily.TexturePack) ||
            baselines.Values.Any(d => d.Probe.Family != FormatFamily.TexturePack))
            throw new InvalidDataException("Model replacement snapshots require GameZ and texture-pack documents only.");
        BeforeEdit?.Invoke(snapshot.Textures.Keys.Append(snapshot.World.Path).Concat(saved.Values.Select(s => s.Target)));
        foreach (var doc in baselines.Values)
            if (!saved.ContainsKey(doc.Path)) { saved[doc.Path] = (doc.Path, doc.Bytes.ToArray(), false); originalTextures[doc.Path] = doc; observedStamps[doc.Path] = doc.Stamp; }
        undo.Push(Current); redo.Clear(); Current = snapshot; EditAccepted?.Invoke(); Changed?.Invoke();
    }
    public void Undo() { if (CanUndo) { redo.Push(Current); Current = undo.Pop(); Changed?.Invoke(); } }
    public void Redo() { if (CanRedo) { undo.Push(Current); Current = redo.Pop(); Changed?.Invoke(); } }
    public string TargetPath(string source) => saved[source].Target;

    /// <summary>Stage/reparse every file before publishing textures first and GameZ last. A partial commit remains explicitly dirty.</summary>
    public async Task<ModelSaveResult> SaveAsync(string? destinationDirectory = null, CancellationToken token = default)
    {
        if (saving) throw new InvalidOperationException("A model save is in progress.");
        token.ThrowIfCancellationRequested();
        var documents = Documents.ToDictionary(d => d.Path, StringComparer.OrdinalIgnoreCase);
        var targets = documents.Keys.ToDictionary(path => path, path => destinationDirectory == null ? saved[path].Target : Path.Combine(Path.GetFullPath(destinationDirectory), Path.GetFileName(path)), StringComparer.OrdinalIgnoreCase);
        BeforeEdit?.Invoke(documents.Keys.Concat(saved.Values.Select(s => s.Target)).Concat(targets.Values));
        saving = true; List<(ZbdDocument Doc,string Target,SealedFile File,bool Replace)> staged = []; List<string> completed = [], errors = [];
        using DirectoryLease directories = new();
        try
        {
            foreach (var doc in documents.Values.OrderBy(d => d.Probe.Family == FormatFamily.GameZ ? 1 : 0).ThenBy(d => d.Path, StringComparer.OrdinalIgnoreCase))
            {
                token.ThrowIfCancellationRequested();
                var prior = saved[doc.Path]; string target = targets[doc.Path];
                ValidateDestination(target);
                ValidateDestination(directories.CapturedPath(target));
                directories.Parent(target, create: true);
                bool replace = destinationDirectory == null && !prior.PendingCopy;
                if (replace) await CheckExternalAsync(prior.Target, prior.Bytes, token, directories);
                else if (File.Exists(target)) throw new IOException($"Save As requires new files: {target}");
                if (replace && doc.Bytes.Span.SequenceEqual(prior.Bytes)) continue;
                string temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                var file = await SealedFile.CreateAsync(temp, doc.Bytes, directories, token);
                staged.Add((doc,target,file,replace));
                byte[] readback = await file.ReadAllAsync(doc.Bytes.Length, token);
                if (!doc.Bytes.Span.SequenceEqual(readback)) throw new IOException("Model save byte verification failed.");
                var parsed = await Task.Run(() => FormatRegistry.Default.OpenBytes(target, readback, token:token), token);
                if (parsed.Diagnostics.Any(d => d.Severity == "Error") || parsed.Assets.Count != doc.Assets.Count) throw new InvalidDataException("Model save shared-reader verification failed.");
            }
            // A partially published Save As still owns every requested copy path.
            // Retrying must create missing copies, never return to a working source.
            foreach (var item in staged.Where(s => !s.Replace))
            {
                var prior = saved[item.Doc.Path];
                observedStamps.Remove(prior.Target);
                saved[item.Doc.Path] = (item.Target, prior.Bytes, true);
            }
            foreach (var item in staged)
            {
                try
                {
                    token.ThrowIfCancellationRequested(); ValidateDestination(item.Target);
                    if (item.Replace) { var prior = saved[item.Doc.Path]; await CheckExternalAsync(prior.Target, prior.Bytes, token, directories); }
                    PublishFile(item.File, item.Target, item.Replace);
                    item.File.Dispose();
                    string previousTarget = saved[item.Doc.Path].Target;
                    saved[item.Doc.Path] = (item.Target,item.Doc.Bytes.ToArray(), false);
                    observedStamps.Remove(previousTarget);
                    observedStamps[item.Target] = FileStamp.ReadHolding(item.Target, item.Doc.Bytes.Span, directories); completed.Add(item.Target);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException) { errors.Add(item.Target + ": " + ex.Message); break; }
            }
            return new(completed,errors);
        }
        finally
        {
            foreach (var item in staged) item.File.Dispose();
            saving = false; Changed?.Invoke();
        }
    }
    private static Task CheckExternalAsync(string target, byte[] baseline, CancellationToken token, DirectoryLease directories) =>
        VerifiedDocumentSave.CheckBaselineAsync(target, baseline, token, directories);
    private static void ValidateDestination(string path)
    {
        if (PickupPlacementEditSession.IsProtectedPath(path)) throw new IOException("Save model edits outside zbd_1998 and zbd_1999.");
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Cannot save through a file link.");
        for (var d = new DirectoryInfo(Path.GetDirectoryName(path)!); d != null; d = d.Parent)
            if (d.Exists && d.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Cannot save through directory links.");
    }
}
