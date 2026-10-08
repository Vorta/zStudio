using System.IO;
using System.Buffers.Binary;
using System.Text;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class WorldLookupWorkTests
{
    private sealed class Files(string script) : IProjectFiles
    {
        private readonly byte[] bytes = Encoding.ASCII.GetBytes(script);
        public bool Exists(string relative) => relative == "gamegen/m1.gs";
        public byte[] Read(string relative, CancellationToken token) => Read(relative, token, ProjectReadLimits.Document);
        public byte[] Read(string relative, CancellationToken token, ProjectReadLimits limits) { token.ThrowIfCancellationRequested(); byte[] result = bytes; limits.Validate(result); return result; }
    }
    private static string Finish(string text) => text + "\nGameZWriteZBDFile ../m1/gamez.zbd\n";
    private static string Nodes(int count) => "NewWorld world\n" + string.Concat(Enumerable.Range(0, count).Select(i => $"NewObject3D node{i}\n"));

    [Theory]
    [InlineData("absent")]
    [InlineData("world")]
    public void SuccessfulAndFailedLookupsConsumeOneAggregateAllowance(string name)
    {
        string script = Finish(Nodes(100) + string.Concat(Enumerable.Repeat($"FindNode {name}\n", 3)));
        var assembler = new WorldAssembler(new Files(script)) { LookupWorkLimit = 202 };
        Assert.Contains("node lookup work", Assert.Throws<InvalidDataException>(() => assembler.Assemble("m1.gs")).Message);
        var good = new WorldAssembler(new Files(Finish("NewWorld good"))) { LookupWorkLimit = 0 };
        Assert.Equal("good", Assert.Single(good.Assemble("m1.gs").Nodes).Name);
    }

    [Fact]
    public void RepeatedComparisonsDoNotDecodeAndAllocateEachStoredNameAgain()
    {
        Files files = new(Finish(Nodes(1000) + string.Concat(Enumerable.Repeat("FindNode absent\n", 1000))));
        long before = GC.GetAllocatedBytesForCurrentThread();
        var world = new WorldAssembler(files).Assemble("m1.gs");
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(1001, world.Nodes.Count);
        Assert.InRange(allocated, 0, 12L << 20);
    }

    [Fact]
    public void RenameDeleteAndSlotReusePreserveNewestLiveNameAndTypedLookupIdentity()
    {
        var world = new WorldAssembler(new Files(Finish("""
            NewWorld world
            NewObject3D same
            NewObject3D second
            NodeSetDescription same
            FindNode same
            Object3DTranslate 2 0 0
            NodeSetDescription renamed
            FindNode same
            Object3DTranslate 1 0 0
            DeleteTree renamed
            NewObject3D same
            Object3DTranslate 3 0 0
            FindNode same
            Object3DTranslate 4 0 0
            NewObject3D world
            NewCamera camera
            CameraSetWorld world
            """))).Assemble("m1.gs");
        Assert.Equal(new[] { 1f, 4f }, world.Nodes.Where(n => n.Name == "same").Select(n => n.PayloadFloat(0x54)).Order());
        Assert.Same(world.Nodes.Single(n => n.Class == WorldNodeClass.World), world.Nodes.Single(n => n.Class == WorldNodeClass.Camera).CameraWorld);
        Assert.DoesNotContain(world.Nodes, n => n.Name == "renamed");
    }

    [Fact]
    public void SubtreeLookupsPreserveBothRequiredOrdersAndShareWorkAcrossCalls()
    {
        WorldNode root = new("root", WorldNodeClass.Object3D), first = new("duplicate", WorldNodeClass.Object3D), last = new("duplicate", WorldNodeClass.Object3D);
        root.Children.AddRange([first, last]);
        LookupWorkBudget budget = new(19);
        Assert.Same(last, budget.FindSub(root, "duplicate"));
        Assert.Same(first, budget.FindSub(root, "duplicate", firstChildFirst: true));
        Assert.Contains("node lookup work", Assert.Throws<InvalidDataException>(() => budget.FindSub(root, "absent")).Message);
    }

    [Fact]
    public void SubtreeWorkIsReservedBeforePushingChildrenAndObservesCancellation()
    {
        WorldNode root = new("root", WorldNodeClass.Object3D), child = new("child", WorldNodeClass.Object3D);
        root.Children.AddRange(Enumerable.Repeat(child, 100_000));
        LookupWorkBudget budget = new(2);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => budget.FindSub(root, "absent"));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 32_768);
        using CancellationTokenSource cancellation = new();
        LookupWorkBudget canceled = new(token: cancellation.Token);
        using var walk = canceled.Subtree([root]).GetEnumerator();
        Assert.True(walk.MoveNext());
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => walk.MoveNext());
    }

    [Fact]
    public void ActualFindSubNodeCommandsCannotResetWorkByChangingTheCurrentNode()
    {
        string script = "NewWorld world\nNewObject3D root\nNewObject3D child\nFindNode root\nAddChild child\n"
            + string.Concat(Enumerable.Repeat("FindNode root\nFindSubNode absent\n", 20));
        var assembler = new WorldAssembler(new Files(Finish(script))) { LookupWorkLimit = 30 };
        Assert.Contains("node lookup work", Assert.Throws<InvalidDataException>(() => assembler.Assemble("m1.gs")).Message);
    }

    [Fact]
    public void LookupSourceBytesAreChargedBeforeDecodingEvenWhenScriptsContainOnlyComments()
    {
        byte[] root = Encoding.ASCII.GetBytes("source a.gw\nsource b.gw\n");
        byte[] comment = Encoding.ASCII.GetBytes("#" + new string('x', 1_000_000));
        int reads = 0;
        byte[] Read(string name) { reads++; return name == "gamegen/m1_zbd.gs" ? root : comment; }
        long before = GC.GetAllocatedBytesForCurrentThread();
        var error = Assert.Throws<InvalidDataException>(() => WorldLookups.FindNodes(Read, "m1", TestContext.Current.CancellationToken,
            root.Length + comment.Length, 100));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Contains("source limit", error.Message);
        Assert.Equal(3, reads);
        Assert.InRange(allocated, 0, 3L << 20); // One decoded comment, never the rejected second one.
    }

    [Fact]
    public void LookupSourceTokensShareOneBudgetAndRepeatedSourcesAreChargedOnce()
    {
        Dictionary<string, byte[]> files = new()
        {
            ["gamegen/m1_zbd.gs"] = Encoding.ASCII.GetBytes("source a.gw\nsource a.gw\nsource b.gw\n"),
            ["gamegen/a.gw"] = Encoding.ASCII.GetBytes("FindNode first\n"),
            ["gamegen/b.gw"] = Encoding.ASCII.GetBytes("FindNode second\n")
        };
        byte[]? Read(string name) => files.GetValueOrDefault(name);
        var found = WorldLookups.FindNodes(Read, "m1", TestContext.Current.CancellationToken, 1000, 10);
        Assert.Equal(new[] { "first", "second" }, found.Select(p => p.Name));
        Assert.Contains("tokens", Assert.Throws<InvalidDataException>(() => WorldLookups.FindNodes(Read, "m1", TestContext.Current.CancellationToken, 1000, 9)).Message);
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => WorldLookups.FindNodes(Read, "m1", canceled.Token, 1000, 10));
    }

    [Fact]
    public void PreviousWorldComparisonRefusalIsReportedSeparatelyFromSuccessfulCurrentLookups()
    {
        using SourceWorldFixture fixture = new();
        var token = TestContext.Current.CancellationToken;
        string destination = Path.Combine(fixture.Root, "previous");
        Directory.CreateDirectory(Path.Combine(destination, "m1"));
        GameZWorld previous = new();
        WorldNode root = new("world", WorldNodeClass.World); previous.Nodes.Add(root);
        for (int i = 0; i < 10; i++)
        {
            WorldNode child = new("child" + i, WorldNodeClass.Object3D);
            previous.Nodes.Add(child); root.Children.Add(child); child.Parents.Add(root);
        }
        File.WriteAllBytes(Path.Combine(destination, "m1", "gamez.zbd"), GameZWriter.Write(previous, token));
        byte[] prefix = new byte[72];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, 0x08170616);
        BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(4), 28);
        AnimationPackage package = new() { Prefix = prefix, Tail = [] };
        for (int i = 0; i < 31; i++)
        {
            AnimationEntry entry = new(new byte[308], i, 72);
            entry.SetText(0, "entry" + i); entry.SetText(32, "world"); entry.SetText(68, "absent");
            package.Entries.Add(entry);
        }
        byte[] animationBytes = AnimationWriter.Write(package, token);
        File.WriteAllBytes(Path.Combine(destination, "m1", "anim.zbd"), animationBytes);
        _ = AnimationPackage.Read(animationBytes, token); // The replaced animation must really be readable.
        SourceBuilder.Snapshot snapshot = new(fixture.Project);
        SourceExportResult[] outputs = [new("m1/gamez.zbd", "world", "built", 0, 0, [])];
        Dictionary<string, AnimationPackage> packages = new() { ["m1"] = package };
        var limited = SourceBuilder.MissionLookups(outputs, packages, snapshot, destination, token, lookupWorkLimit: 512);
        string notice = Assert.Single(limited.NotChecked);
        Assert.Contains("comparison with the previous m1 world was not completed", notice);
        Assert.Contains("node lookup work", notice);
        Assert.DoesNotContain("none of them is reported", notice);
        var complete = SourceBuilder.MissionLookups(outputs, packages, snapshot, destination, token);
        Assert.Empty(complete.NotChecked);
    }
}
