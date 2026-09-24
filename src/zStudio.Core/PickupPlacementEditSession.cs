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
        public byte[] SavedBytes { get; set; } = document.Bytes.ToArray();
        public FileStamp Stamp { get; set; } = document.Stamp;
        public FileStamp SourceStamp { get; set; } = document.Stamp;
    }
    private sealed record Entry(PickupPlacementRecord Record, int[] Offsets);
    private sealed record Move(Dictionary<MissionPickupSource, Vector3> Before, Dictionary<MissionPickupSource, Vector3> After);
    private readonly Dictionary<string, ArchiveState> archives = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<MissionPickupSource, Entry> entries = [];
    private readonly Dictionary<MissionPickupSource, Vector3> positions = [];
    private readonly Dictionary<MissionPickupSource, Vector3> savedPositions = [];
    private readonly Stack<Move> undo = [], redo = [];
    private readonly List<string> diagnostics = [];
    private bool saving;
    public event Action? Changed;
    public IReadOnlyList<string> Diagnostics => diagnostics.AsReadOnly();
    public IReadOnlyList<PickupPlacementRecord> Records => entries.Values.Select(e => e.Record).ToArray();
    public bool IsDirty => positions.Any(p => p.Value != savedPositions[p.Key]);
    public bool IsArchiveDirty(string path) => positions.Any(p => p.Key.ArchivePath.Equals(path, StringComparison.OrdinalIgnoreCase) && p.Value != savedPositions[p.Key]);
    public bool CanUndo => !saving && undo.Count > 0;
    public bool CanRedo => !saving && redo.Count > 0;
    public IReadOnlyList<string> ArchivePaths => archives.Values.Select(a => a.Original.Path).ToArray();
    public string TargetPath(string source) => archives[source].Target;

    public static async Task<PickupPlacementEditSession> LoadAsync(string worldPath, AssetResolver resolver, CancellationToken token = default)
    {
        Dictionary<string, (ZbdDocument Archive, AssetRecord Asset)> found = new(StringComparer.OrdinalIgnoreCase);
        List<string> notes = [];
        foreach (string file in MissionSceneLoader.ResourceFiles(worldPath, resolver))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (FormatRegistry.Probe(file).Family != FormatFamily.Archive) continue;
                var archive = await resolver.OpenCachedAsync(file, token).ConfigureAwait(false);
                foreach (var asset in archive.Assets.Where(a => IsPickupResource(a.Name))) found.TryAdd(asset.Name, (archive, asset));
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
        return await Task.Run(() => Create(resources, notes, token), token).ConfigureAwait(false);
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
                        var (rotation, _) = ReadVector(row[3], doc, asset);
                        _ = ReadNumber(row[4]);
                        var source = new MissionPickupSource(group.Key.Item1, asset.Index, asset.Name.ToUpperInvariant(), index);
                        var record = new PickupPlacementRecord(source, type, position, rotation, group.Select(r => r.Difficulty).Distinct().Order().ToArray());
                        result.entries.Add(source, new(record, offsets)); result.positions.Add(source, position); result.savedPositions.Add(source, position);
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
            string text = components[i].Text("offset");
            if (!text.StartsWith("0x", StringComparison.Ordinal) || !int.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int offset) || offset < 0 || offset > asset.Length - 8)
                throw new InvalidDataException("Invalid coordinate source range.");
            offsets[i] = checked((int)asset.Offset + offset);
            _ = doc.Slice(offsets[i], 8);
        }
        return (new(values[0], values[1], values[2]), offsets);
    }
    private static float ReadNumber(JsonNode? value)
    {
        if (value.Text("type") is not ("int" or "float") || !float.TryParse(value?["value"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float number) || !float.IsFinite(number))
            throw new InvalidDataException("Expected a finite numeric coordinate.");
        return number;
    }
    public PickupPlacementRecord? Find(MissionPickupSource source) => entries.GetValueOrDefault(source)?.Record;
    public Vector3 Position(MissionPickupSource source) => positions[source];
    public PickupPlacementScope Scope(MissionPickupSource source)
    {
        var selected = entries[source].Record;
        var same = entries.Values.Select(e => e.Record).Where(r => r.Type == selected.Type && r.OriginalPosition == selected.OriginalPosition && r.Rotation == selected.Rotation).ToArray();
        var groups = same.GroupBy(r => (r.Source.ArchivePath, r.Source.AssetIndex)).ToArray();
        bool uniqueSource = groups.Single(g => g.Key == (source.ArchivePath, source.AssetIndex)).Count() == 1;
        var matches = uniqueSource ? groups.Where(g => g.Count() == 1).Select(g => g.Single()).ToArray() : [selected];
        var affected = matches.SelectMany(r => r.Difficulties).Distinct().Order().ToArray();
        var skipped = Enum.GetValues<MissionDifficulty>().Except(affected).ToArray();
        string description = "Applies to: " + string.Join(", ", affected);
        if (skipped.Length > 0) description += ". Unmatched or ambiguous (unchanged): " + string.Join(", ", skipped);
        return new(matches.Select(r => r.Source).ToArray(), description);
    }
    public bool MoveTo(MissionPickupSource source, Vector3 position)
    {
        if (saving) throw new InvalidOperationException("Wait for the current save to finish.");
        RequireFinite(position);
        Vector3 delta = position - positions[source]; RequireFinite(delta);
        if (delta == Vector3.Zero) return false;
        var before = Scope(source).Sources.ToDictionary(s => s, s => positions[s]);
        var after = before.ToDictionary(p => p.Key, p => p.Key == source ? position : p.Value + delta);
        foreach (var value in after.Values) RequireFinite(value);
        undo.Push(new(before, after)); redo.Clear(); Apply(after); return true;
    }
    public void Undo() { if (CanUndo) { var move = undo.Pop(); redo.Push(move); Apply(move.Before); } }
    public void Redo() { if (CanRedo) { var move = redo.Pop(); undo.Push(move); Apply(move.After); } }
    private void Apply(Dictionary<MissionPickupSource, Vector3> values) { foreach (var p in values) positions[p.Key] = p.Value; Changed?.Invoke(); }
    private static void RequireFinite(Vector3 value)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z)) throw new InvalidDataException("Position must contain finite game-unit coordinates.");
    }
    public byte[] EncodeArchive(string archivePath)
    {
        var archive = archives[archivePath]; byte[] output = archive.Original.Bytes.ToArray();
        foreach (var entry in entries.Values.Where(e => e.Record.Source.ArchivePath.Equals(archivePath, StringComparison.OrdinalIgnoreCase)))
        {
            var p = positions[entry.Record.Source]; var original = entry.Record.OriginalPosition;
            for (int axis = 0; axis < 3; axis++)
            {
                if (p[axis] == original[axis]) continue;
                int offset = entry.Offsets[axis];
                BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(offset, 4), 2);
                BinaryPrimitives.WriteSingleLittleEndian(output.AsSpan(offset + 4, 4), p[axis]);
            }
        }
        return output;
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
            try { if (FileStamp.Read(archive.Target) != archive.Stamp) return true; }
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
