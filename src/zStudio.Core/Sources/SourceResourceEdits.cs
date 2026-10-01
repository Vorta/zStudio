using System.Buffers.Binary;
using System.Text;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core.Sources;

/// <summary>
/// Carries edits made to a built resource archive (<c>zrdr.zbd</c> built from a source project) back to the source files its
/// members were compiled from. A member records its source in the archive directory, and its payload is the compiled tree of
/// that file, node for node; so a scalar at an offset of the payload is the token at the same place of the source's tree.
/// Text sources change only in the edited tokens (see <see cref="ZrdTextSyntax.ReplaceScalars"/>); compiled sources change
/// only in the edited scalars' bytes.
/// </summary>
public static class SourceResourceEdits
{
    /// <summary>A scalar to write: its offset in the built archive (the start of its node) and its new value.</summary>
    public readonly record struct ScalarEdit(long Offset, ZrdNode Value);

    /// <summary>
    /// The new content of each source file that <paramref name="edits"/> change, read through <paramref name="readSource"/>
    /// (the workspace's accepted content). Every edit must fall on a scalar node of a member built from a project source, and
    /// that source must still compile to the member's payload; otherwise nothing is returned and the call throws.
    /// </summary>
    public static IReadOnlyList<(string Relative, byte[] Content)> SourceChanges(ReadOnlyMemory<byte> builtArchive, IEnumerable<ScalarEdit> edits, Func<string, byte[]?> readSource, CancellationToken token = default)
    {
        var members = ArchiveSources.Read(builtArchive);
        Dictionary<int, List<ScalarEdit>> byMember = [];
        foreach (var edit in edits)
        {
            token.ThrowIfCancellationRequested();
            if (edit.Value.Kind is not (ZrdKind.Int or ZrdKind.Float)) throw new InvalidDataException("Only numeric scalars can be written back to a source.");
            var member = members.FirstOrDefault(m => m.Offset <= edit.Offset && edit.Offset + 8 <= m.Offset + m.Payload.Length)
                ?? throw new InvalidDataException($"Offset 0x{edit.Offset:X} is not inside an archive member.");
            if (!byMember.TryGetValue(member.Index, out var list)) byMember[member.Index] = list = [];
            list.Add(edit with { Offset = edit.Offset - member.Offset });
        }
        List<(string, byte[])> changes = [];
        foreach (var (index, list) in byMember.OrderBy(p => p.Key))
        {
            var member = members[index];
            string relative = SourcePath(member);
            byte[] source = readSource(relative) ?? throw new InvalidDataException($"{relative}, the source of archive member {member.Name}, does not exist; rebuild the world.");
            changes.Add((relative, Apply(source, member.Payload, list, $"{relative} (member {member.Name})", token)));
        }
        return changes;
    }

    /// <summary>The project-relative source a built member records (the builder writes it with backslashes).</summary>
    private static string SourcePath(ArchiveSources.Member member)
    {
        if (member.SourceField is not { Length: > 0 } field) throw new InvalidDataException($"Archive member {member.Name} does not record its source file.");
        string relative = field.Replace('\\', '/');
        if (!relative.StartsWith(SourceProject.DataFolder + "/", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Archive member {member.Name} was not built from a project source ({field}).");
        _ = SourceProject.Resolve(Path.GetTempPath(), relative);
        return relative;
    }

    /// <summary>
    /// <paramref name="source"/> (a text or compiled zReader file) with <paramref name="edits"/> applied, where offsets are
    /// node starts within <paramref name="payload"/>, the member compiled from it.
    /// </summary>
    public static byte[] Apply(byte[] source, ReadOnlyMemory<byte> payload, IReadOnlyList<ScalarEdit> edits, string name, CancellationToken token = default)
    {
        var compiled = ZrdDecoder.Read(payload, token);
        Dictionary<long, ZrdNode> builtNodes = [];
        Index(compiled, builtNodes);
        if (ZrdText.LooksLikeText(source))
        {
            var syntax = ZrdTextSyntax.Parse(source, token);
            if (!ZrdTextSyntax.StructurallyEqual(syntax.Root, compiled)) throw new InvalidDataException($"{name} no longer compiles to the archive the world was built with; rebuild the world first.");
            // Both trees list the same nodes in the same order, so pairing them in order pairs each built node with its source node.
            Dictionary<long, Guid> ids = [];
            Pair(compiled, syntax.Root, ids);
            Dictionary<Guid, ZrdNode> replacements = [];
            foreach (var edit in edits)
            {
                if (!builtNodes.TryGetValue(edit.Offset, out var node) || node.Kind is not (ZrdKind.Int or ZrdKind.Float)) throw new InvalidDataException($"{name}: offset 0x{edit.Offset:X} is not a numeric scalar.");
                replacements[ids[edit.Offset]] = edit.Value;
            }
            // Source text is read as Latin-1, so encoding back as Latin-1 keeps every byte the edit does not touch.
            return Encoding.Latin1.GetBytes(syntax.ReplaceScalars(replacements, token));
        }
        var tree = ZrdDecoder.Read(source, token);
        if (!ZrdTextSyntax.StructurallyEqual(tree, compiled)) throw new InvalidDataException($"{name} no longer compiles to the archive the world was built with; rebuild the world first.");
        Dictionary<long, long> offsets = [];
        PairOffsets(compiled, tree, offsets);
        byte[] result = source.ToArray();
        foreach (var edit in edits)
        {
            if (!builtNodes.TryGetValue(edit.Offset, out var node) || node.Kind is not (ZrdKind.Int or ZrdKind.Float)) throw new InvalidDataException($"{name}: offset 0x{edit.Offset:X} is not a numeric scalar.");
            long at = offsets[edit.Offset];
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(checked((int)at), 4), (uint)edit.Value.Kind);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(checked((int)at + 4), 4), edit.Value.Bits);
        }
        return result;

        static void Index(ZrdNode node, Dictionary<long, ZrdNode> nodes)
        {
            nodes[node.SourceOffset] = node;
            foreach (var child in node.Children) Index(child, nodes);
        }
        static void Pair(ZrdNode built, ZrdNode text, Dictionary<long, Guid> ids)
        {
            ids[built.SourceOffset] = text.Id;
            for (int i = 0; i < built.Children.Count; i++) Pair(built.Children[i], text.Children[i], ids);
        }
        static void PairOffsets(ZrdNode built, ZrdNode file, Dictionary<long, long> offsets)
        {
            offsets[built.SourceOffset] = file.SourceOffset;
            for (int i = 0; i < built.Children.Count; i++) PairOffsets(built.Children[i], file.Children[i], offsets);
        }
    }

    /// <summary>A float scalar node, as placement edits write coordinates (an edited integer coordinate becomes a float).</summary>
    public static ZrdNode Float(float value) => new(Guid.NewGuid(), ZrdKind.Float, BitConverter.SingleToUInt32Bits(value), "", []);
}
