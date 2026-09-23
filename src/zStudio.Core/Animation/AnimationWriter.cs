using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Recoil.Zbd.Core.Animation;

public static class AnimationWriter
{
    public static byte[] Write(AnimationPackage package, CancellationToken token = default)
    {
        if (package.Entries.Count > ushort.MaxValue) throw new InvalidDataException("Animation entry count exceeds 65535.");
        using MemoryStream output = new(); byte[] prefix = (byte[])package.Prefix.Clone();
        int counts = prefix.Length - 60 + 8; uint original = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(counts));
        BinaryPrimitives.WriteUInt32LittleEndian(prefix.AsSpan(counts), (original & 65535) | ((uint)package.Entries.Count << 16)); output.Write(prefix);
        foreach (var entry in package.Entries) { token.ThrowIfCancellationRequested(); output.Write(WriteEntry(entry)); }
        output.Write(package.Tail); return output.ToArray();
    }
    internal static byte[] WriteEntry(AnimationEntry entry)
    {
        if (entry.Sequences.Count > 255 || entry.References.Any(t => t.Count > 255)) throw new InvalidDataException("Sequence/reference counts are limited to 255.");
        using MemoryStream output = new(); byte[] header = (byte[])entry.Bytes.Clone(); header[260] = (byte)entry.Sequences.Count;
        for (int i = 0; i < 8; i++) header[260 + AnimationPackage.ReferenceLanes[i]] = (byte)entry.References[i].Count;
        byte[] primary = SequenceBytes(entry.Primary);
        // The loader overwrites the inline copy. Preserve it on a no-op save, but
        // update both copies when the canonical header or payload length changes.
        if (!primary.AsSpan(0, 64).SequenceEqual(entry.OriginalPrimaryHeader)) primary.AsSpan(0, 64).CopyTo(header.AsSpan(196));
        output.Write(header);
        for (int table = 0; table < 8; table++) foreach (var reference in entry.References[table])
        {
            if (reference.Bytes.Length != AnimationPackage.ReferenceSizes[table]) throw new InvalidDataException("Invalid reference record size.");
            output.Write(reference.Bytes);
        }
        output.Write(primary); foreach (var sequence in entry.Sequences) output.Write(SequenceBytes(sequence)); return output.ToArray();
    }
    private static byte[] SequenceBytes(AnimationSequence sequence)
    {
        using MemoryStream payload = new();
        foreach (var ev in sequence.Events)
        {
            if (ev.Bytes.Length < 12) throw new InvalidDataException("An event header requires 12 bytes.");
            byte[] bytes = (byte[])ev.Bytes.Clone(); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length); payload.Write(bytes);
        }
        payload.Write(sequence.OpaqueTail); byte[] header = (byte[])sequence.Bytes.Clone(); BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(60), checked((int)payload.Length));
        using MemoryStream result = new(); result.Write(header); payload.Position = 0; payload.CopyTo(result); return result.ToArray();
    }
    public static async Task SaveAsAsync(AnimationPackage package, string path, string source, string protectedRoot, CancellationToken token = default)
    {
        path = Path.GetFullPath(path); source = Path.GetFullPath(source); protectedRoot = Path.GetFullPath(protectedRoot);
        if (path.Equals(source, StringComparison.OrdinalIgnoreCase) || IsInside(path, protectedRoot) || path.Split(Path.DirectorySeparatorChar).Any(p => p.Equals("zbd_1998", StringComparison.OrdinalIgnoreCase) || p.Equals("zbd_1999", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Save the edited animation to a new file outside the source dataset.");
        if (File.Exists(path)) throw new IOException("Choose a new filename; Save As does not replace existing files.");
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(path)!); parent != null; parent = parent.Parent)
            if (parent.Exists && parent.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Save As through directory links is not supported; choose a direct destination.");
        byte[] bytes = await Task.Run(() => Write(package, token), token).ConfigureAwait(false);
        var reopened = AnimationPackage.Read(bytes, token);
        if (!bytes.AsSpan().SequenceEqual(Write(reopened, token))) throw new InvalidDataException("Animation save failed its round-trip check.");
        string directory = Path.GetDirectoryName(path)!; Directory.CreateDirectory(directory); string temporary = Path.Combine(directory, ".animation-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            { await stream.WriteAsync(bytes, token).ConfigureAwait(false); await stream.FlushAsync(token).ConfigureAwait(false); }
            byte[] check = await File.ReadAllBytesAsync(temporary, token).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), SHA256.HashData(check))) throw new IOException("The written animation did not pass verification.");
            token.ThrowIfCancellationRequested(); File.Move(temporary, path, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static bool IsInside(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Entry snapshots keep undo independent of renderer runtime state.</summary>
public sealed class AnimationEditSession(AnimationPackage package)
{
    public AnimationPackage Package { get; } = package;
    private readonly Stack<Edit> undo = [], redo = [];
    private long revision, nextRevision, savedRevision;
    public bool IsDirty => revision != savedRevision;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public event Action? Changed;
    public void Apply(int index, string description, Action<AnimationEntry> change)
    {
        var before = Package.Entries[index]; var after = before.Clone(); change(after);
        if (AnimationWriter.WriteEntry(before).AsSpan().SequenceEqual(AnimationWriter.WriteEntry(after))) return;
        long next = ++nextRevision; undo.Push(new(index, description, before, after, revision, next)); redo.Clear();
        if (undo.Count > 128) { var retained = undo.Take(128).Reverse().ToArray(); undo.Clear(); foreach (var item in retained) undo.Push(item); }
        Package.Entries[index] = after; revision = next; Changed?.Invoke();
    }
    public void Undo() { if (!undo.TryPop(out var e)) return; Package.Entries[e.Index] = e.Before; revision = e.BeforeRevision; redo.Push(e); Changed?.Invoke(); }
    public void Redo() { if (!redo.TryPop(out var e)) return; Package.Entries[e.Index] = e.After; revision = e.AfterRevision; undo.Push(e); Changed?.Invoke(); }
    public void MarkSaved() { savedRevision = revision; Changed?.Invoke(); }
    public void EditField(int entry, Guid sequence, Guid ev, AnimationField field, string value) => Apply(entry, "Edit " + field.Name, e =>
    {
        var s = FindSequence(e, sequence); EnsureEditable(s); var record = s.Events.Single(v => v.Id == ev); field.Write(record, value);
        if (record.Spec?.DurationOffset == field.Offset && record.F32(field.Offset) < 0) throw new InvalidDataException("Event duration cannot be negative.");
        if (record.Type == 13 && field.Offset == 16 || record.Type == 14 && field.Offset is 20 or 24)
            if (record.F32(field.Offset) is < 0 or > 1) throw new InvalidDataException("Opacity must be between zero and one.");
        if (field.ReferenceTable >= 0)
        {
            int index = field.Kind == AnimationFieldKind.Short ? record.I16(field.Offset) : record.I32(field.Offset);
            if (index >= e.References[field.ReferenceTable].Count && index > 0) throw new InvalidDataException("The selected reference is outside this animation's table.");
        }
        if (field.Kind == AnimationFieldKind.Text && record.Type is 22 or 23 or 25 or 26 or 27) record.SetInt(44, -1);
        if (field.Kind == AnimationFieldKind.Text && record.Type is 19 or 24) record.SetShort(48, -1);
        if (field.Kind == AnimationFieldKind.Text && record.Type == 10 && field.Offset == 208) record.SetShort(240, -1);
    });
    public static AnimationSequence FindSequence(AnimationEntry entry, Guid id) => entry.AllSequences.Single(s => s.Id == id);
    public static void EnsureEditable(AnimationSequence sequence) { if (!sequence.IsEditable) throw new InvalidDataException("Malformed sequence bytes are preserved. This sequence is read-only."); }
    public void RenameSequence(int entry, Guid id, string name) => Apply(entry, "Rename sequence", e =>
    {
        EnsureStructuralEdit(e); var sequence = FindSequence(e, id); string old = sequence.Name;
        if (e.AllSequences.Any(s => s.Id != id && s.Name == name)) throw new InvalidDataException("Another sequence already uses that name.");
        InvalidateSequenceCaches(e); sequence.Name = name;
        foreach (var reference in SequenceReferences(e).Where(r => r.Event.Text(r.Name) == old))
        { reference.Event.SetText(reference.Name, name); SetCache(reference, -1); }
    });
    public void AddSequence(int entry, Guid? duplicate = null) => Apply(entry, "Add sequence", e =>
    {
        EnsureStructuralEdit(e); var sequence = duplicate is Guid id ? FindSequence(e, id).Clone(true) : new AnimationSequence(new byte[64]);
        string basis = duplicate == null ? "sequence" : sequence.Name[..Math.Min(sequence.Name.Length, 22)] + "_copy";
        int suffix = 1; string name = basis; while (e.AllSequences.Any(s => s.Name == name)) name = basis + suffix++;
        if (duplicate != null)
        {
            string old = sequence.Name;
            foreach (var ev in sequence.Events)
            {
                var reference = SequenceReference(ev);
                if (reference is { } r && (ev.Text(r.Name) == old || GetCache(r) == e.Sequences.FindIndex(s => s.Id == duplicate))) { ev.SetText(r.Name, name); SetCache(r, -1); }
            }
        }
        sequence.Name = name; e.Sequences.Add(sequence);
    });
    public void MoveSequence(int entry, Guid id, int direction) => Apply(entry, "Move sequence", e =>
    {
        EnsureStructuralEdit(e); int at = e.Sequences.FindIndex(s => s.Id == id); int to = at + direction;
        if (at < 0 || to < 0 || to >= e.Sequences.Count) return;
        InvalidateSequenceCaches(e); (e.Sequences[at], e.Sequences[to]) = (e.Sequences[to], e.Sequences[at]);
    });
    public void DeleteSequence(int entry, Guid id) => Apply(entry, "Delete sequence", e =>
    {
        EnsureStructuralEdit(e); var sequence = FindSequence(e, id);
        if (sequence == e.Primary) throw new InvalidDataException("The reset/stop slot cannot be deleted; edit its events instead.");
        int at = e.Sequences.IndexOf(sequence);
        if (SequenceReferences(e).Where(r => !sequence.Events.Contains(r.Event)).Any(r => r.Event.Text(r.Name) == sequence.Name || GetCache(r) == at))
            throw new InvalidDataException("Other events reference this sequence. Retarget or delete those events first.");
        InvalidateSequenceCaches(e); e.Sequences.Remove(sequence);
    });
    private static void InvalidateSequenceCaches(AnimationEntry entry)
    {
        foreach (var reference in SequenceReferences(entry))
        {
            int index = GetCache(reference);
            if (index >= 0 && index < entry.Sequences.Count) reference.Event.SetText(reference.Name, entry.Sequences[index].Name);
            SetCache(reference, -1);
        }
    }
    private static IEnumerable<(AnimationEvent Event, int Name, int Cache, bool Narrow)> SequenceReferences(AnimationEntry entry) => entry.AllSequences.SelectMany(s => s.Events).Select(SequenceReference).Where(r => r.HasValue).Select(r => r!.Value);
    private static (AnimationEvent Event, int Name, int Cache, bool Narrow)? SequenceReference(AnimationEvent ev) => ev.Type switch
    {
        22 or 23 when ev.Bytes.Length >= 48 => (ev,12,44,false),
        10 when ev.Bytes.Length >= 252 && (ev.U32(12) & 0x800) != 0 => (ev,208,240,true),
        _ => null
    };
    private static int GetCache((AnimationEvent Event, int Name, int Cache, bool Narrow) r) => r.Narrow ? r.Event.I16(r.Cache) : r.Event.I32(r.Cache);
    private static void SetCache((AnimationEvent Event, int Name, int Cache, bool Narrow) r, int value) { if (r.Narrow) r.Event.SetShort(r.Cache, checked((short)value)); else r.Event.SetInt(r.Cache,value); }
    private static void EnsureStructuralEdit(AnimationEntry entry)
    {
        if (entry.AllSequences.Any(s => !s.IsEditable || s.Events.Any(e => e.Spec == null || e.Bytes.Length < e.Spec.Size)))
            throw new InvalidDataException("Sequence structure cannot be changed while opaque or malformed events may contain unknown references.");
    }
    private sealed record Edit(int Index, string Description, AnimationEntry Before, AnimationEntry After, long BeforeRevision, long AfterRevision);
}
