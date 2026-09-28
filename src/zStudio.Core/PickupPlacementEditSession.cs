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
    }
    private sealed record Entry(PickupPlacementRecord Record, int[] Offsets, int[] RotationOffsets);
    // All mission coordinates share these archive baselines and one save transaction.
    // Pickup behavior remains in this session; AI/vehicle records use typed adapters.
    private readonly Dictionary<MissionPickupSource, MissionCoordinateRecord> otherCoordinates = [];
    private sealed record Move(IReadOnlyDictionary<MissionPickupSource, PlacementTransform> Before, IReadOnlyDictionary<MissionPickupSource, PlacementTransform> After);
    private readonly Dictionary<string, ArchiveState> archives = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<MissionPickupSource, Entry> entries = [];
    private readonly Dictionary<MissionPickupSource, Vector3> positions = [];
    private readonly Dictionary<MissionPickupSource, Vector3> savedPositions = [];
    private readonly Dictionary<MissionPickupSource, Vector3> rotations = [], savedRotations = [];
    private readonly Stack<Move> undo = [], redo = [];
    private readonly List<string> diagnostics = [];
    private bool saving;
    public event Action? Changed;
    public event Action? EditAccepted;
    public event Action? BeforeEdit;
    public IReadOnlyList<string> Diagnostics => diagnostics.AsReadOnly();
    public IReadOnlyList<PickupPlacementRecord> Records => entries.Values.Where(e => !otherCoordinates.ContainsKey(e.Record.Source)).Select(e => e.Record).ToArray();
    public IReadOnlyList<MissionCoordinateRecord> OtherCoordinates => otherCoordinates.Values.ToArray();
    public bool IsDirty => archives.Keys.Any(IsArchiveDirty);
    public bool IsArchiveDirty(string path) => archives[path].PendingCopy || positions.Any(p => p.Key.ArchivePath.Equals(path, StringComparison.OrdinalIgnoreCase) && (p.Value != savedPositions[p.Key] || rotations[p.Key] != savedRotations[p.Key]));
    public bool CanUndo => !saving && undo.Count > 0;
    public bool CanRedo => !saving && redo.Count > 0;
    public IReadOnlyList<string> ArchivePaths => archives.Values.Select(a => a.Original.Path).ToArray();
    public string TargetPath(string source) => archives[source].Target;

    public static async Task<PickupPlacementEditSession> LoadAsync(string worldPath, AssetResolver resolver, CancellationToken token = default)
    {
        Dictionary<string, (ZbdDocument Archive, AssetRecord Asset)> found = new(StringComparer.OrdinalIgnoreCase);
        List<(ZbdDocument Archive, AssetRecord Asset)> coordinateResources = [];
        List<string> notes = [];
        foreach (string file in MissionSceneLoader.ResourceFiles(worldPath, resolver))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (FormatRegistry.Probe(file).Family != FormatFamily.Archive) continue;
                var archive = await resolver.OpenCachedAsync(file, token).ConfigureAwait(false);
                coordinateResources.AddRange(archive.Assets.Where(a => IsCoordinateResource(a.Name)).Select(a => (archive, a)));
                foreach (var asset in archive.Assets.Where(a => IsPickupResource(a.Name) || IsCoordinateResource(a.Name))) found.TryAdd(asset.Name, (archive, asset));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { notes.Add($"Pickup editing: {Path.GetFileName(file)}: {ex.Message}"); }
        }
        List<PickupPlacementResource> resources = [];
        foreach (var difficulty in Enum.GetValues<MissionDifficulty>())
        {
            string requested = MissionLayoutSelection.For(difficulty).PickupResource;
            string effective = found.ContainsKey(requested) ? requested : "puppies.zrd";
            if (found.TryGetValue(effective, out var resource)) resources.Add(new(difficulty, resource.Archive, resource.Asset));
            else notes.Add($"{difficulty}: pickup resource unavailable.");
        }
        return await Task.Run(() =>
        {
            var result = Create(resources, notes, token);
            result.AddCoordinates(coordinateResources, token);
            return result;
        }, token).ConfigureAwait(false);
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
                if (doc.Assets.Any(a => a.Index != asset.Index && a.Offset < asset.Offset + asset.Length && asset.Offset < a.Offset + a.Length))
                    throw new InvalidDataException("Pickup member overlaps another archive member.");
                var tree = ZrdDecoder.Decode(doc.Slice(asset.Offset, asset.Length), token);
                if (tree["children"] is not JsonArray { Count: 1 } root || root[0]?["children"] is not JsonArray rows)
                    throw new InvalidDataException("Expected an ordered pickup placement list.");
                result.archives.TryAdd(group.Key.Item1, new(doc));
                for (int index = 0; index < rows.Count; index++)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        if (rows[index]?["children"] is not JsonArray { Count: 5 } row || row[0].Text("type") != "string" || row[1].Text("type") != "int")
                            throw new InvalidDataException("Invalid pickup record shape.");
                        string type = row[0].Text("value");
                        if (!MissionPickupType.Catalog.Any(t => t.Name == type)) throw new InvalidDataException("Unknown pickup type.");
                        var (position, offsets) = ReadVector(row[2], doc, asset);
                        var (rotation, rotationOffsets) = ReadVector(row[3], doc, asset);
                        _ = ReadNumber(row[4]);
                        var source = new MissionPickupSource(group.Key.Item1, asset.Index, asset.Name.ToUpperInvariant(), index);
                        var record = new PickupPlacementRecord(source, type, position, rotation, group.Select(r => r.Difficulty).Distinct().Order().ToArray());
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
    private static (Vector3 Vector, int[] Offsets) ReadVector(JsonNode? node, ZbdDocument doc, AssetRecord asset)
    {
        if (node?["children"] is not JsonArray { Count: 3 } components) throw new InvalidDataException("Expected three position/rotation components.");
        float[] values = new float[3]; int[] offsets = new int[3];
        for (int i = 0; i < 3; i++)
        {
            values[i] = ReadNumber(components[i]);
            offsets[i] = ReadScalarOffset(components[i], doc, asset);
        }
        return (new(values[0], values[1], values[2]), offsets);
    }
    private static int ReadScalarOffset(JsonNode? node, ZbdDocument doc, AssetRecord asset)
    {
        string text = node.Text("offset");
        if (!text.StartsWith("0x", StringComparison.Ordinal) || !int.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int offset) || offset < 0 || offset > asset.Length - 8)
            throw new InvalidDataException("Invalid transform source range.");
        int absolute = checked((int)asset.Offset + offset); _ = doc.Slice(absolute, 8); return absolute;
    }
    private static float ReadNumber(JsonNode? value)
    {
        if (value.Text("type") is not ("int" or "float") || !float.TryParse(value?["value"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float number) || !float.IsFinite(number))
            throw new InvalidDataException("Expected a finite numeric coordinate.");
        return number;
    }
    public PickupPlacementRecord? Find(MissionPickupSource source) => otherCoordinates.ContainsKey(source) ? null : entries.GetValueOrDefault(source)?.Record;
    public Vector3 Position(MissionPickupSource source) => positions[source];
    public Vector3 Rotation(MissionPickupSource source) => rotations[source];
    public PlacementTransform Transform(MissionPickupSource source) => new(positions[source], rotations[source]);
    public PlacementRotationKind RotationKind(MissionPickupSource source) => entries[source].RotationOffsets.Length switch
    { 3 => PlacementRotationKind.EulerRadians, 1 => PlacementRotationKind.HeadingDegrees, _ => PlacementRotationKind.None };
    public PickupPlacementScope Scope(MissionPickupSource source)
    {
        if (otherCoordinates.TryGetValue(source, out var coordinate)) return CoordinateScope(coordinate);
        var selected = entries[source].Record;
        var same = Records.Where(r => r.Type == selected.Type && r.OriginalPosition == selected.OriginalPosition && r.Rotation == selected.Rotation).ToArray();
        var groups = same.GroupBy(r => (r.Source.ArchivePath, r.Source.AssetIndex)).ToArray();
        bool uniqueSource = groups.Single(g => g.Key == (source.ArchivePath, source.AssetIndex)).Count() == 1;
        var matches = uniqueSource ? groups.Where(g => g.Count() == 1).Select(g => g.Single()).ToArray() : [selected];
        var affected = matches.SelectMany(r => r.Difficulties).Distinct().Order().ToArray();
        var skipped = Enum.GetValues<MissionDifficulty>().Except(affected).ToArray();
        string description = "Applies to: " + string.Join(", ", affected);
        if (skipped.Length > 0) description += ". Unmatched or ambiguous (unchanged): " + string.Join(", ", skipped);
        return new(matches.Select(r => r.Source).ToArray(), description);
    }
    public bool MoveTo(MissionPickupSource source, Vector3 position) => TransformTo(source, Transform(source) with { Position = position });
    public bool TransformTo(MissionPickupSource source, PlacementTransform transform)
    {
        if (saving) throw new InvalidOperationException("Wait for the current save to finish.");
        var after = PreviewTransform(source, transform);
        var before = after.Keys.ToDictionary(s => s, Transform);
        if (after.All(p => p.Value == before[p.Key])) return false;
        BeforeEdit?.Invoke(); undo.Push(new(before, after)); redo.Clear(); EditAccepted?.Invoke(); Apply(after); return true;
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
            (vehicle.TemplateSourceNode == null || vehicle.Difficulties.Count == 0))
            throw new InvalidDataException("The tank placement has no unambiguous active template binding.");
        if (otherCoordinates.TryGetValue(source, out var record) && record.Kind == "ai" &&
            (Math.Abs(position.X) > 1e12 || Math.Abs(position.Y) > 1e12 || Math.Abs(position.Z) > 1e12))
            throw new InvalidDataException("AI coordinates must stay within the supported ±1e12 preview range.");
        Vector3 delta = position - positions[source]; RequireFinite(delta);
        Vector3 rotationDelta = transform.Rotation - rotations[source]; RequireFinite(rotationDelta);
        var after = Scope(source).Sources.ToDictionary(s => s, s => s == source ? transform : new PlacementTransform(positions[s] + delta, rotations[s] + rotationDelta));
        foreach (var value in after.Values) { RequireFinite(value.Position); RequireFinite(value.Rotation); }
        return after;
    }
    public void Undo() { if (CanUndo) { var move = undo.Pop(); redo.Push(move); Apply(move.Before); } }
    public void Redo() { if (CanRedo) { var move = redo.Pop(); undo.Push(move); Apply(move.After); } }
    private void Apply(IReadOnlyDictionary<MissionPickupSource, PlacementTransform> values)
    { foreach (var p in values) { positions[p.Key] = p.Value.Position; rotations[p.Key] = p.Value.Rotation; } Changed?.Invoke(); }
    private static void RequireFinite(Vector3 value)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z)) throw new InvalidDataException("Transform components must be finite game floats.");
    }
    public byte[] EncodeArchive(string archivePath)
    {
        var archive = archives[archivePath]; byte[] output = archive.Original.Bytes.ToArray();
        foreach (var entry in entries.Values.Where(e => e.Record.Source.ArchivePath.Equals(archivePath, StringComparison.OrdinalIgnoreCase)))
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
        var identities = Scope(source).Sources.ToHashSet();
        var found = mission.Actors.Where(a => a.Pickup is { } p && identities.Contains(p.Source)).ToArray();
        return found.Length == 1 ? found[0].Pickup!.Source : null;
    }
    public bool HasExternalChanges()
    {
        foreach (var archive in archives.Values)
            try { if (archive.PendingCopy ? File.Exists(archive.Target) || Directory.Exists(archive.Target) : FileStamp.Read(archive.Target) != archive.Stamp) return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
        return HasSourceChanges();
    }
    public bool HasSourceChanges()
    {
        foreach (var archive in archives.Values)
            try { if (FileStamp.Read(archive.Original.Path) != archive.SourceStamp) return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
        return false;
    }
}
