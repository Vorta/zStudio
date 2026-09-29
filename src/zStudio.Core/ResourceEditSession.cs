using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Export;

namespace Recoil.Zbd.Core;

public sealed record ResourceMember(Guid Id, int? SourceIndex, string Name, ReadOnlyMemory<byte> Data, ReadOnlyMemory<byte> DirectoryRecord, ZrdNode? Tree = null);
public sealed record ResourceSnapshot(IReadOnlyList<ResourceMember> Members, ZbdDocument Document, string Hash);
public sealed record PreparedResourceEdit(ResourceSnapshot Before, ResourceSnapshot After);

/// <summary>One archive (including embedded ZRD trees) or one standalone ZRD, with immutable history.</summary>
public sealed class ResourceEditSession
{
    private readonly ZbdDocument source;
    private readonly List<ResourceSnapshot> undo = [], redo = [];
    private readonly ConcurrentDictionary<(Guid, ReadOnlyMemory<byte>), ZrdNode> trees = new();
    private ResourceSnapshot saved;
    private bool saving;
    public ResourceSnapshot Current { get; private set; }
    public string TargetPath { get; private set; }
    public FileStamp TargetStamp { get; private set; }
    public bool IsArchive => source.Probe.Family == FormatFamily.Archive;
    public bool IsDirty => Current.Hash != saved.Hash;
    public bool CanUndo => !saving && undo.Count != 0;
    public bool CanRedo => !saving && redo.Count != 0;
    public bool HasHistory => undo.Count != 0 || redo.Count != 0;
    public event Action? Changed;
    public event Action? BeforeEdit;
    public ResourceEditSession(ZbdDocument document)
    {
        if (document.Probe.Family is not (FormatFamily.Archive or FormatFamily.Zrd) || document.Diagnostics.Any(d => d.Severity == "Error")) throw new InvalidDataException("An intact ZAR archive or standalone ZRD is required.");
        source = document; TargetPath = document.Path; TargetStamp = document.Stamp;
        var standalone = IsArchive ? null : (document.Assets.SingleOrDefault()?.Content as ZrdNode ?? ZrdDecoder.Read(document.Bytes));
        var members = document.Assets.Select(a => new ResourceMember(Guid.NewGuid(), a.Index, a.Name, document.Slice(a.Offset, a.Length), IsArchive ? document.Slice(document.ArchiveDirectoryOffset!.Value + a.Index * 148L, 148) : ReadOnlyMemory<byte>.Empty, standalone ?? a.Content as ZrdNode)).ToArray();
        Current = saved = new(members, document, Hash(document.Bytes));
    }
    public ResourceMember Member(Guid id) => Current.Members.SingleOrDefault(m => m.Id == id) ?? throw new InvalidDataException("The archive member no longer exists.");
    /// <summary>Resolve identities for the exact decoded snapshot, including a load overtaken by an edit or Undo/Redo.</summary>
    public ResourceSnapshot? SnapshotFor(ZbdDocument document) => ReferenceEquals(Current.Document, document) ? Current :
        undo.Concat(redo).Append(saved).FirstOrDefault(s => ReferenceEquals(s.Document, document));
    public Task<PreparedResourceEdit> PrepareMechModelAsync(Guid member, int localModel, ImportedMesh mesh, int material, CancellationToken token = default)
    {
        var before = Current;
        return Task.Run(() =>
        {
            var list = before.Members.ToList(); int index = list.FindIndex(m => m.Id == member);
            if (index < 0) throw new InvalidDataException("The mech member no longer exists.");
            byte[] bytes = ModelReplacementWriter.ReplaceMechMember(before.Document, index, localModel, mesh, material, token);
            list[index] = list[index] with { Data = bytes, Tree = null };
            var after = Build(list, token);
            if (after.Document.Assets[index].Content is not MechAssembly assembly || !after.Document.Scene!.Models[assembly.FirstModel + localModel].Vertices.SequenceEqual(mesh.Positions))
                throw new InvalidDataException("Mech replacement failed shared-reader verification.");
            for (int i = 0; i < list.Count; i++) if (i != index && !after.Document.Slice(after.Document.Assets[i].Offset, after.Document.Assets[i].Length).Span.SequenceEqual(before.Members[i].Data.Span))
                throw new InvalidDataException("Mech replacement changed an unrelated archive member.");
            return new PreparedResourceEdit(before, after);
        }, token);
    }
    public Task<PreparedResourceEdit> PrepareMotionAsync(Guid member, string action, int part = -1, int frame = -1, MotionFrame? value = null, float? loopTime = null, CancellationToken token = default)
    {
        var before = Current;
        return Task.Run(() =>
        {
            var list = before.Members.ToList(); int index = list.FindIndex(m => m.Id == member);
            if (index < 0) throw new InvalidDataException("The motion member no longer exists.");
            var clip = MotionClip.Read(list[index].Data, token).Edit(action, part, frame, value, loopTime);
            byte[] bytes = clip.Write(token); _ = MotionClip.Read(bytes, token);
            list[index] = list[index] with { Data = bytes, Tree = null };
            return new PreparedResourceEdit(before, Build(list, token));
        }, token);
    }
    public ZrdNode Tree(ResourceMember member, CancellationToken token = default) => member.Tree ?? trees.GetOrAdd((member.Id, member.Data), _ => ZrdDecoder.Read(member.Data, token));
    public AssetRecord? OriginalAsset(ResourceMember member) => member.SourceIndex is int i ? source.Assets.Single(a => a.Index == i) : null;
    public async Task<PreparedResourceEdit> PrepareArchiveAsync(string action, Guid member, string name = "", string? path = null, int position = -1, CancellationToken token = default)
    {
        if (!IsArchive) throw new InvalidDataException("Member operations require a ZAR archive.");
        var before = Current; byte[]? imported = null;
        if (action is "add" or "replace")
        {
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("Choose an input file.");
            await using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > FormatRegistry.MaximumDocumentBytes) throw new InvalidDataException("Input exceeds 512 MiB.");
            imported = new byte[checked((int)input.Length)]; await input.ReadExactlyAsync(imported, token);
        }
        return await Task.Run(() =>
        {
            var list = before.Members.ToList(); int index = list.FindIndex(m => m.Id == member);
            if (action is not ("add" or "add_zrd") && index < 0) throw new InvalidDataException("The archive member no longer exists.");
            if (action is "add" or "add_zrd" or "rename") ValidateName(name);
            switch (action)
            {
                case "add": list.Add(new(Guid.NewGuid(), null, name, imported!, new byte[148])); break;
                case "add_zrd":
                    if (!Path.GetExtension(name).Equals(".zrd", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("A ZRD resource name must end in .zrd.");
                    var tree = ZrdNode.Create(ZrdKind.Array); list.Add(new(Guid.NewGuid(), null, name, ZrdWriter.Write(tree, token), new byte[148], tree)); break;
                case "replace": list[index] = list[index] with { Data = imported!, Tree = null }; break;
                case "rename": list[index] = list[index] with { Name = name }; break;
                case "delete": list.RemoveAt(index); break;
                case "duplicate":
                    ValidateName(name); list.Insert(index + 1, list[index] with { Id = Guid.NewGuid(), SourceIndex = null, Name = name, Tree = list[index].Tree?.Duplicate() }); break;
                case "move":
                    if (position < 0 || position >= list.Count) throw new InvalidDataException("Destination index is outside the archive.");
                    var item = list[index]; list.RemoveAt(index); list.Insert(position, item); break;
                default: throw new InvalidDataException("Unknown archive action.");
            }
            if (action is "add" or "replace")
            {
                var item = action == "add" ? list[^1] : list[index];
                var probe = FormatRegistry.Probe(item.Data.Span[..Math.Min(36, item.Data.Length)], item.Data.Span[Math.Max(0, item.Data.Length - 8)..], item.Data.Length, Path.GetExtension(item.Name));
                var tree = probe.Family == FormatFamily.Zrd || Path.GetExtension(item.Name).Equals(".zrd", StringComparison.OrdinalIgnoreCase)
                    ? ZrdDecoder.Read(item.Data, token) : ZrdDecoder.TryRead(item.Data, token);
                if (tree != null)
                    list[action == "add" ? list.Count - 1 : index] = item with { Tree = tree.Duplicate() }; // Imported offsets are not original archive ranges.
                if (probe.Family == FormatFamily.Wave || Path.GetExtension(item.Name).Equals(".wav", StringComparison.OrdinalIgnoreCase)) _ = WaveDecoder.Read(item.Data, token);
            }
            return new PreparedResourceEdit(before, Build(list, token));
        }, token);
    }
    public Task<PreparedResourceEdit> PrepareZrdAsync(Guid member, Guid node, string action, ZrdKind kind = ZrdKind.String, string value = "", Guid parent = default, int position = -1, CancellationToken token = default)
    {
        var before = Current;
        return Task.Run(() =>
        {
            var list = before.Members.ToList(); int index = list.FindIndex(m => m.Id == member);
            if (index < 0) throw new InvalidDataException("The archive member no longer exists.");
            var root = Tree(list[index], token); var selected = root.Find(node) ?? throw new InvalidDataException("The ZRD node no longer exists.");
            ZrdNode Change(ZrdNode n, Guid id, Func<ZrdNode, ZrdNode> edit) => n.Id == id ? edit(n) : n with { Children = n.Children.Select(c => Change(c, id, edit)).ToArray() };
            ZrdNode Remove(ZrdNode n, Guid id) => n with { Children = n.Children.Where(c => c.Id != id).Select(c => Remove(c, id)).ToArray() };
            ZrdNode Insert(ZrdNode n, Guid container, ZrdNode child, int target)
            {
                var array = n.Find(container) ?? throw new InvalidDataException("The destination array no longer exists.");
                if (array.Kind != ZrdKind.Array || target < 0 || target > array.Children.Count) throw new InvalidDataException("Choose an array and a valid insertion index.");
                return Change(n, container, a => { var children = a.Children.ToList(); children.Insert(target, child); return a with { Children = children.ToArray() }; });
            }
            switch (action)
            {
                case "set":
                    if (selected.Kind == ZrdKind.Array) throw new InvalidDataException("Use structural operations to edit array children, or change type to replace the complete array.");
                    root = Change(root, node, n => ZrdNode.Set(n, n.Kind, value)); break;
                case "type": root = Change(root, node, n => ZrdNode.Set(n with { Children = [] }, kind, value)); break;
                case "add": root = Insert(root, node, ZrdNode.Create(kind, value), position < 0 ? selected.Children.Count : position); break;
                case "delete": if (node == root.Id) throw new InvalidDataException("The root cannot be deleted; change its type instead."); root = Remove(root, node); break;
                case "duplicate":
                    var owner = FindParent(root, node) ?? throw new InvalidDataException("The root cannot be duplicated.");
                    root = Insert(root, owner.Id, selected.Duplicate(), owner.Children.ToList().FindIndex(c => c.Id == node) + 1); break;
                case "move":
                    if (node == root.Id || selected.Find(parent) != null) throw new InvalidDataException("A node cannot move into itself or its descendants.");
                    root = Insert(Remove(root, node), parent, selected, position); break;
                default: throw new InvalidDataException("Unknown ZRD action.");
            }
            var bytes = ZrdWriter.Write(root, token); _ = ZrdDecoder.Read(bytes, token);
            list[index] = list[index] with { Data = bytes, Tree = root };
            return new PreparedResourceEdit(before, Build(list, token));
        }, token);
    }
    public static ZrdNode? FindParent(ZrdNode root, Guid id) => root.Children.Any(c => c.Id == id) ? root : root.Children.Select(c => FindParent(c, id)).FirstOrDefault(n => n != null);
    public Task<PreparedResourceEdit> PrepareValveAsync(Guid member, AiValveEdit edit, CancellationToken token = default)
    {
        var before = Current;
        return Task.Run(() =>
        {
            var list = before.Members.ToList(); int index = list.FindIndex(m => m.Id == member);
            if (index < 0) throw new InvalidDataException("The valve resource no longer exists.");
            var item = list[index]; var root = MissionAiValves.Edit(item.Name, Tree(item, token), edit, token);
            byte[] bytes = ZrdWriter.Write(root, token); _ = ZrdDecoder.Read(bytes, token);
            list[index] = item with { Data = bytes, Tree = root };
            return new PreparedResourceEdit(before, Build(list, token));
        }, token);
    }
    private ResourceSnapshot Build(IReadOnlyList<ResourceMember> members, CancellationToken token)
    {
        byte[] bytes = IsArchive ? ArchiveWriter.Write(source, members, token) : members.Single().Data.ToArray();
        var document = FormatRegistry.Default.OpenBytes(source.Path, bytes, source.Stamp, token);
        if (document.Probe.Family != source.Probe.Family || document.Diagnostics.Any(d => d.Severity == "Error") || document.Assets.Count != members.Count) throw new InvalidDataException("Resource edit failed shared-reader verification. Its contents must remain an unambiguous ZAR/ZRD file.");
        return new(members.ToArray(), document, Hash(bytes));
    }
    public void Accept(PreparedResourceEdit edit)
    {
        if (saving || !ReferenceEquals(edit.Before, Current)) throw new InvalidOperationException("The resource changed while preparing the edit.");
        if (edit.After.Hash == Current.Hash) return;
        BeforeEdit?.Invoke(); undo.Add(Current); Trim(undo); redo.Clear(); Current = edit.After; PruneTrees(); Changed?.Invoke();
    }
    private void PruneTrees()
    {
        var retained = undo.Concat(redo).Append(Current).Append(saved).SelectMany(s => s.Members).Where(m => m.Tree == null).Select(m => (m.Id, m.Data)).ToHashSet();
        foreach (var key in trees.Keys) if (!retained.Contains(key)) trees.TryRemove(key, out _);
    }
    private static void Trim(List<ResourceSnapshot> history)
    {
        long bytes = history.Sum(s => (long)s.Document.Bytes.Length);
        while (history.Count > 1 && (history.Count > 128 || bytes > 256L * 1024 * 1024)) { bytes -= history[0].Document.Bytes.Length; history.RemoveAt(0); }
    }
    public void UndoRedo(bool forward)
    {
        if (saving) throw new InvalidOperationException("A resource save is in progress.");
        var from = forward ? redo : undo; var to = forward ? undo : redo;
        if (from.Count == 0) return;
        BeforeEdit?.Invoke(); to.Add(Current); Trim(to); Current = from[^1]; from.RemoveAt(from.Count - 1); PruneTrees(); Changed?.Invoke();
    }
    public async Task<string> SaveAsync(string? destination = null, CancellationToken token = default)
    {
        if (saving) throw new InvalidOperationException("A resource save is in progress.");
        string target = Path.GetFullPath(destination ?? TargetPath), temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp"; saving = true;
        try
        {
            VerifiedDocumentSave.ValidateDestination(target);
            if (destination == null) await CheckBaseline(token); else if (File.Exists(target)) throw new IOException("Save As requires a new file.");
            await VerifiedDocumentSave.StageAsync(Current.Document, temp, token, target);
            token.ThrowIfCancellationRequested(); VerifiedDocumentSave.ValidateDestination(target);
            if (destination == null) { await CheckBaseline(token); File.Replace(temp, target, null); } else File.Move(temp, target, false);
            TargetPath = target; TargetStamp = FileStamp.Read(target); saved = Current; return target;
        }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } finally { saving = false; Changed?.Invoke(); } }
    }
    private Task CheckBaseline(CancellationToken token) => VerifiedDocumentSave.CheckBaselineAsync(TargetPath, saved.Document.Bytes, token);
    private static string Hash(ReadOnlyMemory<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes.Span));
    private static void ValidateName(string name)
    { if (name.Length is < 1 or > 63 || name.Any(c => c == 0 || c > 255)) throw new InvalidDataException("Member names require 1–63 Latin-1 characters without NUL."); }
}

public static class ArchiveWriter
{
    public static byte[] Write(ZbdDocument source, IReadOnlyList<ResourceMember> members, CancellationToken token = default)
    {
        long table = source.ArchiveDirectoryOffset ?? throw new InvalidDataException("An intact ZAR directory is required.");
        long maximum = table + 8 + members.Count * 148L;
        foreach (var m in members) if (m.SourceIndex is not int i || !m.Data.Span.SequenceEqual(source.Slice(source.Assets[i].Offset, source.Assets[i].Length).Span)) maximum += m.Data.Length;
        if (maximum > FormatRegistry.MaximumDocumentBytes) throw new InvalidDataException("Archive exceeds the 512 MiB document limit.");
        using MemoryStream output = new((int)maximum); output.Write(source.Bytes.Span[..(int)table]); List<byte[]> records = [];
        foreach (var member in members)
        {
            token.ThrowIfCancellationRequested(); byte[] record = member.DirectoryRecord.ToArray();
            if (record.Length != 148) throw new InvalidDataException("Invalid archive directory record.");
            // Intact archive assets retain directory order, as already required
            // by the size calculation above. Avoid scanning it for every member.
            var original = member.SourceIndex is int index ? source.Assets[index] : null;
            uint offset;
            if (original != null && member.Data.Span.SequenceEqual(source.Slice(original.Offset, original.Length).Span)) offset = checked((uint)original.Offset);
            else { offset = checked((uint)output.Position); output.Write(member.Data.Span); }
            BinaryPrimitives.WriteUInt32LittleEndian(record, offset); BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4), checked((uint)member.Data.Length));
            if (original?.Name != member.Name)
            { record.AsSpan(8, 64).Clear(); if (member.Name.Length > 63 || member.Name.Any(c => c > 255 || c == 0)) throw new InvalidDataException("Invalid member name."); Encoding.Latin1.GetBytes(member.Name).CopyTo(record, 8); }
            records.Add(record);
        }
        foreach (var record in records) output.Write(record);
        Span<byte> trailer = stackalloc byte[8]; BinaryPrimitives.WriteUInt32LittleEndian(trailer, 1); BinaryPrimitives.WriteUInt32LittleEndian(trailer[4..], (uint)members.Count); output.Write(trailer);
        return output.ToArray();
    }
}
