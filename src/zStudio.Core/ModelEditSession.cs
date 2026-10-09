using System.Buffers.Binary;
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
    private readonly List<WeakReference<ModelEditSnapshot>> prepared = [];
    private readonly ZbdDocument sourceWorld;
    private readonly long maximumRetainedBytes, maximumConstructionBytes;
    private bool saving, preparing;
    public ModelEditSnapshot Current { get; private set; }
    public bool CanUndo => !saving && undo.Count > 0;
    public bool CanRedo => !saving && redo.Count > 0;
    public bool HasModelImports => originalTextures.Count > 0;
    public bool IsDirty => Documents.Any(d => !saved.TryGetValue(d.Path, out var s) || s.PendingCopy || !d.Bytes.Span.SequenceEqual(s.Bytes));
    public event Action? Changed;
    public event Action? EditAccepted;
    public event Action<IEnumerable<string>>? BeforeEdit;
    public IEnumerable<ZbdDocument> Documents => originalTextures.Keys.Union(Current.Textures.Keys, StringComparer.OrdinalIgnoreCase).Select(p => Current.Textures.TryGetValue(p, out var d) ? d : originalTextures[p]).Append(Current.World);
    public ModelEditSession(ZbdDocument world) : this(world, EditRetentionBudget.MaximumRetainedBytes, EditRetentionBudget.MaximumConstructionBytes) { }
    internal ModelEditSession(ZbdDocument world, long maximumRetainedBytes, long maximumConstructionBytes)
    {
        EditRetentionBudget.Limit(maximumRetainedBytes, EditRetentionBudget.MaximumRetainedBytes);
        EditRetentionBudget.Limit(maximumConstructionBytes, EditRetentionBudget.MaximumConstructionBytes);
        this.maximumRetainedBytes = maximumRetainedBytes; this.maximumConstructionBytes = maximumConstructionBytes;
        sourceWorld = world; Current = new(world, new Dictionary<string, ZbdDocument>(StringComparer.OrdinalIgnoreCase));
        // The private save baseline is another buffer, including before the first import.
        Retention(Math.Min(maximumRetainedBytes, maximumConstructionBytes), CancellationToken.None).Reserve(288L + world.Bytes.Length, CancellationToken.None);
        saved[world.Path] = (world.Path, world.Bytes.ToArray(), false); observedStamps[world.Path] = world.Stamp;
    }
    internal Action<SealedFile, string, bool> PublishFile { get; set; } = static (file, target, replace) => file.MoveTo(target, replace);
    public bool HasExternalChanges() => observedStamps.Any(p => FileStamp.Read(p.Key) != p.Value);
    public Task<ModelEditSnapshot> PrepareAsync(ModelImportBatch batch, AssetResolver resolver, CancellationToken token = default) => PrepareAsync(batch, resolver, FormatRegistry.MaximumDocumentBytes, token);
    internal async Task<ModelEditSnapshot> PrepareAsync(ModelImportBatch batch, AssetResolver resolver, long maximumTextureBytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (saving || preparing) throw new InvalidOperationException("A model save or preparation is in progress.");
        preparing = true;
        RetainedDocumentBudget? construction = null, retained = null;
        try
        {
            EditBufferBudget budget = new(maximumTextureBytes);
            var baseline = Current;
            if (!Convert.ToHexString(SHA256.HashData(baseline.World.Bytes.Span)).Equals(batch.SourceSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The replacement manifest does not match this GameZ snapshot. Export a fresh bundle and verify model indices.");
            string? sourceDirectory = Path.GetDirectoryName(baseline.World.Path);
            var paths = resolver.TexturePacks(baseline.World.Path, token).Where(p => Path.GetDirectoryName(p)!.Equals(sourceDirectory, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (paths.Length == 0) throw new InvalidDataException("No mission texture packs found; replacement requires every local variant.");
            // Retained ownership and construction are separate bounded dimensions, as
            // in ContentEditSession. Unrelated history/prepared graphs stay in retained
            // admission; the active input, pending outputs and scratch belong here.
            // Their conservative combined ceiling is at most the sum of the two caps.
            _ = Retention(maximumRetainedBytes, token);
            construction = new(maximumConstructionBytes);
            CountInputs(construction, baseline, token);
            CountBatch(construction, batch, token);
            construction.Reserve(128L * paths.Length + paths.Sum(p => 2L * p.Length), token);
            Dictionary<string,ZbdDocument> textures = new(StringComparer.OrdinalIgnoreCase), baselines = new(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                token.ThrowIfCancellationRequested();
                var source = baseline.Textures.TryGetValue(path, out var edited) ? edited : await OpenTextureAsync(path, resolver, budget.Remaining, construction, token);
                budget.Document(source);
                CountDocument(construction, source, token);
                baselines[path] = source;
                long output = source.Bytes.Length + 56L + 2L * batch.Texture.Width * batch.Texture.Height;
                long decoded = TextureShape(source.Assets.Count + 1, path.Length);
                // Encode returns before Write/verification. Append's reader does not escape
                // its call; the outer OpenBytes starts only after Append has returned.
                long payload = output - source.Bytes.Length;
                long texturePeak = Math.Max(payload + 262_144, output + payload + decoded);
                CheckRoom(construction, texturePeak, token);
                textures[path] = await Task.Run(() => FormatRegistry.Default.OpenBytes(path,
                    TexturePackWriter.Append(source, batch.TextureName, batch.Texture, budget.Remaining, token), source.Stamp, token), token);
                budget.Document(textures[path]);
                CountDocument(construction, textures[path], token);
            }
            CheckWorldConstruction(construction, baseline.World, batch, token);
            var world = await Task.Run(() => FormatRegistry.Default.OpenBytes(baseline.World.Path,
                ModelReplacementWriter.Replace(baseline.World, batch.Models, batch.TextureName, token), baseline.World.Stamp, token), token);
            var result = new ModelEditSnapshot(world, textures, baselines);
            retained = Retention(maximumRetainedBytes, token); CountSnapshot(retained, result, token);
            ReserveNewBaselines(retained, baselines.Values, token);
            CountDocument(construction, world, token); ReserveNewBaselines(construction, baselines.Values, token);
            if (prepared.Count >= 512) throw EditRetentionBudget.Refusal();
            retained.Reserve(128, token); construction.Reserve(128, token);
            prepared.Add(new(result));
            return result;
        }
        catch (InvalidDataException) when (construction?.Exhausted == true || retained?.Exhausted == true) { throw EditRetentionBudget.Refusal(); }
        finally { preparing = false; }
    }
    public void Accept(ModelEditSnapshot snapshot)
    {
        if (saving || preparing) throw new InvalidOperationException("A model save or preparation is in progress.");
        var baselines = snapshot.Baselines ?? throw new InvalidOperationException("Missing prepared source snapshot.");
        // Model replacements own GameZ and texture packs. ZAR resources belong
        // to the coordinate/resource services; never accept a cross-family
        // snapshot that could alias their publication or save baselines.
        if (snapshot.World.Probe.Family != FormatFamily.GameZ || snapshot.Textures.Values.Any(d => d.Probe.Family != FormatFamily.TexturePack) ||
            baselines.Values.Any(d => d.Probe.Family != FormatFamily.TexturePack))
            throw new InvalidDataException("Model replacement snapshots require GameZ and texture-pack documents only.");
        var retained = Retention(maximumRetainedBytes, CancellationToken.None);
        CountSnapshot(retained, snapshot, CancellationToken.None);
        ReserveNewBaselines(retained, baselines.Values, CancellationToken.None);
        RetainedDocumentBudget construction = new(maximumConstructionBytes);
        CountSnapshot(construction, snapshot, CancellationToken.None);
        ReserveNewBaselines(construction, baselines.Values, CancellationToken.None);
        BeforeEdit?.Invoke(snapshot.Textures.Keys.Append(snapshot.World.Path).Concat(saved.Values.Select(s => s.Target)));
        foreach (var doc in baselines.Values)
            if (!saved.ContainsKey(doc.Path)) { saved[doc.Path] = (doc.Path, doc.Bytes.ToArray(), false); originalTextures[doc.Path] = doc; observedStamps[doc.Path] = doc.Stamp; }
        undo.Push(Current); redo.Clear(); Current = snapshot;
        if (undo.Count > 128)
        {
            var newest = undo.Take(128).Reverse().ToArray(); undo.Clear();
            foreach (var item in newest) undo.Push(item);
        }
        EditAccepted?.Invoke(); Changed?.Invoke();
    }
    public void Undo() { if (CanUndo) { redo.Push(Current); Current = undo.Pop(); Changed?.Invoke(); } }
    public void Redo() { if (CanRedo) { undo.Push(Current); Current = redo.Pop(); Changed?.Invoke(); } }
    public string TargetPath(string source) => saved[source].Target;

    /// <summary>Stage/reparse every file before publishing textures first and GameZ last. A partial commit remains explicitly dirty.</summary>
    public async Task<ModelSaveResult> SaveAsync(string? destinationDirectory = null, CancellationToken token = default)
    {
        if (saving || preparing) throw new InvalidOperationException("A model save or preparation is in progress.");
        token.ThrowIfCancellationRequested();
        var documents = Documents.ToDictionary(d => d.Path, StringComparer.OrdinalIgnoreCase);
        var targets = documents.Keys.ToDictionary(path => path, path => destinationDirectory == null ? saved[path].Target : Path.Combine(Path.GetFullPath(destinationDirectory), Path.GetFileName(path)), StringComparer.OrdinalIgnoreCase);
        var retained = Retention(maximumRetainedBytes, token);
        RetainedDocumentBudget construction = new(maximumConstructionBytes);
        long verification = 0;
        foreach (var doc in documents.Values)
        {
            var prior = saved[doc.Path];
            // All current outputs and baseline hash inputs remain active throughout
            // staging/publication. Older undo/source graphs are separately retained.
            CountDocument(construction, doc, token);
            construction.Bytes(prior.Bytes, token);
            if (destinationDirectory == null && !prior.PendingCopy && doc.Bytes.Span.SequenceEqual(prior.Bytes)) continue;
            // Old and new private save baselines overlap until each publication succeeds.
            // Reserve every replacement before staging, including partial Save As retries.
            retained.Reserve(96L + doc.Bytes.Length + 2L * targets[doc.Path].Length, token);
            construction.Reserve(32L + doc.Bytes.Length, token);
            verification = Math.Max(verification, 32L + doc.Bytes.Length + DecodedShape(doc, token));
            construction.Reserve(512L + 4L * targets[doc.Path].Length, token);
        }
        CheckRoom(construction, verification + 131_072, token);
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
                await VerifyAsync(file, doc, target, token);
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

    private static async Task VerifyAsync(SealedFile file, ZbdDocument doc, string target, CancellationToken token)
    {
        byte[] readback = await file.ReadAllAsync(doc.Bytes.Length, token);
        if (!doc.Bytes.Span.SequenceEqual(readback)) throw new IOException("Model save byte verification failed.");
        var parsed = await Task.Run(() => FormatRegistry.Default.OpenBytes(target, readback, token: token), token);
        if (parsed.Diagnostics.Any(d => d.Severity == "Error") || parsed.Assets.Count != doc.Assets.Count)
            throw new InvalidDataException("Model save shared-reader verification failed.");
    }

    private RetainedDocumentBudget Retention(long maximum, CancellationToken token)
    {
        RetainedDocumentBudget budget = new(maximum);
        CountDocument(budget, sourceWorld, token);
        foreach (var document in originalTextures.Values) CountDocument(budget, document, token);
        foreach (var baseline in saved.Values) { budget.Bytes(baseline.Bytes, token); budget.Text(baseline.Target, token); }
        CountSnapshot(budget, Current, token);
        foreach (var snapshot in undo) CountSnapshot(budget, snapshot, token);
        foreach (var snapshot in redo) CountSnapshot(budget, snapshot, token);
        // A caller may hold more than one prepared result. Weak receipts count live results
        // without making an abandoned/cancelled preparation into another permanent history.
        for (int i = prepared.Count - 1; i >= 0; i--)
            if (prepared[i].TryGetTarget(out var snapshot)) CountSnapshot(budget, snapshot, token);
            else prepared.RemoveAt(i);
        budget.Reserve(128L * (saved.Count + observedStamps.Count + prepared.Count), token);
        return budget;
    }
    private static void CountSnapshot(RetainedDocumentBudget budget, ModelEditSnapshot snapshot, CancellationToken token)
    {
        if (!budget.Object(snapshot, 128, token)) return;
        CountDocument(budget, snapshot.World, token);
        Count(snapshot.Textures);
        if (snapshot.Baselines is { } baselines) Count(baselines);
        void Count(IReadOnlyDictionary<string, ZbdDocument> documents)
        {
            if (!budget.Object(documents, 128L + 96L * documents.Count, token)) return;
            foreach (var (path, document) in documents) { budget.Text(path, token); CountDocument(budget, document, token); }
        }
    }
    private static void CountInputs(RetainedDocumentBudget budget, ModelEditSnapshot snapshot, CancellationToken token)
    {
        CountDocument(budget, snapshot.World, token);
        budget.Object(snapshot.Textures, 128L + 96L * snapshot.Textures.Count, token);
        foreach (var (path, document) in snapshot.Textures)
        { budget.Text(path, token); CountDocument(budget, document, token); }
    }
    private static void CountDocument(RetainedDocumentBudget budget, ZbdDocument document, CancellationToken token)
    {
        EditRetentionBudget.Document(budget, document, token);
        CountReaderStorage(budget, document, token);
    }
    private static void CountReaderStorage(RetainedDocumentBudget budget, ZbdDocument document, CancellationToken token)
    {
        budget.Object(document.Assets, 64L + 16L * document.Assets.Count, token);
        budget.Object(document.Diagnostics, 64L + 16L * document.Diagnostics.Count, token);
        if (document.GameZLayout is { } layout && budget.Object(layout, 96, token))
            budget.Object(layout.NodeDataOffsets, 64L + 16L * layout.NodeDataOffsets.Count, token);
        foreach (var asset in document.Assets)
            if (asset.Content is TextureInfo texture) budget.Object(texture, 96, token);
    }
    private void ReserveNewBaselines(RetainedDocumentBudget budget, IEnumerable<ZbdDocument> documents, CancellationToken token)
    {
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        foreach (var doc in documents)
            if (!saved.ContainsKey(doc.Path) && paths.Add(doc.Path)) budget.Reserve(256L + 2L * doc.Path.Length + doc.Bytes.Length, token);
    }
    private static void CheckRoom(RetainedDocumentBudget budget, long bytes, CancellationToken token)
    {
        var checkpoint = budget.Checkpoint(); budget.Reserve(bytes, token); budget.Restore(checkpoint);
    }
    private static long DecodedShape(ZbdDocument document, CancellationToken token)
    {
        RetainedDocumentBudget shape = new(EditRetentionBudget.MaximumConstructionBytes);
        shape.Document(document, token); CountReaderStorage(shape, document, token);
        return shape.UsedBytes;
    }
    // TextureReader retains seven scalar/string metadata fields, a TextureInfo, name,
    // summary and asset per row; no decoded pixels. 8 KiB covers their object/key storage.
    private static long TextureShape(long count, int pathLength) => 8192L * (count + 1) + 2L * pathLength;
    private static async Task<ZbdDocument> OpenTextureAsync(string path, AssetResolver resolver, long remaining,
        RetainedDocumentBudget construction, CancellationToken token)
    {
        if (resolver.WorkspaceSnapshot(path, Guid.Empty) is { } snapshot)
        {
            if (snapshot.Bytes.Length > remaining) throw new InvalidDataException("The texture pack exceeds the remaining edit buffer budget.");
            CountDocument(construction, snapshot, token); return snapshot;
        }
        // Freeze the small header and actual handle length through the shared reader open.
        // A pre-read FileStamp alone would permit a larger replacement before allocation.
        using DirectoryLease directories = new();
        await using var file = directories.OpenFile(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        byte[] header = new byte[24]; await file.ReadExactlyAsync(header, token);
        var probe = FormatRegistry.Probe(header, [], file.Length);
        if (probe.Family != FormatFamily.TexturePack || probe.Recognition != Recognition.Supported)
            throw new InvalidDataException("Model replacement requires intact local texture packs.");
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
        FormatRegistry.CheckEntries("Texture record", count, 4096);
        CheckRoom(construction, 32 + file.Length + TextureShape(count, path.Length) + 65_536, token);
        return await resolver.OpenCachedAsync(path, Math.Min(remaining, FormatRegistry.MaximumDocumentBytes), token);
    }
    private static void CountBatch(RetainedDocumentBudget budget, ModelImportBatch batch, CancellationToken token)
    {
        budget.Bytes(batch.Texture.Rgba, token); budget.Text(batch.TextureName, token); budget.Text(batch.SourceSha256, token);
        budget.Object(batch.Models, 128L + 64L * batch.Models.Count, token);
        foreach (var mesh in batch.Models.Values)
        {
            mesh.Validate(); token.ThrowIfCancellationRequested();
            budget.Object(mesh.Positions, 32L + 12L * mesh.Positions.Length, token);
            budget.Object(mesh.Normals, 32L + 12L * mesh.Normals.Length, token);
            budget.Object(mesh.Uvs, 32L + 8L * mesh.Uvs.Length, token);
            budget.Object(mesh.Colors, 32L + 12L * mesh.Colors.Length, token);
            budget.Object(mesh.Triangles, 32L + 4L * mesh.Triangles.Length, token);
        }
    }
    private static void CheckWorldConstruction(RetainedDocumentBudget budget, ZbdDocument world, ModelImportBatch batch, CancellationToken token)
    {
        var layout = world.GameZLayout ?? throw new InvalidDataException("Model replacement requires GameZ v15 or v27.");
        var dialect = GameZLayouts.For(world.Probe.Version);
        long meshBytes = 0, largestMesh = 0, addedDecoded = 32_768;
        foreach (var mesh in batch.Models.Values)
        {
            long encoded = 24L * mesh.Positions.Length + mesh.Triangles.Length / 3L * (dialect.HasVertexColors ? 120 : 76);
            meshBytes += encoded; largestMesh = Math.Max(largestMesh, encoded);
            // A new polygon has <=9 metadata fields, four three-corner arrays, and
            // list/Polygon wrappers; 8 KiB includes field keys/values and list capacity.
            addedDecoded += 24L * mesh.Positions.Length + 8192L * (mesh.Triangles.Length / 3);
        }
        long replacedBytes = 0; int matched = 0;
        foreach (var asset in world.Assets)
            if (asset.Kind == AssetKind.Model && batch.Models.ContainsKey(asset.Index)) { replacedBytes += asset.Length; matched++; }
        if (matched != batch.Models.Count) throw new InvalidDataException("Missing model index.");
        long output = world.Bytes.Length + dialect.TextureSize + meshBytes - replacedBytes;
        FormatRegistry.ValidateDocumentSize(output);
        long decoded = DecodedShape(world, token) + addedDecoded;
        long materials = layout.ModelOffset - layout.MaterialOffset;
        long table = 12L + layout.ModelCapacity * (dialect.ModelSize + 4L);
        long dynamics = layout.NodeOffset - layout.ModelOffset - table + meshBytes - replacedBytes;
        long sections = layout.MaterialOffset + materials + table + world.Bytes.Length - layout.NodeOffset + dialect.TextureSize;
        if (dynamics < 0 || sections < 0) throw new InvalidDataException("Invalid model section ranges.");
        long readerAndWriterIndexes = 128L * (world.Assets.Count + (long)world.Scene!.Nodes.Count + world.Scene.Models.Count) +
            64L * layout.MaterialCapacity + 32L * world.Scene.Nodes.Sum(n => (long)n.Parents.Length);
        // Use phase peaks, not the sum of allocations whose lifetimes do not overlap.
        // Growing streams hold old + new capacity (<=3*length); stable capacity is <=2*length.
        // EncodeMesh ends before its returned array is copied into a growing dynamics stream.
        long poolCheck = materials + materials;
        long meshEncode = materials + table + 2 * dynamics + 3 * largestMesh;
        long dynamicsGrow = materials + table + 3 * dynamics + largestMesh;
        long outputGrow = sections + 2 * dynamics + 3 * output;
        // Replace's verification retains its own streams, final raw bytes and ONE decoded
        // graph. That graph and those streams cannot escape Replace; the outer OpenBytes
        // starts afterward, with only returned raw bytes + its one decoded graph.
        long verifyInsideWriter = sections + 2 * dynamics + 2 * output + output + decoded;
        long parseReturnedBytes = output + decoded;
        long peak = Math.Max(Math.Max(poolCheck, meshEncode), Math.Max(dynamicsGrow, outputGrow));
        peak = Math.Max(peak, Math.Max(verifyInsideWriter, parseReturnedBytes));
        CheckRoom(budget, peak + readerAndWriterIndexes + 131_072, token);
    }
    private static void ValidateDestination(string path)
    {
        if (PickupPlacementEditSession.IsProtectedPath(path)) throw new IOException("Save model edits outside zbd_1998 and zbd_1999.");
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Cannot save through a file link.");
        for (var d = new DirectoryInfo(Path.GetDirectoryName(path)!); d != null; d = d.Parent)
            if (d.Exists && d.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Cannot save through directory links.");
    }
}
