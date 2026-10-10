using System.IO;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ScriptTraceBudgetTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static IReadOnlyList<IReadOnlyList<string>> Lines(string text) => GameGenScriptText.Tokenize(text);

    [Fact]
    public void ModelAndTextureHistoryShareTheExactBudgetBoundary()
    {
        var lines = Lines("SetModelDirectory ../data/a\nSetTextureDirectory ../data/t\nSetModelDirectory ../data/b");
        ScriptTraceBudget exact = new(16);
        var trace = ScriptTrace.Trace(_ => lines, "m1.gs", [], exact, token: Token);
        Assert.Equal(16, exact.UsedUnits);
        Assert.Equal(["data/a"], trace[0].ModelDirectories);
        Assert.Empty(trace[0].TextureDirectories);
        Assert.Equal(["data/t"], trace[1].TextureDirectories);
        Assert.Equal(["data/b", "data/a"], trace[2].ModelDirectories);
        ScriptTraceBudget shortBudget = new(15);
        Assert.Contains("directory history", Assert.Throws<InvalidDataException>(() => ScriptTrace.Trace(_ => lines, "m1.gs", [], shortBudget, token: Token)).Message);
        Assert.True(shortBudget.Exhausted);
        Assert.Equal(10, shortBudget.UsedUnits); // The rejected copy was never reserved.
    }

    [Fact]
    public void RepeatingAnUnchangedDirectoryOrderReusesHistoryButRetainsScriptOwnership()
    {
        Dictionary<string, IReadOnlyList<IReadOnlyList<string>>> scripts = new()
        {
            ["m1.gs"] = Lines("SetModelDirectory ../data/a;../data/b\nsource child.gs\nNewObject3D after"),
            ["child.gs"] = Lines("NewObject3D before\nSetModelDirectory ../data/a;../data/b\nSetTextureDirectory outside-project\nNewObject3D after")
        };
        ScriptTraceBudget budget = new(6);
        var trace = ScriptTrace.Trace(n => scripts.GetValueOrDefault(n), "m1.gs", [], budget, token: Token);
        Assert.Equal(6, budget.UsedUnits);
        Assert.All(trace, step => Assert.Same(trace[0].ModelDirectories, step.ModelDirectories));
        Assert.Null(trace[1].ScriptModelDirectory);
        Assert.Equal("data/b", trace[2].ScriptModelDirectory);
        Assert.Equal("data/b", trace[^1].ScriptModelDirectory);
        Assert.All(trace, step => Assert.Empty(step.TextureDirectories));
    }

    [Theory]
    [InlineData("SetModelDirectory")]
    [InlineData("SetTextureDirectory")]
    public void ReorderingRetainsEarlierViewsAndChargesNewHistory(string command)
    {
        var lines = Lines($"{command} ../data/a;../data/b\n{command} ../data/a");
        ScriptTraceBudget budget = new(12);
        var trace = ScriptTrace.Trace(_ => lines, "m1.gs", [], budget, token: Token);
        IReadOnlyList<string> View(int index) => command == "SetModelDirectory" ? trace[index].ModelDirectories : trace[index].TextureDirectories;
        Assert.Equal(["data/b", "data/a"], View(0));
        Assert.Equal(["data/a", "data/b"], View(1));
        Assert.NotSame(View(0), View(1));
        Assert.Equal(12, budget.UsedUnits);
    }

    [Fact]
    public void NestedSourcesAndSeparateMissionTracesShareTheirOperationAllowance()
    {
        Dictionary<string, IReadOnlyList<IReadOnlyList<string>>> scripts = new()
        {
            ["m1.gs"] = Lines("SetModelDirectory ../data/a\nsource child.gs"),
            ["child.gs"] = Lines("SetTextureDirectory ../data/t")
        };
        ScriptTraceBudget budget = new(15);
        Assert.Equal(2, ScriptTrace.Trace(n => scripts.GetValueOrDefault(n), "m1.gs", [], budget, token: Token).Count);
        Assert.Throws<InvalidDataException>(() => ScriptTrace.Trace(n => scripts.GetValueOrDefault(n), "m1.gs", [], budget, token: Token));
        Assert.Equal(15, budget.UsedUnits);
        Assert.True(budget.Exhausted);
    }

    [Theory]
    [InlineData("SetModelDirectory")]
    [InlineData("SetTextureDirectory")]
    public void ThousandsOfDistinctDirectoriesRefuseBeforeQuadraticStorageEscapesItsAllowance(string command)
    {
        var small = Lines(string.Join('\n', Enumerable.Range(0, 2000).Select(i => $"{command} ../data/d{i}")));
        var large = Lines(string.Join('\n', Enumerable.Range(0, 4000).Select(i => $"{command} ../data/d{i}")));
        Assert.Equal(2000, ScriptTrace.Trace(_ => small, "small.gs", []).Count);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var error = Assert.Throws<InvalidDataException>(() => ScriptTrace.Trace(_ => large, "large.gs", []));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Contains("directory", error.Message);
        // Fixture/token allocation is excluded. Historical copies use at most 32 MiB on x64;
        // normalization, instruction records and arguments must not restore the former ~67 MiB growth.
        Assert.InRange(allocated, 0, 48L << 20);
    }

    [Fact]
    public void ReconstructionSharesDirectoryHistoryAcrossMissions()
    {
        GameZWorld world = new();
        world.Nodes.Add(new("world", WorldNodeClass.World));
        var lines = Lines("SetModelDirectory ../data/a\nNewWorld world");
        var error = Assert.Throws<InvalidDataException>(() => WorldSources.Reconstruct([new(1, world), new(2, world)],
            _ => lines, (_, _) => null, new HashSet<string>(), _ => 0, [], Token, maximumTraceUnits: 9));
        Assert.Contains("directory history", error.Message);
    }

    [Fact]
    public void CheckoutRefusesSharedDirectoryHistoryInsteadOfSkippingAMission()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write("gamegen/m1.gs", "SetModelDirectory ../data/m1/models\n");
        fixture.Write("gamegen/m2.gs", "SetTextureDirectory ../data/m2/textures\n");
        SourceWorkspace workspace = new(fixture.Project);
        const string model = "data/m1/models/m1.gltf";
        byte[] original = File.ReadAllBytes(fixture.Path(model));
        List<string> reads = [];
        var error = Assert.Throws<InvalidDataException>(() => SourceBlender.Checkout(workspace, model, Token, reads.Add, maximumTraceUnits: 9));
        Assert.Contains("directory history", error.Message);
        Assert.Contains("gamegen/m1.gs", reads);
        Assert.Contains("gamegen/m2.gs", reads);
        Assert.False(Directory.Exists(fixture.Path(SourceBlender.ExportFolder)));
        Assert.Equal(original, File.ReadAllBytes(fixture.Path(model)));
        Assert.Empty(SourceBlender.Checkouts(fixture.Project, Token));
    }

    [Theory]
    [InlineData("SetModelDirectory")]
    [InlineData("SetTextureDirectory")]
    public void OneLargeDirectoryOperandCannotEvadeTheAggregateWorkBudget(string command)
    {
        string operand = string.Join(';', Enumerable.Range(0, 32_000).Select(i => $"../data/d{i}"));
        IReadOnlyList<IReadOnlyList<string>> lines = [new[] { command, operand }];
        ScriptTraceBudget budget = new();
        var error = Assert.Throws<InvalidDataException>(() => ScriptTrace.Trace(_ => lines, "m1.gs", [], budget, token: Token));
        Assert.Contains("directory search-path work", error.Message);
        Assert.True(budget.Work.Exhausted);
        Assert.True(budget.Exhausted);
        Assert.Equal(0, budget.UsedUnits); // Refused before publishing even one history array.
    }

    [Fact]
    public void DirectoryWorkIsReservedBeforeSplittingAndSharedAcrossSources()
    {
        string oversized = new(';', 1_000_000);
        ScriptTraceBudget noWork = new(maximumWorkUnits: 0);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => ScriptTrace.Trace(_ => [new[] { "SetModelDirectory", oversized }], "m1.gs", [], noWork, token: Token));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 32_768);
        Assert.Equal(0, noWork.Work.UsedUnits);

        var lines = Lines("SetModelDirectory outside-project\n");
        ScriptTraceBudget shared = new(maximumWorkUnits: 100);
        ScriptTrace.Trace(_ => lines, "first.gs", [], shared, token: Token);
        Assert.Throws<InvalidDataException>(() => ScriptTrace.Trace(_ => lines, "second.gs", [], shared, token: Token));
        Assert.True(shared.Exhausted);
    }

    [Fact]
    public void CheckoutPropagatesDirectoryWorkRefusalBeforePublishingFiles()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write("gamegen/m1.gs", "SetModelDirectory " + string.Join(';', Enumerable.Range(0, 32_000).Select(i => $"../data/d{i}")));
        var error = Assert.Throws<InvalidDataException>(() => SourceBlender.Checkout(new(fixture.Project), "data/m1/models/m1.gltf", Token));
        Assert.Contains("directory search-path work", error.Message);
        Assert.False(Directory.Exists(fixture.Path(SourceBlender.ExportFolder)));
    }

    [Fact]
    public void MissingScriptNotesBoundOperandsBeforeFormattingAcrossMissionTraces()
    {
        // Keep the diagnostic-cap fixture below the separate executed-operand work allowance. Each operand still
        // greatly exceeds the displayed representation, so this exercises pre-formatting bounds and deduplication.
        string missing = new('x', 10_000);
        IReadOnlyList<IReadOnlyList<string>> lines = [.. Enumerable.Repeat<IReadOnlyList<string>>(new[] { "source", missing }, 100)];
        List<string> notes = [];
        BoundedDiagnostics diagnostics = new(notes);
        ScriptTraceBudget budget = new();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 12; i++)
            ScriptTrace.Trace(n => n == "main.gs" ? lines : null, "main.gs", notes, budget, diagnostics, Token);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, 8L << 20);
        Assert.Equal(2, notes.Count);
        Assert.Contains("Script", notes[0]);
        Assert.True(notes[0].Length <= BoundedDiagnostics.MaximumMessageCharacters);
        Assert.Equal(BoundedDiagnostics.OmissionNotice, notes[1]);
    }

    [Fact]
    public void DecompositionMissingRootsUseTheSameBoundedWarningCollector()
    {
        string authored = new('x', 1_000_000);
        GameZWorld world = new();
        world.Nodes.Add(new("world", WorldNodeClass.World));
        TracedInstruction missing = new("m1.gs", "LoadGameGen", [authored, authored], [], null, []);
        TracedInstruction[] trace = [.. Enumerable.Repeat(missing, 100)];
        List<string> notes = [];
        BoundedDiagnostics diagnostics = new(notes);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Empty(WorldDecomposer.DecomposeAll(world, trace, notes, Token, diagnostics).Loads);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 2L << 20);
        Assert.Single(notes);
        Assert.True(notes[0].Length <= BoundedDiagnostics.MaximumMessageCharacters);
    }
}
