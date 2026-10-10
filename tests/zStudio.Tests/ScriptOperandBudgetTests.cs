using System.Text;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ScriptOperandBudgetTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static IReadOnlyList<IReadOnlyList<string>> Lines(string text) => GameGenScriptText.Tokenize(text);

    [Fact]
    public void WideCachedInstructionsRefuseBeforeAllocatingTheirArgumentArrays()
    {
        IReadOnlyList<IReadOnlyList<string>> wide = [new[] { "echo" }.Concat(Enumerable.Repeat("x", 100_000)).ToArray()];
        ScriptTraceBudget budget = new(maximumOperandReferences: 1000);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Contains("executed operand", Assert.Throws<InvalidDataException>(() => ScriptTrace.Trace(_ => wide, "m1.gs", [], budget, token: Token)).Message);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 30_000);
        Assert.True(budget.Exhausted); Assert.Equal(0, budget.Operands.References);
    }

    [Fact]
    public void RepeatedCachedArgumentsHaveAnAggregateDefaultLimit()
    {
        var wide = Lines("echo " + string.Join(' ', Enumerable.Repeat("x", 10_000)));
        var entry = Lines(string.Concat(Enumerable.Repeat("source wide.gw\n", 500)));
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Contains("executed operand", Assert.Throws<InvalidDataException>(() => ScriptTrace.Trace(
            name => name == "main.gs" ? entry : wide, "main.gs", [])).Message);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 36L << 20);
    }

    [Fact]
    public void SeparateTracesShareOperandReferencesAndKeepFullLiteralIdentity()
    {
        string literal = new('z', 10_000);
        var lines = Lines("echo " + literal);
        ScriptTraceBudget budget = new(maximumOperandReferences: 25);
        Assert.Equal(literal, Assert.Single(ScriptTrace.Trace(_ => lines, "one.gs", [], budget, token: Token)).Args.Single());
        Assert.Throws<InvalidDataException>(() => ScriptTrace.Trace(_ => lines, "two.gs", [], budget, token: Token));
        Assert.True(budget.Exhausted);
        Assert.Single(ScriptTrace.Trace(_ => lines, "fresh.gs", []));
    }

    [Fact]
    public void MacroTextIsChargedBeforeBuildersAndOversizedValuesBeforeAppend()
    {
        var lines = Lines("set value abc\necho %value%\necho %value%");
        ScriptTraceBudget budget = new(maximumOperandCharacters: 75);
        Assert.Throws<InvalidDataException>(() => ScriptTrace.Trace(_ => lines, "m1.gs", [], budget, token: Token));
        Assert.True(budget.Exhausted);
        string huge = new('v', 1_000_000);
        IReadOnlyDictionary<string, string> macros = new Dictionary<string, string> { ["value"] = huge };
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Contains("past 1023", Assert.Throws<InvalidDataException>(() => ScriptConditions.Expand("%value%", macros)).Message);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 20_000);
    }

    [Fact]
    public void AssemblyAccountsForSourcedAndPostWriteArgumentsBeforeProvenanceGrowth()
    {
        var files = new Files(new()
        {
            ["gamegen/m1.gs"] = "NewWorld world\nsource child.gw\nGameZWriteZBDFile gamez.zbd\nsource child.gw",
            ["gamegen/child.gw"] = "echo one two three four five",
        });
        var assembler = new WorldAssembler(files, Token) { OperandReferenceLimit = 60 };
        Assert.Contains("executed operand", Assert.Throws<InvalidDataException>(() => assembler.Assemble("m1.gs")).Message);
        Assert.Single(assembler.World.Nodes);
        Assert.Single(new WorldAssembler(new Files(new() { ["gamegen/good.gs"] = "NewWorld good\nGameZWriteZBDFile gamez.zbd" }), Token).Assemble("good.gs").Nodes);
    }

    [Fact]
    public void ReconstructionAndOtherMissionChecksCannotResetOrSwallowOperandCapacity()
    {
        GameZWorld world = new(); world.Nodes.Add(new("world", WorldNodeClass.World));
        Assert.Contains("executed operand", Assert.Throws<InvalidDataException>(() => WorldSources.Reconstruct([new(1, world), new(2, world)],
            _ => Lines("echo x"), (_, _) => null, new HashSet<string>(), _ => 0, [], Token, maximumOperandReferences: 25)).Message);
        using SourceWorldFixture fixture = new();
        fixture.Write("gamegen/m2.gs", "echo x"); fixture.Write("gamegen/m3.gs", "echo x");
        ScriptTraceBudget budget = new(maximumOperandReferences: 25);
        Assert.Contains("executed operand", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.TransformElsewhere(new(fixture.Project),
            "m1", new() { ModelFile = fixture.Tank }, "hull", Token, budget)).Message);
        Assert.True(budget.Exhausted);
    }

    [Fact]
    public void CheckoutPropagatesSharedOperandRefusalWithoutWritingOrChangingSources()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write("gamegen/m1.gs", "echo x"); fixture.Write("gamegen/m2.gs", "echo x");
        const string model = "data/m1/models/m1.gltf";
        byte[] original = File.ReadAllBytes(fixture.Path(model));
        List<string> reads = [];
        Assert.Contains("executed operand", Assert.Throws<InvalidDataException>(() => SourceBlender.Checkout(new(fixture.Project), model, Token,
            reads.Add, maximumOperandReferences: 25)).Message);
        Assert.Contains("gamegen/m1.gs", reads); Assert.Contains("gamegen/m2.gs", reads);
        Assert.Equal(original, File.ReadAllBytes(fixture.Path(model)));
        Assert.False(Directory.Exists(fixture.Path(SourceBlender.ExportFolder)));
    }

    [Fact]
    public void CancellationAfterLoadingCachedInstructionsPreventsTraceAllocation()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var lines = Lines("echo x"); ScriptTraceBudget budget = new();
        Assert.ThrowsAny<OperationCanceledException>(() => ScriptTrace.Trace(_ => { cancel.Cancel(); return lines; }, "m1.gs", [], budget, token: cancel.Token));
        Assert.Equal(0, budget.Operands.References);
    }

    private sealed class Files(Dictionary<string, string> texts) : IProjectFiles
    {
        private readonly Dictionary<string, byte[]> ownedInputs = texts.ToDictionary(p => p.Key, p => Encoding.ASCII.GetBytes(p.Value), texts.Comparer);
        public bool Exists(string relative) => texts.ContainsKey(relative);
        public byte[] Read(string relative, CancellationToken token) => Read(relative, token, ProjectReadLimits.Document);
        public byte[] Read(string relative, CancellationToken token, ProjectReadLimits limits) { token.ThrowIfCancellationRequested(); byte[] result = ownedInputs[relative]; limits.Validate(result); return result; }
    }
}
