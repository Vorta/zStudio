using System.Text;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class AnimationDefinitionRepairBudgetTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Root = "data/m1/zrdr/anim.zad";
    private sealed class Files(Dictionary<string, byte[]> data) : IProjectFiles
    {
        public bool Exists(string relative) => data.ContainsKey(relative);
        public byte[] Read(string relative, CancellationToken token) => Read(relative, token, ProjectReadLimits.Document);
        public byte[] Read(string relative, CancellationToken token, ProjectReadLimits limits) { token.ThrowIfCancellationRequested(); byte[] bytes = data[relative]; limits.Validate(bytes); return bytes; }
    }
    private static byte[] Source(int events) => Encoding.ASCII.GetBytes(
        "ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION ( NAME ( gate ) SEQUENCE_DEFINITION ( NAME ( s ) "
        + string.Concat(Enumerable.Repeat("ANIM_VERBOSE ( OFF ) ", events)) + ") ) ) )");
    private static AnimationPackage Package(int events)
    {
        var compiled = AnimationCompiler.Compile(new Files(new() { [Root] = Source(events) }), Root, ["gate"], Token);
        // The counterexample is an admitted binary package, not an artificial unbounded entry collection.
        return AnimationPackage.Read(compiled.Bytes, Token);
    }

    [Fact]
    public void AdmittedVerboseEventsRefuseBeforeAllocatingTheDefinitionGraph()
    {
        var package = Package(100);
        AnimationDefinitionBudget budget = new(Token, maximumNodes: 100);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var error = Assert.Throws<IOException>(() => AnimationDecompiler.Definition(package.Entries[1], _ => throw new InvalidOperationException(), budget));
        Assert.Contains("syntax graph limit", error.Message);
        Assert.Equal(0, budget.UsedNodes);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 64 * 1024);
    }

    [Fact]
    public void ActualRepairPropagatesCapacityRefusalAndLeavesSourcesAndPackageUntouched()
    {
        var package = Package(100);
        byte[] source = Source(0), originalPackage = AnimationWriter.Write(package, Token);
        Files files = new(new() { [Root] = source }); List<string> notes = [];
        var error = Assert.Throws<IOException>(() => AnimationSources.Reconstruct([new(1, package, [], ["gate"])], files, notes, Token, maximumDefinitionNodes: 100));
        Assert.Contains("syntax graph limit", error.Message);
        Assert.Empty(notes); // A capacity failure cannot be downgraded to "kept as shipped".
        Assert.Equal(source, files.Read(Root, Token));
        Assert.Equal(originalPackage, AnimationWriter.Write(package, Token));
    }

    [Fact]
    public void GeneralizedCopiesAndReplacementWrappersShareTheCandidateAllowance()
    {
        var package = Package(4);
        AnimationDefinitionBudget measured = new(Token);
        _ = AnimationDecompiler.Definition(package.Entries[1], _ => throw new InvalidOperationException(), measured);
        var error = Assert.Throws<IOException>(() => AnimationSources.Reconstruct([new(1, package, [], ["gate"])],
            new Files(new() { [Root] = Source(0) }), [], Token, maximumDefinitionNodes: measured.UsedNodes));
        Assert.Contains("syntax graph limit", error.Message);

        // A wrapper is separately budgeted before its child list is copied, even with an already-built candidate.
        var root = ZrdText.Parse(Source(0), Token);
        AnimationDefinitionBudget wrapperBudget = new(Token, _ => throw new IOException("wrapper reservation"));
        Assert.Contains("wrapper reservation", Assert.Throws<IOException>(() => AnimationDefinitionSet.ReplaceDefinition(root, 0, root, wrapperBudget)).Message);
    }

    [Fact]
    public void SuccessfulRepairStillCompilesExactlyAndSharesItsBudgetAcrossMissions()
    {
        var package = Package(4);
        var data = new Dictionary<string, byte[]> { [Root] = Source(0), ["data/m2/zrdr/anim.zad"] = Source(0) };
        Files files = new(data); List<string> notes = [];
        var output = Assert.Single(AnimationSources.Reconstruct([new(1, package, [], ["gate"])], files, notes, Token, maximumDefinitionNodes: 60));
        Assert.Equal(Root, output.Path);
        Assert.Contains("was rebuilt from anim.zbd", Assert.Single(notes), StringComparison.Ordinal);
        var repaired = AnimationCompiler.Compile(new Files(new() { [Root] = output.Bytes }), Root, ["gate"], Token).Package;
        Assert.Null(AnimationComparer.Difference(package.Entries[1], repaired.Entries[1], Token));
        Assert.Contains("syntax graph limit", Assert.Throws<IOException>(() => AnimationSources.Reconstruct(
            [new(1, package, [], ["gate"]), new(2, package, [], ["gate"])], files, [], Token, maximumDefinitionNodes: 60)).Message);
        Assert.Equal(Source(0), data[Root]); Assert.Equal(Source(0), data["data/m2/zrdr/anim.zad"]);
    }

    [Fact]
    public void GraphStorageDebitsTheSharedReconstructionMemoryBudget()
    {
        var package = Package(4);
        Assert.Contains("memory limit", Assert.Throws<IOException>(() => AnimationSources.Reconstruct([new(1, package, [], ["gate"])],
            new Files(new() { [Root] = Source(0) }), [], Token, maximumRetainedBytes: 100)).Message);
    }

    [Fact]
    public void CancellationDuringAdmissionStopsConstructionAndDoesNotPoisonRetry()
    {
        var entry = Package(4).Entries[1]; using CancellationTokenSource stop = new();
        int reservations = 0;
        AnimationDefinitionBudget budget = new(stop.Token, _ => { if (++reservations == 8) stop.Cancel(); });
        var error = Assert.Throws<OperationCanceledException>(() => AnimationDecompiler.Definition(entry, _ => throw new InvalidOperationException(), budget));
        Assert.Equal(stop.Token, error.CancellationToken); Assert.Equal(7, budget.UsedNodes);
        Assert.NotNull(AnimationDecompiler.Definition(entry, _ => throw new InvalidOperationException(), Token));
        Assert.Throws<OperationCanceledException>(() => AnimationDefinitionSet.ReplaceDefinition(ZrdText.Parse(Source(0), Token), 0,
            ZrdText.Parse(Source(0), Token), stop.Token));
    }

    [Fact]
    public void EventCountDiagnosticIsBoundedAndPreservesExactCountsAndSemanticComparison()
    {
        var expected = Package(20_000).Entries[1]; var actual = Package(0).Entries[1];
        _ = AnimationComparer.Difference(expected, actual, Token);
        long before = GC.GetAllocatedBytesForCurrentThread();
        string difference = Assert.IsType<string>(AnimationComparer.Difference(expected, actual, Token));
        Assert.Equal("sequence s event count 20000|0", difference);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 16 * 1024);
        var equal = Package(2).Entries[1]; var changed = equal.Clone(Token);
        Assert.Null(AnimationComparer.Difference(equal, changed, Token));
        // The catalog marks opcode39's opaque payload read-only; comparison intentionally excludes it.
        changed.Sequences[0].Events[1].SetInt(12, 1);
        Assert.Null(AnimationComparer.Difference(equal, changed, Token));
        changed.Sequences[0].Events[1].Threshold = 1;
        Assert.Equal(0, equal.Sequences[0].Events[1].Threshold);
        Assert.Contains("event 1: timing", Assert.IsType<string>(AnimationComparer.Difference(equal, changed, Token)));
        Assert.Throws<OperationCanceledException>(() => AnimationComparer.Difference(expected, actual, new CancellationToken(true)));
    }
}
