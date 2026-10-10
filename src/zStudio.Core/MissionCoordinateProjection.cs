using System.Runtime.CompilerServices;

namespace Recoil.Zbd.Core;

/// <summary>An operation-local view: compare complete source prefixes once, then look up integer record offsets.</summary>
internal sealed class MissionCoordinateProjection<T>
{
    private sealed class MemberReferences : IEqualityComparer<(int Index, string Name)>
    {
        public bool Equals((int Index, string Name) x, (int Index, string Name) y) => x.Index == y.Index && ReferenceEquals(x.Name, y.Name);
        public int GetHashCode((int Index, string Name) value) => HashCode.Combine(value.Index, RuntimeHelpers.GetHashCode(value.Name));
    }
    private sealed class Archive
    {
        internal readonly Dictionary<(int Index, string Name), Dictionary<int, T>> Members = [];
        internal readonly Dictionary<(int Index, string Name), Dictionary<int, T>?> References = new(new MemberReferences());
    }
    private readonly Dictionary<string, Archive> archives = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Archive?> references = new(ReferenceEqualityComparer.Instance);
    internal int ArchiveValueLookups { get; private set; }
    internal int MemberValueLookups { get; private set; }

    internal MissionCoordinateProjection(IEnumerable<KeyValuePair<MissionPickupSource, T>> values)
    {
        foreach (var (source, value) in values)
            Member(Source(source.ArchivePath, create: true)!, source.AssetIndex, source.ResourceName, create: true)![source.RecordIndex] = value;
    }
    internal IReadOnlyDictionary<int, T>? Find(string archive, int memberIndex, string member)
        => Source(archive, create: false) is { } source ? Member(source, memberIndex, member, create: false) : null;

    private Archive? Source(string path, bool create)
    {
        if (references.TryGetValue(path, out var cached)) return cached;
        ArchiveValueLookups++;
        if (!archives.TryGetValue(path, out var found) && create) archives.Add(path, found = new());
        references.Add(path, found); return found;
    }
    private Dictionary<int, T>? Member(Archive archive, int index, string name, bool create)
    {
        var key = (index, name);
        if (archive.References.TryGetValue(key, out var cached)) return cached;
        MemberValueLookups++;
        if (!archive.Members.TryGetValue(key, out var found) && create) archive.Members.Add(key, found = []);
        archive.References.Add(key, found); return found;
    }
}
