using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Recoil.Zbd.Core.Export;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core;

public sealed record ModelEditSnapshot(ZbdDocument World, IReadOnlyDictionary<string, ZbdDocument> Textures, IReadOnlyDictionary<string, ZbdDocument>? Baselines = null);
public sealed record ModelSaveResult(IReadOnlyList<string> SavedPaths, IReadOnlyList<string> Errors);

/// <summary>Immutable accepted snapshots and one undo step per validated import batch.</summary>
public sealed class ModelEditSession
{
    // Oldest first. Accepting an edit retires the oldest undo steps that no longer fit and drops redo.
    private readonly List<ModelEditSnapshot> undo = [], redo = [];
    // Save baselines share the immutable document arrays; they are never copied.
    private readonly Dictionary<string, (string Target, ReadOnlyMemory<byte> Bytes, bool PendingCopy)> saved = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ZbdDocument> originalTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileStamp> observedStamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<WeakReference<ModelEditSnapshot>> prepared = [];
    // Each immutable document's decoded content is measured once, where it is opened or prepared (off
    // the dispatcher). Admission then sums these by document/array identity instead of re-walking graphs.
    private readonly ConditionalWeakTable<ZbdDocument, StrongBox<long>> decodedCosts = new();
    private readonly ZbdDocument sourceWorld;
    // The opened file's snapshot holds only the source world, which stays retained anyway.
    private readonly ModelEditSnapshot opened;
    private readonly long maximumRetainedBytes, maximumConstructionBytes;
    private bool saving, preparing;
    public ModelEditSnapshot Current { get; private set; }
    public bool CanUndo => !saving && undo.Count > 0;
    public bool CanRedo => !saving && redo.Count > 0;
    public bool HasModelImports => originalTextures.Count > 0;
    /// <summary>Why this world's models cannot be replaced (it remains open for viewing), or null.</summary>
    public string? UnavailableReason { get; }
    public bool IsDirty => Documents.Any(d => !saved.TryGetValue(d.Path, out var s) || s.PendingCopy || !d.Bytes.Span.SequenceEqual(s.Bytes.Span));
    public event Action? Changed;
    /// <summary>Raised after an accepted edit with the number of oldest undo steps it retired, so a history shared
    /// with other editors of the document can drop the same steps.</summary>
    public event Action<int>? EditAccepted;
    public event Action<IEnumerable<string>>? BeforeEdit;
    public IEnumerable<ZbdDocument> Documents => originalTextures.Keys.Union(Current.Textures.Keys, StringComparer.OrdinalIgnoreCase).Select(p => Current.Textures.TryGetValue(p, out var d) ? d : originalTextures[p]).Append(Current.World);
    /// <summary>Measures the source world's decoded content; construct it off the dispatcher.</summary>
    public ModelEditSession(ZbdDocument world, CancellationToken token = default) : this(world, EditRetentionBudget.MaximumRetainedBytes, EditRetentionBudget.MaximumConstructionBytes, token) { }
    internal ModelEditSession(ZbdDocument world, long maximumRetainedBytes, long maximumConstructionBytes, CancellationToken token = default)
    {
        EditRetentionBudget.Limit(maximumRetainedBytes, EditRetentionBudget.MaximumRetainedBytes);
        EditRetentionBudget.Limit(maximumConstructionBytes, EditRetentionBudget.MaximumConstructionBytes);
        this.maximumRetainedBytes = maximumRetainedBytes; this.maximumConstructionBytes = maximumConstructionBytes;
        sourceWorld = world; Current = opened = new(world, new Dictionary<string, ZbdDocument>(StringComparer.OrdinalIgnoreCase));
        saved[world.Path] = (world.Path, world.Bytes, false); observedStamps[world.Path] = world.Stamp;
        // The source is retained beside every later snapshot and is also the first import's input. A world too
        // large to edit is not too large to view: it opens, and only model replacement is refused.
        long maximum = Math.Min(maximumRetainedBytes, maximumConstructionBytes);
        try { _ = Mandatory(null, [], maximum, token); }
        catch (InvalidDataException)
        {
            UnavailableReason = $"This world's decoded content exceeds the {maximum >> 20:N0} MiB model-edit allowance, so its models cannot be replaced. " +
                "It remains open for viewing and export; replace models in a smaller GameZ world.";
        }
    }
    internal Action<SealedFile, string, bool> PublishFile { get; set; } = static (file, target, replace) => file.MoveTo(target, replace);
    public bool HasExternalChanges() => observedStamps.Any(p => FileStamp.Read(p.Key) != p.Value);
    public Task<ModelEditSnapshot> PrepareAsync(ModelImportBatch batch, AssetResolver resolver, CancellationToken token = default) => PrepareAsync(batch, resolver, FormatRegistry.MaximumDocumentBytes, token);
    internal async Task<ModelEditSnapshot> PrepareAsync(ModelImportBatch batch, AssetResolver resolver, long maximumTextureBytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (UnavailableReason is { } reason) throw new InvalidDataException(reason);
        if (saving || preparing) throw new InvalidOperationException("A model save or preparation is in progress.");
        preparing = true;
        try
        {
            var baseline = Current;
            HashSet<string> savedPaths = new(saved.Keys, StringComparer.OrdinalIgnoreCase);
            // Current and live prepared results must already fit before building another candidate.
            _ = Mandatory(null, [], maximumRetainedBytes, token);
            // Hashing, reading, encoding, parsing and measuring never run on the caller's (dispatcher) context.
            var result = await Task.Run(() => BuildAsync(batch, resolver, maximumTextureBytes, baseline, savedPaths, token), token);
            // Undo/redo history is retirable when the edit is accepted; the source, saved baselines,
            // Current, live prepared results and this candidate are not. Cached sizes keep this cheap.
            var retained = Mandatory(result, result.Baselines!.Values, maximumRetainedBytes, token);
            if (prepared.Count >= 512) throw EditRetentionBudget.Refusal();
            try { retained.Reserve(128, token); }
            catch (InvalidDataException) when (retained.Exhausted) { throw EditRetentionBudget.Refusal(); }
            prepared.Add(new(result));
            return result;
        }
        finally { preparing = false; }
    }
    // Runs on a worker. Reads only the captured baseline/save paths, readonly limits and the thread-safe size cache.
    private async Task<ModelEditSnapshot> BuildAsync(ModelImportBatch batch, AssetResolver resolver, long maximumTextureBytes,
        ModelEditSnapshot baseline, HashSet<string> savedPaths, CancellationToken token)
    {
        // Retained ownership and construction are separate bounded dimensions, as in ContentEditSession.
        // History/prepared graphs stay in retained admission; the active input, pending outputs and
        // scratch belong here. Their conservative combined ceiling is at most the sum of the two caps.
        RetainedDocumentBudget construction = new(maximumConstructionBytes);
        try
        {
            EditBufferBudget budget = new(maximumTextureBytes);
            if (!Convert.ToHexString(SHA256.HashData(baseline.World.Bytes.Span)).Equals(batch.SourceSha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The replacement manifest does not match this GameZ snapshot. Export a fresh bundle and verify model indices.");
            string? sourceDirectory = Path.GetDirectoryName(baseline.World.Path);
            var paths = resolver.TexturePacks(baseline.World.Path, token).Where(p => Path.GetDirectoryName(p)!.Equals(sourceDirectory, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (paths.Length == 0) throw new InvalidDataException("No mission texture packs found; replacement requires every local variant.");
            CountInputs(construction, baseline, token);
            CountBatch(construction, batch, token);
            construction.Reserve(128L * paths.Length + paths.Sum(p => 2L * p.Length), token);
            Dictionary<string,ZbdDocument> textures = new(StringComparer.OrdinalIgnoreCase), baselines = new(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                token.ThrowIfCancellationRequested();
                var source = baseline.Textures.TryGetValue(path, out var edited) ? edited : await OpenTextureAsync(path, resolver, budget.Remaining, construction, token).ConfigureAwait(false);
                budget.Document(source);
                Charge(construction, source, token);
                baselines[path] = source;
                long output = source.Bytes.Length + 56L + 2L * batch.Texture.Width * batch.Texture.Height;
                long decoded = TextureShape(source.Assets.Count + 1, path.Length);
                // Encode returns before Write/verification. Append's reader does not escape
                // its call; the outer OpenBytes starts only after Append has returned.
                long payload = output - source.Bytes.Length;
                long texturePeak = Math.Max(payload + 262_144, output + payload + decoded);
                CheckRoom(construction, texturePeak, token);
                textures[path] = FormatRegistry.Default.OpenBytes(path,
                    TexturePackWriter.Append(source, batch.TextureName, batch.Texture, budget.Remaining, token), source.Stamp, token);
                budget.Document(textures[path]);
                Charge(construction, textures[path], token);
            }
            CheckWorldConstruction(construction, baseline.World, batch, token);
            var world = FormatRegistry.Default.OpenBytes(baseline.World.Path,
                ModelReplacementWriter.Replace(baseline.World, batch.Models, batch.TextureName, token), baseline.World.Stamp, token);
            Charge(construction, world, token); ReserveNewBaselines(construction, baselines.Values, savedPaths.Contains, token);
            construction.Reserve(128, token);
            return new ModelEditSnapshot(world, textures, baselines);
        }
        catch (InvalidDataException) when (construction.Exhausted) { throw EditRetentionBudget.Refusal(); }
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
        // The accepted snapshot and the state it replaces (the newest undo step) must fit; the
        // oldest undo steps that no longer fit are retired and redo is dropped, as documented.
        var retained = Mandatory(snapshot, baselines.Values, maximumRetainedBytes, CancellationToken.None);
        int keep = Math.Min(127, EditRetentionBudget.KeepNewest(undo, retained, (b, s) => ChargeSnapshot(b, s, CancellationToken.None)));
        // Retiring steps never makes the opened file unreachable: its snapshot costs only its own small entry.
        int first = undo.Count > keep && keep < 127 && ReferenceEquals(undo[0], opened) && Fits(retained, opened) ? 1 : 0;
        int retired = undo.Count - keep - first;
        RetainedDocumentBudget construction = new(maximumConstructionBytes);
        try
        {
            ChargeSnapshot(construction, snapshot, CancellationToken.None);
            ReserveNewBaselines(construction, baselines.Values, saved.ContainsKey, CancellationToken.None);
        }
        catch (InvalidDataException) when (construction.Exhausted) { throw EditRetentionBudget.Refusal(); }
        BeforeEdit?.Invoke(snapshot.Textures.Keys.Append(snapshot.World.Path).Concat(saved.Values.Select(s => s.Target)));
        foreach (var doc in baselines.Values)
            if (!saved.ContainsKey(doc.Path)) { saved[doc.Path] = (doc.Path, doc.Bytes, false); originalTextures[doc.Path] = doc; observedStamps[doc.Path] = doc.Stamp; }
        undo.RemoveRange(first, retired);
        undo.Add(Current); redo.Clear(); Current = snapshot;
        // Accepted, it is history rather than a live prepared result: retiring it must free it.
        Discard(snapshot);
        EditAccepted?.Invoke(retired); Changed?.Invoke();
    }
    private bool Fits(RetainedDocumentBudget budget, ModelEditSnapshot snapshot)
    {
        var checkpoint = budget.Checkpoint();
        try { ChargeSnapshot(budget, snapshot, CancellationToken.None); return true; }
        catch (InvalidDataException) when (budget.Exhausted) { budget.Restore(checkpoint); return false; }
    }
    /// <summary>Ends the redo branch when another editor of the document (pickup or AI coordinates) accepts an edit.</summary>
    public void ClearRedo() => redo.Clear();
    /// <summary>Stops charging a prepared result its caller abandoned (or that was accepted) as live content.</summary>
    public void Discard(ModelEditSnapshot snapshot) =>
        prepared.RemoveAll(p => !p.TryGetTarget(out var live) || ReferenceEquals(live, snapshot));
    public void Undo() { if (CanUndo) { redo.Add(Current); Current = undo[^1]; undo.RemoveAt(undo.Count - 1); Changed?.Invoke(); } }
    public void Redo() { if (CanRedo) { undo.Add(Current); Current = redo[^1]; redo.RemoveAt(redo.Count - 1); Changed?.Invoke(); } }
    public string TargetPath(string source) => saved[source].Target;

    /// <summary>Stage/reparse every file before publishing textures first and GameZ last. A partial commit remains explicitly dirty.</summary>
    public async Task<ModelSaveResult> SaveAsync(string? destinationDirectory = null, CancellationToken token = default)
    {
        if (saving || preparing) throw new InvalidOperationException("A model save or preparation is in progress.");
        token.ThrowIfCancellationRequested();
        var documents = Documents.ToDictionary(d => d.Path, StringComparer.OrdinalIgnoreCase);
        var targets = documents.Keys.ToDictionary(path => path, path => destinationDirectory == null ? saved[path].Target : Path.Combine(Path.GetFullPath(destinationDirectory), Path.GetFileName(path)), StringComparer.OrdinalIgnoreCase);
        RetainedDocumentBudget construction = new(maximumConstructionBytes);
        long verification = 0;
        foreach (var doc in documents.Values)
        {
            var prior = saved[doc.Path];
            // All current outputs and baseline hash inputs remain active throughout
            // staging/publication. Older undo/source graphs are separately retained.
            // A published baseline shares the saved document's array, so saving adds no
            // retained content; only the staged copy, readback and verification graph are new.
            Charge(construction, doc, token);
            construction.Bytes(prior.Bytes, token);
            if (destinationDirectory == null && !prior.PendingCopy && doc.Bytes.Span.SequenceEqual(prior.Bytes.Span)) continue;
            construction.Reserve(32L + doc.Bytes.Length, token);
            verification = Math.Max(verification, 32L + doc.Bytes.Length + DecodedCost(doc, token));
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
                if (replace && doc.Bytes.Span.SequenceEqual(prior.Bytes.Span)) continue;
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
                    saved[item.Doc.Path] = (item.Target, item.Doc.Bytes, false);
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
    private static Task CheckExternalAsync(string target, ReadOnlyMemory<byte> baseline, CancellationToken token, DirectoryLease directories) =>
        VerifiedDocumentSave.CheckBaselineAsync(target, baseline, token, directories);

    private static async Task VerifyAsync(SealedFile file, ZbdDocument doc, string target, CancellationToken token)
    {
        byte[] readback = await file.ReadAllAsync(doc.Bytes.Length, token);
        if (!doc.Bytes.Span.SequenceEqual(readback)) throw new IOException("Model save byte verification failed.");
        var parsed = await Task.Run(() => FormatRegistry.Default.OpenBytes(target, readback, token: token), token);
        if (parsed.Diagnostics.Any(d => d.Severity == "Error") || parsed.Assets.Count != doc.Assets.Count)
            throw new InvalidDataException("Model save shared-reader verification failed.");
    }

    /// <summary>Content that stays retained whatever history is retired: the source, original texture packs,
    /// save baselines, Current, live prepared results and an optional candidate with its new baselines.</summary>
    private RetainedDocumentBudget Mandatory(ModelEditSnapshot? candidate, IEnumerable<ZbdDocument> baselines, long maximum, CancellationToken token)
    {
        RetainedDocumentBudget budget = new(maximum);
        try
        {
            Charge(budget, sourceWorld, token);
            foreach (var document in originalTextures.Values) Charge(budget, document, token);
            foreach (var baseline in saved.Values) { budget.Bytes(baseline.Bytes, token); budget.Text(baseline.Target, token); }
            ChargeSnapshot(budget, Current, token);
            // A caller may hold more than one prepared result. Weak receipts count live results
            // without making an abandoned/cancelled preparation into another permanent history.
            for (int i = prepared.Count - 1; i >= 0; i--)
                if (prepared[i].TryGetTarget(out var snapshot)) ChargeSnapshot(budget, snapshot, token);
                else prepared.RemoveAt(i);
            if (candidate != null) ChargeSnapshot(budget, candidate, token);
            int added = ReserveNewBaselines(budget, baselines, saved.ContainsKey, token);
            budget.Reserve(128L * (saved.Count + observedStamps.Count + prepared.Count + added), token);
            return budget;
        }
        catch (InvalidDataException) when (budget.Exhausted) { throw EditRetentionBudget.Refusal(); }
    }
    private void ChargeSnapshot(RetainedDocumentBudget budget, ModelEditSnapshot snapshot, CancellationToken token)
    {
        if (!budget.Object(snapshot, 128, token)) return;
        Charge(budget, snapshot.World, token);
        ChargeAll(snapshot.Textures);
        if (snapshot.Baselines is { } baselines) ChargeAll(baselines);
        void ChargeAll(IReadOnlyDictionary<string, ZbdDocument> documents)
        {
            if (!budget.Object(documents, 128L + 96L * documents.Count, token)) return;
            foreach (var (path, document) in documents) { budget.Text(path, token); Charge(budget, document, token); }
        }
    }
    private void CountInputs(RetainedDocumentBudget budget, ModelEditSnapshot snapshot, CancellationToken token)
    {
        Charge(budget, snapshot.World, token);
        budget.Object(snapshot.Textures, 128L + 96L * snapshot.Textures.Count, token);
        foreach (var (path, document) in snapshot.Textures)
        { budget.Text(path, token); Charge(budget, document, token); }
    }
    /// <summary>A document's raw array (shared by identity with save baselines) plus its measured decoded content.</summary>
    private void Charge(RetainedDocumentBudget budget, ZbdDocument document, CancellationToken token)
    {
        budget.Bytes(document.Bytes, token);
        budget.Object(document, DecodedCost(document, token), token);
    }
    /// <summary>Decoded objects of one immutable document, excluding its raw array; measured once and cached.</summary>
    private long DecodedCost(ZbdDocument document, CancellationToken token)
    {
        if (decodedCosts.TryGetValue(document, out var known)) return known.Value;
        RetainedDocumentBudget shape = new(Math.Max(maximumRetainedBytes, maximumConstructionBytes));
        try
        {
            shape.Bytes(document.Bytes, token);
            long raw = shape.UsedBytes;
            EditRetentionBudget.Document(shape, document, token); CountReaderStorage(shape, document, token);
            long cost = shape.UsedBytes - raw;
            decodedCosts.AddOrUpdate(document, new(cost));
            return cost;
        }
        catch (InvalidDataException) when (shape.Exhausted) { throw EditRetentionBudget.Refusal(); }
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
    // A new save baseline shares its document's array (charged with that document); only its entry is new.
    private static int ReserveNewBaselines(RetainedDocumentBudget budget, IEnumerable<ZbdDocument> documents, Func<string, bool> saved, CancellationToken token)
    {
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        foreach (var doc in documents)
            if (!saved(doc.Path) && paths.Add(doc.Path)) budget.Reserve(256L + 2L * doc.Path.Length, token);
        return paths.Count;
    }
    private static void CheckRoom(RetainedDocumentBudget budget, long bytes, CancellationToken token)
    {
        var checkpoint = budget.Checkpoint(); budget.Reserve(bytes, token); budget.Restore(checkpoint);
    }
    // TextureReader retains seven scalar/string metadata fields, a TextureInfo, name,
    // summary and asset per row; no decoded pixels. 8 KiB covers their object/key storage.
    private static long TextureShape(long count, int pathLength) => 8192L * (count + 1) + 2L * pathLength;
    private async Task<ZbdDocument> OpenTextureAsync(string path, AssetResolver resolver, long remaining,
        RetainedDocumentBudget construction, CancellationToken token)
    {
        if (resolver.WorkspaceSnapshot(path, Guid.Empty) is { } snapshot)
        {
            if (snapshot.Bytes.Length > remaining) throw new InvalidDataException("The texture pack exceeds the remaining edit buffer budget.");
            Charge(construction, snapshot, token); return snapshot;
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
    private void CheckWorldConstruction(RetainedDocumentBudget budget, ZbdDocument world, ModelImportBatch batch, CancellationToken token)
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
        long decoded = DecodedCost(world, token) + addedDecoded;
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
