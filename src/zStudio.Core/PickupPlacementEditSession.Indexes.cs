using System.Numerics;

namespace Recoil.Zbd.Core;

public sealed partial class PickupPlacementEditSession
{
    private readonly MissionSourceComparer sourceComparer = new();
    private readonly object indexGate = new();
    /// <summary>Exact full source identity, sharing this session's bounded-lifetime prefix memoization.</summary>
    public IEqualityComparer<MissionPickupSource> SourceComparer => sourceComparer;
    private Dictionary<ArchiveState, List<Entry>>? archiveEntries;
    private IReadOnlyList<PickupPlacementRecord>? pickupRecords;
    private Dictionary<MissionPickupSource, PickupPlacementScope>? scopes;
    internal long ScopeIndexRows { get; private set; }
    internal long IdentityPrefixResolutions => sourceComparer.PrefixResolutions;

    public PickupPlacementEditSession()
    {
        entries = new(sourceComparer); otherCoordinates = new(sourceComparer);
        positions = new(sourceComparer); savedPositions = new(sourceComparer);
        rotations = new(sourceComparer); savedRotations = new(sourceComparer);
    }
    private void InvalidateMembership()
    { lock (indexGate) { archiveEntries = null; pickupRecords = null; scopes = null; } }
    private void InvalidateScopes() { lock (indexGate) scopes = null; }
    private IReadOnlyList<PickupPlacementRecord> IndexedRecords()
    {
        lock (indexGate) return pickupRecords ??= Array.AsReadOnly(entries.Values.Where(e => !otherCoordinates.ContainsKey(e.Record.Source)).Select(e => e.Record).ToArray());
    }

    private IEnumerable<Entry> EntriesForArchive(string path, bool exact = false)
    {
        lock (indexGate)
        {
            if (archiveEntries == null)
            {
                Dictionary<ArchiveState, List<Entry>> built = [];
                Dictionary<string, ArchiveState> resolved = new(ReferenceEqualityComparer.Instance);
                foreach (var entry in entries.Values)
                {
                    string prefix = entry.Record.Source.ArchivePath;
                    if (!resolved.TryGetValue(prefix, out var archive)) resolved.Add(prefix, archive = archives[prefix]);
                    if (!built.TryGetValue(archive, out var rows)) built.Add(archive, rows = []);
                    rows.Add(entry);
                }
                archiveEntries = built;
            }
            if (!archiveEntries.TryGetValue(archives[path], out var found)) return [];
            return exact ? found.Where(e => sourceComparer.SameArchive(e.Record.Source.ArchivePath, path)) : found;
        }
    }

    private PickupPlacementScope IndexedScope(MissionPickupSource source)
    {
        lock (indexGate)
        {
            if (scopes == null) BuildScopes();
            return scopes![source];
        }
    }
    private void BuildScopes()
    {
        Dictionary<MissionPickupSource, PickupPlacementScope> result = new(sourceComparer);
        Dictionary<(string Type, Vector3 Position, Vector3 Rotation), List<PickupPlacementRecord>> pickups = [];
        foreach (var record in Records)
        {
            ScopeIndexRows++;
            var key = (record.Type, record.OriginalPosition, record.Rotation);
            if (!pickups.TryGetValue(key, out var group)) pickups.Add(key, group = []);
            group.Add(record);
        }
        foreach (var group in pickups.Values) Add(group.Select(r => (r.Source, r.Difficulties)));

        Dictionary<(string Template, int? Node, Vector3 Position, Vector3 Rotation), List<MissionCoordinateRecord>> tanks = [];
        foreach (var record in otherCoordinates.Values)
        {
            ScopeIndexRows++;
            string? single = record.Kind == "ai" ? "This AI network node" :
                record.MissionSpecific ? "This authored mission actor only" :
                record.Difficulties.Count == 0 ? "Read-only: shadowed tank resource" :
                record.TemplateSourceNode == null ? "Read-only: missing or ambiguous tank template" : null;
            if (single != null) result.Add(record.Source, new(Array.AsReadOnly(new[] { record.Source }), single));
            // A mission-specific selected row is a singleton, but the original counterpart search still
            // included it when an ordinary tank from a mixed public session was selected.
            if (record.Kind != "tank" || record.Difficulties.Count == 0 || record.TemplateSourceNode == null) continue;
            var key = (record.Template, record.TemplateSourceNode, record.OriginalPosition, record.Rotation);
            if (!tanks.TryGetValue(key, out var group)) tanks.Add(key, group = []);
            group.Add(record);
        }
        foreach (var group in tanks.Values) Add(group.Select(r => (r.Source, r.Difficulties)));
        scopes = result;

        void Add(IEnumerable<(MissionPickupSource Source, IReadOnlyList<MissionDifficulty> Difficulties)> records)
        {
            Dictionary<(MissionSourceComparer.Prefix Archive, int Asset), List<(MissionPickupSource Source, IReadOnlyList<MissionDifficulty> Difficulties)>> groups = [];
            foreach (var record in records)
            {
                var key = (sourceComparer.ArchiveIdentity(record.Source), record.Source.AssetIndex);
                if (!groups.TryGetValue(key, out var group)) groups.Add(key, group = []);
                group.Add(record);
            }
            var unique = groups.Values.Where(g => g.Count == 1).Select(g => g[0]).ToArray();
            PickupPlacementScope? shared = unique.Length == 0 ? null : Make(unique);
            foreach (var group in groups.Values)
                foreach (var record in group)
                    if (!result.ContainsKey(record.Source)) result.Add(record.Source, group.Count == 1 ? shared! : Make([record]));
        }
        static PickupPlacementScope Make((MissionPickupSource Source, IReadOnlyList<MissionDifficulty> Difficulties)[] rows)
        {
            var affected = rows.SelectMany(r => r.Difficulties).Distinct().Order().ToArray();
            var skipped = Enum.GetValues<MissionDifficulty>().Except(affected).ToArray();
            string description = "Applies to: " + string.Join(", ", affected);
            if (skipped.Length > 0) description += ". Unmatched or ambiguous (unchanged): " + string.Join(", ", skipped);
            return new(Array.AsReadOnly(rows.Select(r => r.Source).ToArray()), description);
        }
    }
}
