using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class TerrainCreationInputBudgetTests
{
    private const string Database = "data/m1/models/m1.gltf";
    private const string First = "data/m1/models/first.gltf";
    private const string Second = "data/m1/models/second.gltf";
    private const string Recipe = "data/m1/terrain/new.terrain.json";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void UnusedReferencesAdmittedByTheWorldStillBoundCreationBeforeOneUndoablePublication()
    {
        using SourceWorldFixture fixture = new();
        byte[] database = AddUnusedReferences(fixture, "first.gltf", "second.gltf");
        byte[] first = Document(), second = [.. Document(), .. Encoding.ASCII.GetBytes(new string(' ', 257))];
        fixture.Write(First, first); fixture.Write(Second, second);
        DiskFiles files = new(fixture);
        var world = new WorldAssembler(files, Token).Assemble("m1.gs");
        Assert.Contains(world.Nodes, n => n.Name == "ground");
        Assert.DoesNotContain(First, files.Reads); Assert.DoesNotContain(Second, files.Reads);

        SourceWorkspace workspace = new(fixture.Project);
        long total = database.LongLength + first.Length + second.Length;
        var refused = Assert.Throws<InvalidDataException>(() => Create(workspace, fixture.Tank, total - 1));
        Assert.Contains("complete model references cannot be checked", refused.Message);
        AssertClean(workspace); Assert.Equal(database, File.ReadAllBytes(fixture.Path(Database)));
        Assert.False(File.Exists(fixture.Path(Recipe)));

        var transaction = Create(workspace, fixture.Tank, total);
        Assert.Same(transaction, Assert.Single(workspace.History));
        Assert.Equal(1, workspace.UndoCount); Assert.Equal(1, workspace.Revision);
        Assert.NotNull(workspace.Read(Recipe, Token));
        Assert.NotEqual(database, workspace.Read(Database, Token));
        Assert.Equal(database, File.ReadAllBytes(fixture.Path(Database)));
        workspace.Undo();
        Assert.Equal(database, workspace.Read(Database, Token)); Assert.Null(workspace.Read(Recipe, Token));
        Assert.False(workspace.IsDirty); Assert.False(workspace.CanUndo); Assert.True(workspace.CanRedo);
    }

    [Fact]
    public void AProtectedModelBeyondTheBudgetIsNeverReportedAsAnIndependentTerrainSource()
    {
        using SourceWorldFixture fixture = new();
        byte[] database = AddUnusedReferences(fixture, "first.gltf");
        byte[] first = Document("../../m2/models/bft/tank.gltf"); fixture.Write(First, first);
        long total = database.LongLength + first.Length + File.ReadAllBytes(fixture.Path(fixture.Tank)).Length;
        SourceWorkspace workspace = new(fixture.Project);
        var capacity = Assert.Throws<InvalidDataException>(() => Create(workspace, fixture.Tank, database.Length + first.Length - 1));
        Assert.Contains("complete model references", capacity.Message); AssertClean(workspace);
        var protectedModel = Assert.Throws<InvalidDataException>(() => Create(workspace, fixture.Tank, total));
        Assert.Contains("loaded with the mission database", protectedModel.Message); AssertClean(workspace);
        Assert.Null(workspace.Read(Recipe, Token));
    }

    [Fact]
    public void PreparedCachedInputsRemainBoundedAndExternalChangesCannotPublish()
    {
        using SourceWorldFixture fixture = new();
        byte[] database = AddUnusedReferences(fixture, "first.gltf"), first = Document(); fixture.Write(First, first);
        SourceWorkspace workspace = new(fixture.Project);
        // A clean baseline exists before the prepared fork; its cached bytes cannot bypass the new allowance.
        workspace.Apply("Temporary padding", [(First, (byte[]?)[.. first, 32])], Token); workspace.Undo();
        long revision = workspace.Revision;
        var prepared = workspace.BeginPreparedEdit();
        Assert.Throws<InvalidDataException>(() => Create(prepared.Workspace, fixture.Tank, database.Length + first.Length - 1));
        Assert.Equal(revision, workspace.Revision); Assert.False(workspace.IsDirty); Assert.True(workspace.CanRedo);
        Assert.False(prepared.Workspace.CanUndo);

        Create(prepared.Workspace, fixture.Tank, database.Length + first.Length);
        fixture.Write(First, [.. first, 32]);
        Assert.Throws<SourceFileChangedException>(() => workspace.AcceptPreparedEdit(prepared, Token));
        Assert.Equal(revision, workspace.Revision); Assert.False(workspace.IsDirty); Assert.True(workspace.CanRedo);
        Assert.Equal(database, workspace.Read(Database, Token)); Assert.Null(workspace.Read(Recipe, Token));
        // Fresh preparation observes the new length and starts with a fresh reference allowance.
        var retry = workspace.BeginPreparedEdit();
        Create(retry.Workspace, fixture.Tank, database.Length + first.Length + 1);
        Assert.NotNull(workspace.AcceptPreparedEdit(retry, Token));
        Assert.Equal(1, workspace.UndoCount); Assert.False(workspace.CanRedo);
    }

    [Fact]
    public void RemainingAllowanceRejectsTheLastHeldPayloadBeforeReadingOrHashingIt()
    {
        byte[] root = Document("first.gltf", "second.gltf"), first = Document(), second = Document("protected.gltf");
        Dictionary<string, byte[]> inputs = new(StringComparer.OrdinalIgnoreCase) { [Database] = root, [First] = first, [Second] = second };
        Dictionary<string, (long Limit, CountingStream Stream)> reads = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> hashed = new(StringComparer.OrdinalIgnoreCase);
        byte[]? Read(string path, ProjectReadLimits limits)
        {
            if (!inputs.TryGetValue(path, out var bytes)) return null;
            var stream = new CountingStream(bytes); reads.Add(path, (limits.MaximumBytes, stream));
            using (stream) return SourceRead.AllAdmitted(stream, limits.MaximumBytes, path, limits.CheckPrefix, Token, _ => hashed.Add(path));
        }
        long total = root.LongLength + first.Length + second.Length;
        Assert.Throws<InvalidDataException>(() => SourceTerrain.Referenced(Database, Read, Token, maximumInputBytes: total - 1));
        Assert.Equal(second.Length - 1, reads[Second].Limit);
        Assert.Equal(0, reads[Second].Stream.BytesRead); Assert.DoesNotContain(Second, hashed);
        Assert.Contains(First, hashed); Assert.True(reads[First].Stream.BytesRead >= first.Length);
        reads.Clear(); hashed.Clear();
        var found = SourceTerrain.Referenced(Database, Read, Token, maximumInputBytes: total);
        Assert.Contains("data/m1/models/protected.gltf", found);
        Assert.Contains(Second, hashed); Assert.Equal(second.Length, reads[Second].Limit);
    }

    [Fact]
    public void CanonicalAliasesCyclesAndAbsentOrEmptyFilesDoNotSpendTheAllowanceTwice()
    {
        byte[] root = Document("./first.gltf", "sub/../first.gltf", "FIRST.gltf", "sub\\..\\first.gltf", "missing.gltf", "empty.gltf");
        byte[] child = Document("./m1.gltf");
        Dictionary<string, byte[]> inputs = new(StringComparer.OrdinalIgnoreCase) { [Database] = root, [First] = child, ["data/m1/models/empty.gltf"] = [] };
        List<(string Path, long Limit)> reads = [];
        byte[]? Read(string path, ProjectReadLimits limits)
        {
            reads.Add((path, limits.MaximumBytes));
            if (!inputs.TryGetValue(path, out var bytes)) return null;
            limits.Validate(bytes); return bytes;
        }
        var found = SourceTerrain.Referenced("./data/m1/./models/m1.gltf", Read, Token, maximumFiles: 4, maximumInputBytes: root.Length + child.Length);
        Assert.Equal(4, found.Count); Assert.Contains(Database, found);
        Assert.Equal(4, reads.Count); Assert.Single(reads, r => r.Path == Database); Assert.Single(reads, r => r.Path == First);
        Assert.Equal(0, reads.Single(r => r.Path.EndsWith("missing.gltf", StringComparison.Ordinal)).Limit);
        Assert.Equal(0, reads.Single(r => r.Path.EndsWith("empty.gltf", StringComparison.Ordinal)).Limit);
    }

    [Fact]
    public void GlbBinaryAndUnknownChunksCountAndItsReferencesProtectTheCompletePath()
    {
        using SourceWorldFixture fixture = new();
        byte[] root = Document("first.glb"), tail = Document();
        byte[] glb = Glb(Document("../other/first.gltf"));
        fixture.Write(Database, root); fixture.Write("data/m1/models/first.glb", glb); fixture.Write("data/m1/other/first.gltf", tail);
        SourceWorkspace workspace = new(fixture.Project);
        long total = root.LongLength + glb.Length + tail.Length;
        Assert.Throws<InvalidDataException>(() => SourceTerrain.Referenced(workspace, Database, Token, maximumInputBytes: total - 1));
        var found = SourceTerrain.Referenced(workspace, Database, Token, maximumInputBytes: total);
        Assert.Equal(2, found.Count); Assert.Contains("data/m1/other/first.gltf", found);
        AssertClean(workspace);
    }

    [Fact]
    public void MalformedJsonStillSpendsItsBytesBeforeTheNextReference()
    {
        using SourceWorldFixture fixture = new();
        byte[] root = Document("first.gltf", "second.gltf"), malformed = Encoding.ASCII.GetBytes("{broken"), second = Document("protected.gltf");
        fixture.Write(Database, root); fixture.Write(First, malformed); fixture.Write(Second, second);
        SourceWorkspace workspace = new(fixture.Project);
        long total = root.LongLength + malformed.Length + second.Length;
        Assert.Throws<InvalidDataException>(() => SourceTerrain.Referenced(workspace, Database, Token, maximumInputBytes: total - 1));
        Assert.Contains("data/m1/models/protected.gltf", SourceTerrain.Referenced(workspace, Database, Token, maximumInputBytes: total));
        AssertClean(workspace);
    }

    [Fact]
    public void DirtyOverlayRefusalAndCancellationPreserveExistingHistoryAndRetryUsesActualOverlayBytes()
    {
        using SourceWorldFixture fixture = new();
        byte[] database = AddUnusedReferences(fixture, "first.gltf"), disk = Document();
        fixture.Write(First, disk);
        SourceWorkspace workspace = new(fixture.Project);
        byte[] overlay = [.. disk, .. Encoding.ASCII.GetBytes(new string(' ', 113))];
        var prior = workspace.Apply("Pad reference", [(First, overlay)], Token);
        long revision = workspace.Revision, total = database.LongLength + overlay.Length;
        Assert.Throws<InvalidDataException>(() => Create(workspace, fixture.Tank, total - 1));
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => SourceTerrain.Create(workspace, Database, fixture.Tank, ["hull"], Recipe, canceled.Token,
            SourceProject.MaximumFiles, maximumReferenceInputBytes: total));
        Assert.Same(prior, Assert.Single(workspace.History)); Assert.Equal(revision, workspace.Revision);
        Assert.Equal(1, workspace.UndoCount); Assert.True(workspace.IsDirty); Assert.False(workspace.CanRedo);
        Assert.Equal(overlay, workspace.Read(First, Token)); Assert.Equal(disk, File.ReadAllBytes(fixture.Path(First)));
        Assert.Equal(database, workspace.Read(Database, Token)); Assert.Null(workspace.Read(Recipe, Token));
        Create(workspace, fixture.Tank, total);
        Assert.Equal(2, workspace.History.Count); workspace.Undo();
        Assert.Equal(overlay, workspace.Read(First, Token)); Assert.Equal(database, workspace.Read(Database, Token));
        Assert.Null(workspace.Read(Recipe, Token));
    }

    [Fact]
    public void CancellationAfterAReadStopsTheWalkAndRetryHasAFreshBudget()
    {
        byte[] root = Document("first.gltf"), child = Document();
        using CancellationTokenSource canceled = new(); int calls = 0;
        byte[] Read(string path, ProjectReadLimits limits)
        {
            calls++; byte[] bytes = path == Database ? root : child;
            limits.Validate(bytes); if (calls == 1) canceled.Cancel(); return bytes;
        }
        Assert.Throws<OperationCanceledException>(() => SourceTerrain.Referenced(Database, Read, canceled.Token, maximumInputBytes: root.Length + child.Length));
        Assert.Equal(1, calls);
        Assert.Contains(First, SourceTerrain.Referenced(Database, Read, Token, maximumInputBytes: root.Length + child.Length));
        Assert.Equal(3, calls);
    }

    [Fact]
    public void ThePerFileLimitStillRejectsALargeGlbBeforeAnyReadOrHash()
    {
        // A virtual length exercises the whole-container limit without constructing a large payload.
        using LengthOnlyStream stream = new(SourceTerrain.MaximumDatabaseJsonBytes + 4L);
        bool hashed = false; long offered = -1;
        byte[] Read(string path, ProjectReadLimits limits)
        {
            offered = limits.MaximumBytes;
            return SourceRead.AllAdmitted(stream, limits.MaximumBytes, path, limits.CheckPrefix, Token, _ => hashed = true);
        }
        Assert.Throws<InvalidDataException>(() => SourceTerrain.Referenced("data/large.glb", Read, Token));
        Assert.Equal(SourceTerrain.MaximumDatabaseJsonBytes, offered); Assert.False(hashed); Assert.Equal(0, stream.ReadCalls);
    }

    private static SourceTransaction Create(SourceWorkspace workspace, string model, long maximum) =>
        SourceTerrain.Create(workspace, Database, model, ["hull"], Recipe, Token, SourceProject.MaximumFiles, maximumReferenceInputBytes: maximum);

    private static void AssertClean(SourceWorkspace workspace)
    {
        Assert.False(workspace.IsDirty); Assert.False(workspace.CanUndo); Assert.False(workspace.CanRedo);
        Assert.Empty(workspace.History); Assert.Equal(0, workspace.Revision);
    }

    private static byte[] AddUnusedReferences(SourceWorldFixture fixture, params string[] references)
    {
        var root = JsonNode.Parse(File.ReadAllBytes(fixture.Path(Database)))!.AsObject();
        foreach (string reference in references) root["nodes"]!.AsArray().Add(Reference(reference));
        // Keep the existing explicit scene roots: these added records are intentionally not loaded.
        byte[] bytes = Encoding.UTF8.GetBytes(root.ToJsonString()); fixture.Write(Database, bytes); return bytes;
    }

    private static JsonObject Reference(string path) => new() { ["extras"] = new JsonObject { ["recoil"] = new JsonObject { ["ref"] = path } } };
    private static byte[] Document(params string[] references) => Encoding.UTF8.GetBytes(new JsonObject
    {
        ["asset"] = new JsonObject { ["version"] = "2.0" }, ["nodes"] = new JsonArray(references.Select(r => (JsonNode)Reference(r)).ToArray()),
        ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray() }), ["scene"] = 0,
    }.ToJsonString());

    private static byte[] Glb(byte[] json)
    {
        int padded = (json.Length + 3) & ~3;
        byte[] bytes = new byte[12 + 8 + padded + 8 + 64 + 8 + 4];
        void UInt(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at, 4), value);
        UInt(0, 0x46546C67); UInt(4, 2); UInt(8, (uint)bytes.Length);
        UInt(12, (uint)padded); UInt(16, 0x4E4F534A); bytes.AsSpan(20, padded).Fill(32); json.CopyTo(bytes, 20);
        int bin = 20 + padded; UInt(bin, 64); UInt(bin + 4, 0x004E4942);
        int unknown = bin + 8 + 64; UInt(unknown, 4); UInt(unknown + 4, 0x12345678);
        return bytes;
    }

    private sealed class DiskFiles(SourceWorldFixture fixture) : IProjectFiles
    {
        public HashSet<string> Reads { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Exists(string relative) => File.Exists(fixture.Path(relative));
        public byte[] Read(string relative, CancellationToken token, ProjectReadLimits limits)
        { Reads.Add(relative); return SourceRead.All(fixture.Path(relative), limits, token); }
    }

    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public int BytesRead { get; private set; }
        public override int Read(Span<byte> buffer) { int read = base.Read(buffer); BytesRead += read; return read; }
    }

    private sealed class LengthOnlyStream(long length) : Stream
    {
        public int ReadCalls { get; private set; }
        public override long Length => length;
        public override long Position { get; set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override int Read(byte[] buffer, int offset, int count) { ReadCalls++; throw new InvalidOperationException("No payload may be read."); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
    }
}
