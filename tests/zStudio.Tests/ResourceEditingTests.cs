using System.Buffers.Binary;
using System.Text;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ResourceEditingTests
{
    [Fact]
    public async Task ResourceSnapshotIdentityFollowsTheExactDocumentAcrossHistory()
    {
        var token = TestContext.Current.CancellationToken;
        byte[] bytes = Archive(("same", new byte[] { 99 }));
        var source = FormatRegistry.Default.OpenBytes("library.zbd", bytes, token: token);
        var edits = new ResourceEditSession(source); var original = edits.Current;
        edits.Accept(await edits.PrepareArchiveAsync("duplicate", original.Members[0].Id, "same", token: token));
        var duplicate = edits.Current; Guid id = duplicate.Members[1].Id;
        edits.Accept(await edits.PrepareArchiveAsync("rename", id, "renamed", token: token));
        var renamed = edits.Current;
        Assert.Same(original, edits.SnapshotFor(source)); Assert.Same(duplicate, edits.SnapshotFor(duplicate.Document));
        Assert.Same(renamed, edits.SnapshotFor(renamed.Document));
        Assert.Null(edits.SnapshotFor(FormatRegistry.Default.OpenBytes("library.zbd", duplicate.Document.Bytes.ToArray(), token: token)));
        edits.UndoRedo(false); edits.UndoRedo(false);
        Assert.Same(renamed, edits.SnapshotFor(renamed.Document)); Assert.Same(duplicate, edits.SnapshotFor(duplicate.Document));
        edits.UndoRedo(true); edits.UndoRedo(true);
        Assert.Equal(id, edits.SnapshotFor(duplicate.Document)!.Members[1].Id);
        Assert.Null(edits.SnapshotFor(duplicate.Document)!.Members[1].SourceIndex);
        Assert.Equal(bytes, source.Bytes.ToArray());

        // Tiny raw snapshots must still evict decoded history. Each edit owns a verified reader tree
        // as well as its authored tree; shared saved/source roots must not be charged on every undo entry.
        var values = ZrdNode.Create(ZrdKind.Array) with { Children = Enumerable.Range(0, 64).Select(i => ZrdNode.Create(ZrdKind.Int, i.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray() };
        var valueDocument = FormatRegistry.Default.OpenBytes("values.zrd", ZrdWriter.Write(values, token), token: token);
        var bounded = new ResourceEditSession(valueDocument, token, null, maximumRetainedBytes: 256 * 1024, maximumConstructionBytes: 2 * 1024 * 1024);
        var valueMember = bounded.Current.Members[0]; Guid scalar = bounded.Tree(valueMember, token).Children[0].Id;
        ResourceSnapshot? early = null;
        for (int i = 1; i <= 24; i++)
        {
            bounded.Accept(await bounded.PrepareZrdAsync(valueMember.Id, scalar, "set", value: i.ToString(System.Globalization.CultureInfo.InvariantCulture), token: token));
            if (i == 1) early = bounded.Current;
        }
        Assert.Null(bounded.SnapshotFor(early!.Document)); Assert.True(bounded.CanUndo);
        var beforeRefusal = bounded.Current;
        var refusal = await Assert.ThrowsAsync<InvalidDataException>(() => bounded.PrepareZrdAsync(valueMember.Id, scalar, "type", ZrdKind.String,
            "\"" + new string('x', 262144) + "\"", token: token));
        Assert.Contains("allowance", refusal.Message); Assert.Same(beforeRefusal, bounded.Current); Assert.True(bounded.CanUndo);
        bounded.Accept(await bounded.PrepareZrdAsync(valueMember.Id, scalar, "set", value: "25", token: token));
        bounded.UndoRedo(false); Assert.Same(beforeRefusal, bounded.Current);
        bounded.UndoRedo(true); Assert.Equal(scalar, bounded.Tree(bounded.Member(valueMember.Id), token).Children[0].Id);
    }

    [Fact]
    public void SupportedZrdDepthFitsBoundedInspectionAndCompleteJsonExport()
    {
        var token = TestContext.Current.CancellationToken;
        var root = ZrdNode.Create(ZrdKind.Int, "42");
        for (int i = 0; i < 128; i++) root = ZrdNode.Create(ZrdKind.Array) with { Children = [root] };
        var doc = FormatRegistry.Default.OpenBytes("deep.zrd", ZrdWriter.Write(root, token), token: token);
        Assert.Empty(doc.Diagnostics);
        var tree = Assert.IsType<ZrdNode>(doc.Assets[0].Content);
        string full = tree.ToJson(token).ToJsonString(JsonData.Options);
        Assert.Contains("42", full); Assert.DoesNotContain("children_truncated", full);
        Assert.Contains("children_truncated", tree.ToPreviewJson(token).ToJsonString());
    }
    [Fact]
    public void StandaloneZrdMustDecodeCompletelyBeforeEditing()
    {
        var token = TestContext.Current.CancellationToken;
        foreach (byte[] bytes in new byte[][] { [1,0,0,0], [2,0,0,0], [3,0,0,0,2,0,0,0,65], [4,0,0,0,0,0,0,0], [4,0,0,0,2,0,0,0,99,0,0,0], [1,0,0,0,0,0,0,0,99] })
        {
            var doc = FormatRegistry.Default.OpenBytes("malformed.zrd", bytes, token: token);
            Assert.Contains(doc.Diagnostics, d => d.Severity == "Error" && d.Message.StartsWith("Parsing stopped:"));
            Assert.Equal(AssetKind.Raw, Assert.Single(doc.Assets).Kind);
            Assert.Throws<InvalidDataException>(() => new ResourceEditSession(doc));
        }
        var valid = FormatRegistry.Default.OpenBytes("valid.zrd", ZrdWriter.Write(ZrdNode.Create(ZrdKind.Int, "0"), token), token: token);
        Assert.Empty(valid.Diagnostics);
        var tree = Assert.IsType<ZrdNode>(Assert.Single(valid.Assets).Content);
        var edits = new ResourceEditSession(valid);
        Assert.Same(tree, edits.Tree(Assert.Single(edits.Current.Members), token));
        var uncheckedDocument = new ZbdDocument("malformed.zrd", new(4, DateTime.MinValue), valid.Probe, new byte[] { 1,0,0,0 });
        Assert.Throws<InvalidDataException>(() => new ResourceEditSession(uncheckedDocument));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => FormatRegistry.Default.OpenBytes("valid.zrd", valid.Bytes.ToArray(), token: cancellation.Token));
    }
    [Fact]
    public async Task MalformedEmbeddedZrdRemainsRawAndCanBeReplaced()
    {
        var token = TestContext.Current.CancellationToken;
        var doc = FormatRegistry.Default.OpenBytes("resources.zbd", Archive(("bad.zrd", [1,0,0,0])), token: token);
        Assert.Equal(AssetKind.Raw, Assert.Single(doc.Assets).Kind);
        Assert.Contains(doc.Diagnostics, d => d.Severity == "Warning" && d.AssetIndex == 0 && d.Offset == 0);
        var edits = new ResourceEditSession(doc);
        edits.Accept(await edits.PrepareArchiveAsync("delete", edits.Current.Members[0].Id, token: token));
        Assert.Empty(edits.Current.Members); edits.UndoRedo(false);
        Assert.Equal(new byte[] { 1,0,0,0 }, edits.Current.Members[0].Data.ToArray());
    }
    [Fact]
    public async Task ReplacingAnArrayClearsItsChildrenAndUndoRestoresThem()
    {
        var token = TestContext.Current.CancellationToken;
        var root = ZrdNode.Create(ZrdKind.Array) with { Children = [ZrdNode.Create(ZrdKind.Int, "23")] };
        var edits = new ResourceEditSession(FormatRegistry.Default.OpenBytes("values.zrd", ZrdWriter.Write(root, token), token: token));
        var member = edits.Current.Members[0]; root = edits.Tree(member, token);
        await Assert.ThrowsAsync<InvalidDataException>(() => edits.PrepareZrdAsync(member.Id, root.Id, "set", value: "ignored", token: token));
        Assert.False(edits.IsDirty);
        edits.Accept(await edits.PrepareZrdAsync(member.Id, root.Id, "type", ZrdKind.Array, token: token));
        Assert.Empty(edits.Tree(edits.Member(member.Id), token).Children);
        edits.UndoRedo(false); Assert.Equal(root, edits.Tree(edits.Member(member.Id), token));
    }
    [Fact]
    public async Task AliasedArchivePayloadIsDecodedOnceAndEditsStayIndependent()
    {
        var token = TestContext.Current.CancellationToken;
        byte[] data = ZrdWriter.Write(ZrdNode.Create(ZrdKind.String) with { Text = new string('x', 256 * 1024) }, token);
        const int count = 128; byte[] bytes = new byte[data.Length + count * 148 + 8]; data.CopyTo(bytes, 0);
        for (int i = 0; i < count; i++)
        {
            int record = data.Length + i * 148;
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(record + 4), data.Length);
            Encoding.Latin1.GetBytes("alias.zrd").CopyTo(bytes, record + 8);
        }
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(bytes.Length - 8), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(bytes.Length - 4), count);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        var doc = FormatRegistry.Default.OpenBytes("aliases.zbd", bytes, token: token);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.True(allocated < 4 * 1024 * 1024, $"Aliased payload allocated {allocated:N0} bytes.");
        Assert.Equal(count, doc.Assets.Count);
        var edits = new ResourceEditSession(doc); var first = edits.Current.Members[0]; var second = edits.Current.Members[1];
        edits.Accept(await edits.PrepareZrdAsync(first.Id, edits.Tree(first, token).Id, "set", value: "\"changed\"", token: token));
        Assert.Equal("changed", edits.Tree(edits.Member(first.Id), token).Text);
        Assert.Equal(256 * 1024, edits.Tree(edits.Member(second.Id), token).Text.Length);
        Assert.NotEqual(first.Id, second.Id); edits.UndoRedo(false); Assert.False(edits.IsDirty);
    }
    [Fact]
    public async Task RejectedWorkspacePublicationLeavesEveryPreviousOwnerIntact()
    {
        string root = Path.Combine(Path.GetTempPath(), "zstudio-snapshots");
        using AssetResolver resolver = new(root); var token = TestContext.Current.CancellationToken;
        var first = FormatRegistry.Default.OpenBytes(Path.Combine(root, "one.zbd"), Archive(), token: token);
        var second = FormatRegistry.Default.OpenBytes(Path.Combine(root, "two.zbd"), Archive(), token: token);
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        resolver.SetWorkspaceSnapshots(a, [first]); resolver.SetWorkspaceSnapshots(b, [second]); long revision = resolver.SnapshotRevision;
        Assert.Throws<ArgumentException>(() => resolver.SetWorkspaceSnapshots(b, [first]));
        Assert.Equal(revision, resolver.SnapshotRevision);
        resolver.SetWorkspaceSnapshots(a, []);
        Assert.Same(second, await resolver.OpenCachedAsync(second.Path, token));
        resolver.SetWorkspaceSnapshots(a, [first]);
        Assert.Same(first, await resolver.OpenCachedAsync(first.Path, token));
        // Closing a document that never published anything is not a source change for other previews.
        revision = resolver.SnapshotRevision; bool notified = false; resolver.WorkspaceSnapshotsChanged += () => notified = true;
        resolver.SetWorkspaceSnapshots(Guid.NewGuid(), []);
        Assert.Equal(revision, resolver.SnapshotRevision); Assert.False(notified);
        resolver.SetWorkspaceSnapshots(a, []);
        Assert.Equal(revision + 1, resolver.SnapshotRevision); Assert.True(notified);
    }
    [Fact]
    public async Task RenamedTypedMembersKeepStructureAfterUndoAndReopen()
    {
        var token = TestContext.Current.CancellationToken;
        foreach (var node in new[] { ZrdNode.Create(ZrdKind.Int, "0"), ZrdNode.Create(ZrdKind.String, "\"value\""), ZrdNode.Create(ZrdKind.Array) })
        {
            var doc = FormatRegistry.Default.OpenBytes("test.zbd", Archive(("typed.zrd", ZrdWriter.Write(node, token))), token: token);
            var edits = new ResourceEditSession(doc); var member = edits.Current.Members.Single(); Guid id = edits.Tree(member, token).Id;
            edits.Accept(await edits.PrepareArchiveAsync("rename", member.Id, "renamed.bin", token: token));
            Assert.Equal(AssetKind.Zrd, edits.Current.Document.Assets.Single().Kind); Assert.Equal(id, edits.Tree(edits.Member(member.Id), token).Id);
            var reopened = FormatRegistry.Default.OpenBytes("copy.zbd", edits.Current.Document.Bytes.ToArray(), token: token);
            Assert.Equal(AssetKind.Zrd, reopened.Assets.Single().Kind);
            Assert.Equal(node.Kind, Assert.IsType<ZrdNode>(reopened.Assets.Single().Content).Kind);
            edits.UndoRedo(false); Assert.Equal("typed.zrd", edits.Current.Members.Single().Name); edits.UndoRedo(true);
            Assert.Equal(id, edits.Tree(edits.Member(member.Id), token).Id);
        }
        var opaque = FormatRegistry.Default.OpenBytes("opaque.zbd", Archive(("not-zrd", new byte[] {1,0,0,0,0,0,0,0,99})), token: token);
        Assert.Equal(AssetKind.Raw, opaque.Assets.Single().Kind); // A plausible first word is insufficient.
    }
    [Fact]
    public void ZrdInspectionBoundsTreeAndStringsWithoutTruncatingExports()
    {
        var token = TestContext.Current.CancellationToken;
        var text = ZrdNode.Create(ZrdKind.String) with { Text = new string('\0', 1024 * 1024) };
        var root = ZrdNode.Create(ZrdKind.Array) with { Children = Enumerable.Range(0, 2048).Select(_ => text with { Id = Guid.NewGuid() }).ToArray() };
        var preview = root.ToPreviewJson(token); Assert.True(preview["children_truncated"]!.GetValue<bool>());
        Assert.Equal(1023, preview["children"]!.AsArray().Count);
        Assert.True(preview["children"]![0]!["value_truncated"]!.GetValue<bool>());
        Assert.True(preview["children"]!.AsArray().Sum(n => n!["value"]!.GetValue<string>().Length) <= 65536);
        Assert.True(preview.ToJsonString().Length < 512 * 1024);
        Assert.Equal(text.Text, text.ToJson(token)["value"]!.GetValue<string>());
    }
    [Fact]
    public void ZrdPreviewBoundsFormattingBeforeEscapingAndPreservesExactPrefixes()
    {
        foreach (string text in new[] { "", "plain", new string('x', 4094), new string('x', 4095), string.Concat(Enumerable.Range(0, 256).Select(i => (char)i)) })
        {
            var node = ZrdNode.Create(ZrdKind.String) with { Text = text };
            string full = System.Text.Json.JsonSerializer.Serialize(text);
            foreach (int limit in new[] { 1, 2, 199, 200, 4096 })
            {
                var preview = node.PreviewValue(limit);
                Assert.Equal(full[..Math.Min(limit, full.Length)], preview.Value);
                Assert.Equal(full.Length > limit, preview.Truncated);
            }
        }
        foreach (var node in new[] { ZrdNode.Create(ZrdKind.Int, "-2147483648"), ZrdNode.Create(ZrdKind.Float, "0x7FA12345"), ZrdNode.Create(ZrdKind.Array) })
            Assert.Equal((node.Value, false), node.PreviewValue(4096));
        var large = ZrdNode.Create(ZrdKind.String) with { Text = new string('\0', 8 * 1024 * 1024) };
        _ = large.PreviewValue(4096);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        var bounded = large.PreviewValue(4096);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Assert.Equal(4096, bounded.Value.Length); Assert.True(bounded.Truncated);
        // Concurrent corpus tests can trim the serializer's pooled buffers between warmup and measurement.
        // Include cold buffer rentals while remaining far below the 8 MiB stored string (before escaping).
        Assert.True(allocated < 256 * 1024, $"Formatting allocated {allocated:N0} bytes for a bounded prefix.");
        Assert.Throws<ArgumentOutOfRangeException>(() => large.PreviewValue(0));
    }
    [Fact]
    public async Task OptionalCorpusArchiveAndZrdRoundTripsAreByteIdentical()
    {
        string? root = Environment.GetEnvironmentVariable("ZSTUDIO_CORPUS"); if (root == null) return;
        var token = TestContext.Current.CancellationToken;
        int archives = 0, resources = 0;
        foreach (string path in Directory.EnumerateFiles(root, "*.zbd", SearchOption.AllDirectories).Where(p => FormatRegistry.Probe(p).Family == FormatFamily.Archive))
        {
            var doc = await FormatRegistry.Default.OpenAsync(path, token); var edits = new ResourceEditSession(doc);
            Assert.Equal(doc.Bytes.ToArray(), ArchiveWriter.Write(doc, edits.Current.Members, token)); archives++;
            foreach (var asset in doc.Assets.Where(a => a.Kind == AssetKind.Zrd))
            {
                var bytes = doc.Slice(asset.Offset, asset.Length); Assert.Equal(bytes.ToArray(), ZrdWriter.Write(ZrdDecoder.Read(bytes, token), token)); resources++;
            }
        }
        Assert.True(archives > 0 && resources > 0, "Corpus must contain both ZAR archives and ZRD members.");
    }
    [Fact]
    public async Task ImportsEmptyArchivesAndStructuralEditsRetainUntouchedBytes()
    {
        string root = Path.Combine(Path.GetTempPath(), "zstudio-resource-input-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); var token = TestContext.Current.CancellationToken;
        try
        {
            string input = Path.Combine(root, "input.bin"); await File.WriteAllBytesAsync(input, [9,8,7,6,5], token);
            var edits = new ResourceEditSession(FormatRegistry.Default.OpenBytes("empty.zbd", Archive(), token: token));
            edits.Accept(await edits.PrepareArchiveAsync("add", Guid.Empty, "opaque", input, token: token)); var first = edits.Current.Members.Single();
            edits.Accept(await edits.PrepareArchiveAsync("add_zrd", Guid.Empty, "data.zrd", token: token)); var zrd = edits.Current.Members[1]; var node = edits.Tree(zrd, token);
            edits.Accept(await edits.PrepareZrdAsync(zrd.Id, node.Id, "add", ZrdKind.String, "\"abc\"", token: token));
            var child = edits.Tree(edits.Member(zrd.Id), token).Children.Single();
            edits.Accept(await edits.PrepareZrdAsync(zrd.Id, child.Id, "duplicate", token: token));
            var nodes = edits.Tree(edits.Member(zrd.Id), token).Children; Assert.NotEqual(nodes[0].Id, nodes[1].Id);
            edits.Accept(await edits.PrepareZrdAsync(zrd.Id, child.Id, "type", ZrdKind.Int, "-17", token: token));
            edits.Accept(await edits.PrepareZrdAsync(zrd.Id, child.Id, "delete", token: token));
            Assert.Equal("abc", edits.Tree(edits.Member(zrd.Id), token).Children.Single().Text);
            Assert.Equal(first.Data.ToArray(), edits.Member(first.Id).Data.ToArray());
            await Assert.ThrowsAsync<InvalidDataException>(() => edits.PrepareArchiveAsync("replace", zrd.Id, path: input, token: token));
            await File.WriteAllBytesAsync(input, ZrdWriter.Write(ZrdNode.Create(ZrdKind.Int, "9"), token), token);
            edits.Accept(await edits.PrepareArchiveAsync("replace", zrd.Id, path: input, token: token)); Assert.Equal(-1, edits.Tree(edits.Member(zrd.Id), token).SourceOffset);
            // Ordinary opaque payloads do not expand into typed trees. A five-MiB replacement must
            // remain supported even though applying the ZRD expansion estimate to it would exceed 1 GiB.
            byte[] opaque = new byte[5 * 1024 * 1024]; opaque[0] = 99; opaque[^1] = 77;
            await File.WriteAllBytesAsync(input, opaque, token);
            edits.Accept(await edits.PrepareArchiveAsync("replace", first.Id, path: input, token: token));
            Assert.True(opaque.AsSpan().SequenceEqual(edits.Member(first.Id).Data.Span));
            edits.UndoRedo(false); Assert.Equal(first.Data.ToArray(), edits.Member(first.Id).Data.ToArray());
            await File.WriteAllBytesAsync(input, [7], token); edits.Accept(await edits.PrepareArchiveAsync("replace", first.Id, path: input, token: token)); Assert.Equal(new byte[] {7}, edits.Member(first.Id).Data.ToArray());
            edits.UndoRedo(false); Assert.Equal(first.Data.ToArray(), edits.Member(first.Id).Data.ToArray());
            edits.Accept(await edits.PrepareArchiveAsync("delete", first.Id, token: token)); edits.Accept(await edits.PrepareArchiveAsync("delete", zrd.Id, token: token)); Assert.Empty(edits.Current.Document.Assets);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void BoundedZrdReaderAndWriterRejectMalformedStructure()
    {
        var token = TestContext.Current.CancellationToken;
        foreach (byte[] bytes in new byte[][] { [], [1,0,0,0], [7,0,0,0], [3,0,0,0,255,255,255,127], [4,0,0,0,255,255,255,127], [1,0,0,0,0,0,0,0,9] })
            Assert.Throws<InvalidDataException>(() => ZrdDecoder.Read(bytes, token));
        var child = ZrdNode.Create(ZrdKind.Int); var duplicate = ZrdNode.Create(ZrdKind.Array) with { Children = [child, child] }; Assert.Throws<InvalidDataException>(() => ZrdWriter.Write(duplicate, token));
        var nested = child; for (int i = 0; i < 130; i++) nested = ZrdNode.Create(ZrdKind.Array) with { Children = [nested] };
        Assert.Throws<InvalidDataException>(() => ZrdWriter.Write(nested, token));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); Assert.Throws<OperationCanceledException>(() => ZrdWriter.Write(child, cancellation.Token));
    }
    internal static byte[] Archive(params (string Name, byte[] Bytes)[] entries)
    {
        int size = entries.Sum(e => e.Bytes.Length); byte[] bytes = new byte[size + entries.Length * 148 + 8]; int offset = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            var e = entries[i]; e.Bytes.CopyTo(bytes, offset); int record = size + i * 148;
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(record), offset); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(record + 4), e.Bytes.Length);
            Encoding.Latin1.GetBytes(e.Name).CopyTo(bytes, record + 8); bytes[record + 72] = 19; bytes[record + 144] = 27; offset += e.Bytes.Length;
        }
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(bytes.Length - 8), 1); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(bytes.Length - 4), entries.Length); return bytes;
    }
    [Fact]
    public async Task ArchiveStructureRetainsIdentityMetadataAndPayload()
    {
        var token = TestContext.Current.CancellationToken;
        byte[] bytes = Archive(("same", [1,2,3]), ("same", [9,8])); var doc = FormatRegistry.Default.OpenBytes("test.zbd", bytes, token: token); var edits = new ResourceEditSession(doc);
        Assert.Equal(bytes, ArchiveWriter.Write(doc, edits.Current.Members, token));
        var first = edits.Current.Members[0]; var second = edits.Current.Members[1];
        edits.Accept(await edits.PrepareArchiveAsync("rename", second.Id, "renamed", token: token));
        edits.Accept(await edits.PrepareArchiveAsync("move", second.Id, position: 0, token: token));
        Assert.Equal(second.Id, edits.Current.Members[0].Id); Assert.Equal(first.Id, edits.Current.Members[1].Id);
        Assert.Equal(bytes.AsSpan(0, 5).ToArray(), edits.Current.Document.Bytes.Span[..5].ToArray());
        Assert.Equal(19, edits.Current.Document.Bytes.Span[(int)edits.Current.Document.ArchiveDirectoryOffset! + 72]);
        edits.Accept(await edits.PrepareArchiveAsync("duplicate", first.Id, "copy", token: token));
        Assert.NotEqual(first.Id, edits.Current.Members[2].Id);
        edits.Accept(await edits.PrepareArchiveAsync("delete", second.Id, token: token));
        Assert.Equal(2, edits.Current.Members.Count);
        edits.UndoRedo(false); Assert.Equal(second.Id, edits.Current.Members[0].Id);
        edits.UndoRedo(false); edits.UndoRedo(false); edits.UndoRedo(false); Assert.False(edits.IsDirty); Assert.Equal(bytes, edits.Current.Document.Bytes.ToArray());
    }
    [Fact]
    public async Task ZrdStructureAndFloatBitsRoundTripWithUndo()
    {
        var token = TestContext.Current.CancellationToken;
        var root = ZrdNode.Create(ZrdKind.Array) with { Children = [ZrdNode.Create(ZrdKind.Float, "0x7FA12345"), ZrdNode.Create(ZrdKind.String, "\"old\\u0000\\u00ff\""), ZrdNode.Create(ZrdKind.Array)] };
        byte[] bytes = ZrdWriter.Write(root, token); Assert.Equal(bytes, ZrdWriter.Write(ZrdDecoder.Read(bytes, token), token));
        var edits = new ResourceEditSession(FormatRegistry.Default.OpenBytes("test.zrd", bytes, token: token)); var member = edits.Current.Members[0]; root = edits.Tree(member, token);
        var value = root.Children[1]; var array = root.Children[2];
        edits.Accept(await edits.PrepareZrdAsync(member.Id, value.Id, "set", value: "\"a much longer string\"", token: token));
        edits.Accept(await edits.PrepareZrdAsync(member.Id, value.Id, "move", parent: array.Id, position: 0, token: token));
        Assert.Equal(value.Id, edits.Tree(edits.Member(member.Id), token).Children[1].Children[0].Id);
        Assert.Equal(0x7FA12345u, edits.Tree(edits.Member(member.Id), token).Children[0].Bits);
        await Assert.ThrowsAsync<InvalidDataException>(() => edits.PrepareZrdAsync(member.Id, array.Id, "move", parent: value.Id, position: 0, token: token));
        edits.UndoRedo(false); edits.UndoRedo(false); Assert.False(edits.IsDirty);
        await Assert.ThrowsAsync<InvalidDataException>(() => edits.PrepareZrdAsync(member.Id, root.Id, "delete", token: token));
    }
    [Fact]
    public async Task EmbeddedZrdSavesAndDetectsExternalChanges()
    {
        string root = Path.Combine(Path.GetTempPath(), "zstudio-resource-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); var token = TestContext.Current.CancellationToken;
        try
        {
            string path = Path.Combine(root, "data.zbd"); byte[] original = Archive(("data.zrd", ZrdWriter.Write(ZrdNode.Create(ZrdKind.Int, "3"), token)), ("opaque", [9,8,7])); await File.WriteAllBytesAsync(path, original, token);
            var edits = new ResourceEditSession(await FormatRegistry.Default.OpenAsync(path, token)); var member = edits.Current.Members[0]; var node = edits.Tree(member, token);
            edits.Accept(await edits.PrepareZrdAsync(member.Id, node.Id, "type", ZrdKind.String, "\"longer\"", token: token));
            await edits.SaveAsync(token: token); Assert.False(edits.IsDirty);
            var check = await FormatRegistry.Default.OpenAsync(path, token); Assert.Equal("longer", ZrdDecoder.Read(check.Slice(check.Assets[0].Offset, check.Assets[0].Length), token).Text);
            Assert.Equal(new byte[] {9,8,7}, check.Slice(check.Assets[1].Offset, check.Assets[1].Length).ToArray());
            edits.UndoRedo(false); await edits.SaveAsync(token: token); Assert.Equal(original, await File.ReadAllBytesAsync(path, token));
            edits.UndoRedo(true); await File.AppendAllTextAsync(path, "external", token); await Assert.ThrowsAsync<IOException>(() => edits.SaveAsync(token: token));
            string copy = Path.Combine(root, "copy.zbd"); await edits.SaveAsync(copy, token); Assert.Equal(copy, edits.TargetPath); Assert.False(edits.IsDirty);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task MalformedEditsAndProtectedSavesNeverPublish()
    {
        var token = TestContext.Current.CancellationToken;
        var edits = new ResourceEditSession(FormatRegistry.Default.OpenBytes(Path.Combine(Path.GetTempPath(), "zbd_1999", "data.zbd"), Archive(("a", [9])), token: token));
        var member = edits.Current.Members[0]; await Assert.ThrowsAsync<InvalidDataException>(() => edits.PrepareArchiveAsync("rename", member.Id, new string('x', 64), token: token));
        Assert.Throws<InvalidDataException>(() => ZrdNode.Create(ZrdKind.String, "\"漢\""));
        Assert.Throws<InvalidDataException>(() => ZrdDecoder.Read(new byte[] {4,0,0,0,0,0,0,0}, token));
        await Assert.ThrowsAsync<IOException>(() => edits.SaveAsync(token: token)); Assert.False(edits.IsDirty);
        var stale = await edits.PrepareArchiveAsync("rename", member.Id, "b", token: token); edits.Accept(await edits.PrepareArchiveAsync("rename", member.Id, "c", token: token)); Assert.Throws<InvalidOperationException>(() => edits.Accept(stale));
    }
    [Fact]
    public void OwnershipRetainsOneWriterUntilReleased()
    {
        ResourceEditOwnership ownership = new(); Guid first = Guid.NewGuid(), second = Guid.NewGuid(); string path = Path.Combine(Path.GetTempPath(), "archive.zbd");
        ownership.Acquire(first, "map", [path]); Assert.Throws<InvalidOperationException>(() => ownership.Acquire(second, "archive", [path])); ownership.Release(first); ownership.Acquire(second, "archive", [path]);
    }
}
