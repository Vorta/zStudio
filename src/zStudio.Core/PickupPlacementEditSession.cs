using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core;

public sealed record PickupPlacementResource(MissionDifficulty Difficulty, ZbdDocument Archive, AssetRecord Asset);
public sealed record PickupPlacementRecord(MissionPickupSource Source, string Type, Vector3 OriginalPosition,
    Vector3 Rotation, IReadOnlyList<MissionDifficulty> Difficulties);
public sealed record PickupPlacementScope(IReadOnlyList<MissionPickupSource> Sources, string Description);

/// <summary>Authored placement edits; never owns or changes a runtime mission scene.</summary>
public sealed partial class PickupPlacementEditSession
{
    private sealed class ArchiveState(ZbdDocument document)
    {
        public ZbdDocument Original { get; } = document;
        public string Target { get; set; } = document.Path;
        public bool PendingCopy { get; set; }
        public byte[] SavedBytes { get; set; } = document.Bytes.ToArray();
        public FileStamp Stamp { get; set; } = document.Stamp;
        public FileStamp SourceStamp { get; set; } = document.Stamp;
        // A working copy published by another document; its file stamp does not describe its bytes.
        public bool FromSnapshot { get; set; }
    }
    private sealed record Entry(PickupPlacementRecord Record, int[] Offsets, int[] RotationOffsets);
    // All mission coordinates share these archive baselines and one save transaction.
    // Pickup behavior remains in this session; AI/vehicle records use typed adapters.
    private readonly Dictionary<MissionPickupSource, MissionCoordinateRecord> otherCoordinates;
    private sealed record Move(IReadOnlyDictionary<MissionPickupSource, PlacementTransform> Before, IReadOnlyDictionary<MissionPickupSource, PlacementTransform> After);
    private readonly Dictionary<string, ArchiveState> archives = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<MissionPickupSource, Entry> entries;
    private readonly Dictionary<MissionPickupSource, Vector3> positions;
    private readonly Dictionary<MissionPickupSource, Vector3> savedPositions;
    private readonly Dictionary<MissionPickupSource, Vector3> rotations, savedRotations;
    private readonly Stack<Move> undo = [], redo = [];
    private readonly PreviewNotes diagnostics = new();
    private readonly Dictionary<ZbdDocument, HashSet<AssetRecord>> overlappingMembers = [];
    // Archives changed by accepted history or saved copies. Only these are owned, published and
    // checked for external changes; other readers stay free for independent resource documents.
    private readonly HashSet<string> touched = new(StringComparer.OrdinalIgnoreCase);
    private bool saving;
    public event Action? Changed;
    public event Action? EditAccepted;
    /// <summary>Raised with the source paths an accepted transform will change, before history changes.</summary>
    public event Action<IReadOnlyList<string>>? BeforeEdit;
    public IReadOnlyList<string> Diagnostics => diagnostics;
    public int DiagnosticCount => diagnostics.TotalCount;
    public IReadOnlyList<PickupPlacementRecord> Records => IndexedRecords();
    public IReadOnlyList<MissionCoordinateRecord> OtherCoordinates => otherCoordinates.Values.ToArray();
    public bool IsDirty => archives.Keys.Any(IsArchiveDirty);
    public bool IsArchiveDirty(string path) => archives[path].PendingCopy || EntriesForArchive(path).Any(e => positions[e.Record.Source] != savedPositions[e.Record.Source] || rotations[e.Record.Source] != savedRotations[e.Record.Source]);
    public bool CanUndo => !saving && undo.Count > 0;
    public bool CanRedo => !saving && redo.Count > 0;
    public IReadOnlyList<string> ArchivePaths => archives.Values.Select(a => a.Original.Path).ToArray();
    public IReadOnlyList<string> EditedArchivePaths => archives.Where(a => touched.Contains(a.Key)).Select(a => a.Value.Original.Path).ToArray();
    public bool IsArchiveEdited(string path) => touched.Contains(Path.GetFullPath(path));
    public string TargetPath(string source) => archives[source].Target;
    /// <summary>Whether an archive saves to a copy (a Save As destination, or one still being created) rather than its source.</summary>
    public bool HasCopyTargets => archives.Values.Any(a => a.PendingCopy || !Path.GetFullPath(a.Target).Equals(Path.GetFullPath(a.Original.Path), StringComparison.OrdinalIgnoreCase));

    /// <summary>Why a world's placements cannot be edited (they remain inspectable), or null.</summary>
    public string? ReadOnlyReason { get; private set; }
    /// <summary>
    /// Why the placements of a world with this format are read-only: the 1998 demos' worlds (GameZ version 13) open
    /// read-only, and so do the placements of their missions; their archives are never written.
    /// </summary>
    public static string? ReadOnlyWorld(FormatProbe probe) => probe is { Family: FormatFamily.GameZ, Version: 13 }
        ? "This is a 1998 demo world (GameZ version 13), which opens read-only: its placements can be inspected, not edited." : null;

    public static Task<PickupPlacementEditSession> LoadAsync(string worldPath, AssetResolver resolver, CancellationToken token = default)
        => LoadAsync(worldPath, resolver, new AssetReadBudget(), token);
    internal static Task<PickupPlacementEditSession> LoadAsync(string worldPath, AssetResolver resolver, AssetReadBudget reads, CancellationToken token)
        => LoadWithResourcesAsync(worldPath, resolver, new PreviewResourceBudget(reads), token);
    internal static async Task<PickupPlacementEditSession> LoadWithResourcesAsync(string worldPath, AssetResolver resolver, PreviewResourceBudget resourcesBudget, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        resourcesBudget.Check(token);
        long revision = resolver.SnapshotRevision;
        Dictionary<string, (ZbdDocument Archive, AssetRecord Asset)> found = new(StringComparer.OrdinalIgnoreCase);
        List<(ZbdDocument Archive, AssetRecord Asset)> coordinateResources = [];
        PreviewNotes notes = new();
        var probe = FormatRegistry.Probe(worldPath);
        bool mw3 = probe is { Family: FormatFamily.GameZ, Version: 27 };
        // Load the coordinate store once for every reader in this map. Preview selection
        // filters by archive identity; changing mission must never discard accepted history.
        var files = MissionSceneLoader.ResourceFiles(worldPath, resolver); HashSet<ZbdDocument> snapshots = new(ReferenceEqualityComparer.Instance);
        foreach (string file in files)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (FormatRegistry.Probe(file).Family != FormatFamily.Archive) continue;
                var archive = await resourcesBudget.OpenAsync(file, resolver, token).ConfigureAwait(false);
                if (resolver.IsWorkspaceSnapshot(archive)) snapshots.Add(archive);
                coordinateResources.AddRange(archive.Assets.Where(a => IsCoordinateResource(a.Name)).Select(a => (archive, a)));
                foreach (var asset in archive.Assets.Where(a => IsPickupResource(a.Name) || IsCoordinateResource(a.Name))) found.TryAdd(asset.Name, (archive, asset));
            }
            // A missing/damaged file remains a diagnostic. Capacity exhaustion cannot publish a
            // partial store: it could hide a difficulty counterpart or an independently owned actor.
            catch (Exception ex) when (!resourcesBudget.Exhausted && (ex is IOException or UnauthorizedAccessException or InvalidDataException))
            { notes.Add($"Pickup editing: {Path.GetFileName(file)}: {ex.Message}"); }
        }
        List<PickupPlacementResource> resources = [];
        foreach (var difficulty in mw3 ? Array.Empty<MissionDifficulty>() : Enum.GetValues<MissionDifficulty>())
        {
            string requested = MissionLayoutSelection.For(difficulty).PickupResource;
            string effective = found.ContainsKey(requested) ? requested : "puppies.zrd";
            if (found.TryGetValue(effective, out var resource)) resources.Add(new(difficulty, resource.Archive, resource.Asset));
            else notes.Add($"{difficulty}: pickup resource unavailable.");
        }
        var loaded = await Task.Run(() =>
        {
            var result = Create(resources, notes, token);
            result.ReadOnlyReason = ReadOnlyWorld(probe);
            result.AddCoordinates(coordinateResources, token, mw3);
            foreach (var archive in result.archives.Values) archive.FromSnapshot = snapshots.Contains(archive.Original);
            return result;
        }, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (resolver.SnapshotRevision != revision)
            throw new InvalidDataException("Mission coordinate sources changed while loading; retry with the current workspace.");
        return loaded;
    }
    private static bool IsPickupResource(string name) => name.ToLowerInvariant() is "puppies.zrd" or "puppies_easy.zrd" or "puppies_hard.zrd";

    public static PickupPlacementEditSession Create(IEnumerable<PickupPlacementResource> resources, IEnumerable<string>? notes = null, CancellationToken token = default)
    {
        var result = new PickupPlacementEditSession();
        if (notes != null) result.diagnostics.AddRange(notes);
        foreach (var group in resources.GroupBy(r => (Path.GetFullPath(r.Archive.Path).ToUpperInvariant(), r.Asset.Index)))
        {
            var resource = group.First(); var doc = resource.Archive; var asset = resource.Asset;
            try
            {
                if (doc.Probe.Family != FormatFamily.Archive || doc.Diagnostics.Any(d => d.Severity == "Error") || !IsPickupResource(asset.Name))
                    throw new InvalidDataException("An intact ZAR pickup resource is required.");
                if (result.Overlaps(doc, asset, token))
                    throw new InvalidDataException("Pickup member overlaps another archive member.");
                var tree = ZrdDecoder.ReadAsset(doc, asset, token);
                if (tree.Kind != ZrdKind.Array || tree.Children is not { Count: 1 } root || root[0].Kind != ZrdKind.Array)
                    throw new InvalidDataException("Expected an ordered pickup placement list.");
                var rows = root[0].Children;
                if (!result.archives.ContainsKey(group.Key.Item1)) result.archives.Add(group.Key.Item1, new(doc));
                string? memberIdentity = null;
                for (int index = 0; index < rows.Count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if (rows[index].Kind != ZrdKind.Array || rows[index].Children is not { Count: 5 } row || row[0].Kind != ZrdKind.String || row[1].Kind != ZrdKind.Int)
                            throw new InvalidDataException("Invalid pickup record shape.");
                        string type = row[0].Text;
                        if (!MissionPickupType.Catalog.Any(t => t.Name == type)) throw new InvalidDataException("Unknown pickup type.");
                        var (position, offsets) = ReadVector(row[2], doc, asset);
                        var (rotation, rotationOffsets) = ReadVector(row[3], doc, asset);
                        _ = ReadNumber(row[4]);
                        var source = new MissionPickupSource(group.Key.Item1, asset.Index, memberIdentity ??= asset.Name.ToUpperInvariant(), index);
                        var record = new PickupPlacementRecord(source, type, position, rotation, group.Select(r => r.Difficulty).Distinct().Order().ToArray());
                        result.sourceComparer.Register(source);
                        result.entries.Add(source, new(record, offsets, rotationOffsets)); result.positions.Add(source, position); result.savedPositions.Add(source, position);
                        result.rotations.Add(source, rotation); result.savedRotations.Add(source, rotation);
                    }
                    catch (Exception ex) when (ex is InvalidDataException or OverflowException or FormatException)
                    { result.diagnostics.Add($"{asset.Name} #{index}: {ex.Message}"); }
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or OverflowException or FormatException)
            { result.diagnostics.Add($"{asset.Name} in {doc.Path}: {ex.Message}"); }
        }
        return result;
    }
    private static (Vector3 Vector, int[] Offsets) ReadVector(ZrdNode node, ZbdDocument doc, AssetRecord asset)
    {
        if (node.Kind != ZrdKind.Array || node.Children is not { Count: 3 } components) throw new InvalidDataException("Expected three position/rotation components.");
        float[] values = new float[3]; int[] offsets = new int[3];
        for (int i = 0; i < 3; i++)
        {
            values[i] = ReadNumber(components[i]);
            offsets[i] = ReadScalarOffset(components[i], doc, asset);
        }
        return (new(values[0], values[1], values[2]), offsets);
    }
    private static int ReadScalarOffset(ZrdNode node, ZbdDocument doc, AssetRecord asset)
    {
        long offset = node.SourceOffset;
        if (offset < 0 || offset > asset.Length - 8) throw new InvalidDataException("Invalid transform source range.");
        int absolute = checked((int)(asset.Offset + offset)); _ = doc.Slice(absolute, 8); return absolute;
    }
    private static float ReadNumber(ZrdNode value)
    {
        float number = value.Kind switch { ZrdKind.Int => unchecked((int)value.Bits), ZrdKind.Float => BitConverter.UInt32BitsToSingle(value.Bits), _ => float.NaN };
        if (!float.IsFinite(number)) throw new InvalidDataException("Expected a finite numeric coordinate.");
        return number;
    }
    private bool Overlaps(ZbdDocument doc, AssetRecord asset, CancellationToken token)
    {
        if (!overlappingMembers.TryGetValue(doc, out var overlaps))
        {
            overlaps = []; AssetRecord? furthest = null;
            foreach (var member in doc.Assets.OrderBy(a => a.Offset).ThenBy(a => a.Length))
            {
                token.ThrowIfCancellationRequested();
                if (furthest != null && furthest.Offset + furthest.Length > member.Offset && furthest.Offset < member.Offset + member.Length)
                { overlaps.Add(furthest); overlaps.Add(member); }
                if (furthest == null || member.Offset + member.Length > furthest.Offset + furthest.Length) furthest = member;
            }
            overlappingMembers.Add(doc, overlaps);
        }
        return overlaps.Contains(asset);
    }
    public PickupPlacementRecord? Find(MissionPickupSource source) => otherCoordinates.ContainsKey(source) ? null : entries.GetValueOrDefault(source)?.Record;
    public Vector3 Position(MissionPickupSource source) => positions[source];
    public Vector3 Rotation(MissionPickupSource source) => rotations[source];
    public PlacementTransform Transform(MissionPickupSource source) => new(positions[source], rotations[source]);
    public PlacementRotationKind RotationKind(MissionPickupSource source) => entries[source].RotationOffsets.Length switch
    { 3 => PlacementRotationKind.EulerRadians, 1 => PlacementRotationKind.HeadingDegrees, _ => PlacementRotationKind.None };
    public PickupPlacementScope Scope(MissionPickupSource source)
        => IndexedScope(source);
    public bool MoveTo(MissionPickupSource source, Vector3 position) => TransformTo(source, Transform(source) with { Position = position });
    public bool TransformTo(MissionPickupSource source, PlacementTransform transform)
    {
        if (ReadOnlyReason is { } reason) throw new InvalidOperationException(reason);
        if (saving) throw new InvalidOperationException("Wait for the current save to finish.");
        var after = PreviewTransform(source, transform);
        var before = after.Keys.ToDictionary(s => s, Transform, sourceComparer);
        if (after.All(p => p.Value == before[p.Key])) return false;
        string[] affected = after.Keys.Select(s => s.ArchivePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        BeforeEdit?.Invoke(affected.Select(a => archives[a].Original.Path).ToArray());
        foreach (string archive in affected) touched.Add(archive);
        undo.Push(new(before, after)); redo.Clear(); EditAccepted?.Invoke(); Apply(after); return true;
    }
    /// <summary>Validate and derive linked transforms without accepting an edit or changing history.</summary>
    public IReadOnlyDictionary<MissionPickupSource, PlacementTransform> PreviewTransform(MissionPickupSource source, PlacementTransform transform)
    {
        var position = transform.Position; RequireFinite(position); RequireFinite(transform.Rotation);
        var kind = RotationKind(source);
        if (kind == PlacementRotationKind.None && transform.Rotation != rotations[source] ||
            kind == PlacementRotationKind.HeadingDegrees && (transform.Rotation.X != 0 || transform.Rotation.Z != 0))
            throw new InvalidDataException("This placement does not support the requested rotation axes.");
        if (otherCoordinates.TryGetValue(source, out var vehicle) && vehicle.Kind == "tank" &&
            (!vehicle.MissionSpecific && (vehicle.TemplateSourceNode == null || vehicle.Difficulties.Count == 0)))
            throw new InvalidDataException("The tank placement has no unambiguous active template binding.");
        if (otherCoordinates.TryGetValue(source, out var record) && (record.Kind == "ai" || record.MissionSpecific) &&
            (Math.Abs(position.X) > 1e12 || Math.Abs(position.Y) > 1e12 || Math.Abs(position.Z) > 1e12))
            throw new InvalidDataException("Mission coordinates must stay within the supported ±1e12 preview range.");
        Vector3 delta = position - positions[source]; RequireFinite(delta);
        Vector3 rotationDelta = transform.Rotation - rotations[source]; RequireFinite(rotationDelta);
        // A counterpart where the source is takes the requested values exactly: arithmetic on the delta would round them
        // apart, and the records would no longer match as counterparts.
        var after = Scope(source).Sources.ToDictionary(s => s, s => sourceComparer.Equals(s, source) || positions[s] == positions[source] && rotations[s] == rotations[source] ? transform
            : new PlacementTransform(positions[s] + delta, rotations[s] + rotationDelta), sourceComparer);
        foreach (var value in after.Values) { RequireFinite(value.Position); RequireFinite(value.Rotation); }
        return after;
    }
    public void Undo() { if (CanUndo) { var move = undo.Pop(); redo.Push(move); Apply(move.Before); } }
    public void Redo() { if (CanRedo) { var move = redo.Pop(); undo.Push(move); Apply(move.After); } }
    /// <summary>Ends the redo branch when another editor of the document (model replacement) accepts an edit.</summary>
    public void ClearRedo() => redo.Clear();
    private void Apply(IReadOnlyDictionary<MissionPickupSource, PlacementTransform> values)
    { foreach (var p in values) { positions[p.Key] = p.Value.Position; rotations[p.Key] = p.Value.Rotation; } Changed?.Invoke(); }
    private static void RequireFinite(Vector3 value)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z)) throw new InvalidDataException("Transform components must be finite game floats.");
    }
    /// <summary>
    /// The scalar writes <paramref name="values"/> need in the stored archives: for each component that differs from the
    /// stored value, the archive, the offset of its node and the new value. Used to carry a placement edit back to the
    /// sources a built archive came from (see <see cref="Sources.SourceResourceEdits"/>).
    /// </summary>
    public IReadOnlyList<(string ArchivePath, int Offset, float Value)> ScalarWrites(IReadOnlyDictionary<MissionPickupSource, PlacementTransform> values)
    {
        List<(string, int, float)> writes = [];
        foreach (var (source, transform) in values)
        {
            var entry = entries[source];
            for (int axis = 0; axis < 3; axis++)
                if (transform.Position[axis] != entry.Record.OriginalPosition[axis]) writes.Add((source.ArchivePath, entry.Offsets[axis], transform.Position[axis]));
            for (int i = 0; i < entry.RotationOffsets.Length; i++)
            {
                int axis = entry.RotationOffsets.Length == 1 ? 1 : i;
                if (transform.Rotation[axis] != entry.Record.Rotation[axis]) writes.Add((source.ArchivePath, entry.RotationOffsets[i], transform.Rotation[axis]));
            }
        }
        return writes;
    }
    /// <summary>The bytes of a loaded archive as this session read them.</summary>
    public ReadOnlyMemory<byte> ArchiveBytes(string archivePath) => archives[archivePath].Original.Bytes;
    public byte[] EncodeArchive(string archivePath)
    {
        var archive = archives[archivePath]; byte[] output = archive.Original.Bytes.ToArray();
        foreach (var entry in EntriesForArchive(archivePath))
        {
            foreach (var (offset, value, original) in Scalars(entry))
            {
                if (value == original) continue;
                BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(offset, 4), 2);
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(offset + 4, 4), value);
            }
        }
        return output;
    }
    private IEnumerable<(int Offset, float Value, float Original)> Scalars(Entry entry)
    {
        var source = entry.Record.Source;
        for (int axis = 0; axis < 3; axis++) yield return (entry.Offsets[axis], positions[source][axis], entry.Record.OriginalPosition[axis]);
        for (int i = 0; i < entry.RotationOffsets.Length; i++)
        {
            int axis = entry.RotationOffsets.Length == 1 ? 1 : i;
            yield return (entry.RotationOffsets[i], rotations[source][axis], entry.Record.Rotation[axis]);
        }
    }
    public IReadOnlyDictionary<int, Vector3> PreviewPositions(MissionSceneContext mission) => mission.Actors
        .Where(a => a.Pickup is { } p && positions.ContainsKey(p.Source))
        .ToDictionary(a => a.Root, a => positions[a.Pickup!.Source]);
    public MissionPickupSource? MatchIn(MissionPickupSource source, MissionSceneContext mission)
    {
        if (!entries.ContainsKey(source)) return null;
        var identities = Scope(source).Sources.ToHashSet(sourceComparer);
        var found = mission.Actors.Where(a => a.Pickup is { } p && identities.Contains(p.Source)).ToArray();
        return found.Length == 1 ? found[0].Pickup!.Source : null;
    }
    private IEnumerable<ArchiveState> Edited => archives.Where(a => touched.Contains(a.Key)).Select(a => a.Value);
    /// <summary>External changes to edited archives. Unedited readers are rebased instead of blocking.</summary>
    public bool HasExternalChanges()
    {
        foreach (var archive in Edited)
            try { if (archive.PendingCopy ? File.Exists(archive.Target) || Directory.Exists(archive.Target) : FileStamp.Read(archive.Target) != archive.Stamp) return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
        return HasSourceChanges();
    }
    public bool HasSourceChanges()
    {
        foreach (var archive in Edited)
            try { if (FileStamp.Read(archive.Original.Path) != archive.SourceStamp) return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
        return false;
    }
    /// <summary>
    /// Whether an unedited archive's baseline differs from the source now served: another
    /// document's published working copy (compared by identity) or otherwise the file.
    /// </summary>
    public bool HasStaleBaselines(Func<string, ZbdDocument?> published, IEnumerable<string>? paths = null)
    {
        var scope = paths?.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, archive) in archives)
        {
            if (touched.Contains(key) || scope?.Contains(key) == false) continue;
            if (published(archive.Original.Path) is { } current) { if (!ReferenceEquals(current, archive.Original)) return true; continue; }
            if (archive.FromSnapshot) return true; // The working copy was closed or discarded.
            try { if (FileStamp.Read(archive.Original.Path) != archive.SourceStamp) return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
        }
        return false;
    }
    /// <summary>
    /// Archives saved to a Save As copy whose original file changed outside zStudio, while no history or unsaved change
    /// depends on their baselines: <see cref="RebaseUntouched"/> then reads the changed original and keeps the copy as target.
    /// </summary>
    public bool HasChangedCopySources() => ChangedCopySources().Any();
    private IEnumerable<string> ChangedCopySources()
    {
        if (saving || undo.Count > 0 || redo.Count > 0 || IsDirty) yield break;
        foreach (var (key, archive) in archives)
        {
            if (!touched.Contains(key) || archive.PendingCopy || Path.GetFullPath(archive.Target).Equals(Path.GetFullPath(archive.Original.Path), StringComparison.OrdinalIgnoreCase)) continue;
            FileStamp current;
            // An unreadable original is not a new baseline to follow; the external-change guard reports it.
            try { current = FileStamp.Read(archive.Original.Path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (current != archive.SourceStamp) yield return key;
        }
    }
    /// <summary>
    /// Adopt freshly loaded baselines for archives this session never edited. Edited archives
    /// keep their owned baseline, history and save targets; no edit or revision is recorded.
    /// An archive saved to a copy also adopts its changed original (<see cref="HasChangedCopySources"/>), keeping its copy target.
    /// </summary>
    public void RebaseUntouched(PickupPlacementEditSession fresh)
    {
        if (saving) throw new InvalidOperationException("Wait for the current save to finish.");
        // Follow only a file read from disk; another document's unsaved working copy of the original is not followed.
        var follow = ChangedCopySources().Where(k => fresh.archives.TryGetValue(k, out var reloaded) && !reloaded.FromSnapshot)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        InvalidateMembership();
        Dictionary<string, bool> untouched = new(ReferenceEqualityComparer.Instance);
        bool Untouched(MissionPickupSource s)
        {
            if (!untouched.TryGetValue(s.ArchivePath, out bool value)) untouched.Add(s.ArchivePath, value = !touched.Contains(s.ArchivePath) || follow.Contains(s.ArchivePath));
            return value;
        }
        foreach (var key in entries.Keys.Where(Untouched).ToArray())
        { entries.Remove(key); positions.Remove(key); savedPositions.Remove(key); rotations.Remove(key); savedRotations.Remove(key); otherCoordinates.Remove(key); }
        foreach (string key in archives.Keys.Where(k => !touched.Contains(k)).ToArray()) archives.Remove(key);
        foreach (var (key, archive) in fresh.archives)
        {
            if (!touched.Contains(key)) archives[key] = archive;
            else if (follow.Contains(key))
            {
                // The copy stays the save target, verified against the bytes last written there.
                var copy = archives[key];
                archive.Target = copy.Target; archive.Stamp = copy.Stamp; archive.SavedBytes = copy.SavedBytes;
                archives[key] = archive;
            }
        }
        foreach (var (key, entry) in fresh.entries)
        {
            if (!Untouched(key)) continue;
            sourceComparer.Register(key);
            entries[key] = entry; positions[key] = fresh.positions[key]; savedPositions[key] = fresh.savedPositions[key];
            rotations[key] = fresh.rotations[key]; savedRotations[key] = fresh.savedRotations[key];
            if (fresh.otherCoordinates.TryGetValue(key, out var coordinate)) otherCoordinates[key] = coordinate;
        }
        sourceComparer.Reset(entries.Keys);
        overlappingMembers.Clear();
    }
}
