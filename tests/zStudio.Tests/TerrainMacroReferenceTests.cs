using System.Text;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class TerrainMacroReferenceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Entry = "gamegen/m1.gs", Database = "data/m1/models/m1.gltf";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExecutedMacroTargetSurvivesConversionAndRebuild(bool sourced)
    {
        using SourceWorldFixture fixture = new(); fixture.WriteTerrainDatabase();
        string action = "FindNode %prefix%_a\nSetIntersectSurface off\n";
        Insert(fixture, "set prefix flat\n" + (sourced ? "source support/reference.gw\n" : action));
        if (sourced) fixture.Write("gamegen/support/reference.gw", action);
        SourceWorkspace workspace = new(fixture.Project);
        var before = Assemble(workspace);
        uint flags = Assert.Single(before.World.Nodes, n => n.Name == "flat_a").Flags;
        Assert.Empty(before.Warnings);
        // Deliberately omit the sourced child from this list: execution must discover it from the exact entry.
        var references = SourceTerrainConversion.References(workspace, [Entry, "data/m1/zrdr/gates.zad"], Entry, Token);
        Assert.Contains("flat_a", references.Names);
        var plan = SourceTerrainConversion.Plan(workspace, Database, references, Token);
        Assert.Equal(2, plan.Converted);
        Assert.Contains(plan.Kept, k => k.Node == "flat_a" && k.Reason == "named by a script, resource or animation");
        SourceTerrainConversion.Apply(workspace, plan, Token);
        var after = Assemble(workspace);
        Assert.Empty(after.Warnings);
        Assert.Equal(flags, Assert.Single(after.World.Nodes, n => n.Name == "flat_a").Flags);
    }

    [Fact]
    public void SharedMacroStateConditionsAndPostWriteSourcesUseInterpreterSemantics()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write(Entry, "set enabled TRUE\nset prefix flat\nsource shared.gw\nset prefix other\nsource shared.gw\nGameZWriteZBDFile world.zbd\nset prefix late\nsource shared.gw\nQuit\nset prefix ignored\nsource shared.gw\n");
        fixture.Write("gamegen/shared.gw", "ifdef enabled\nFindNode %prefix%_a\nendif\nifdef absent\nFindNode %prefix%_hidden\nendif\nFindNode %prefix%_*\nFindNode %undefined%_empty\n");
        SourceWorkspace workspace = new(fixture.Project);
        var references = SourceTerrainConversion.References(workspace, [Entry], Entry, Token);
        Assert.Contains("flat_a", references.Names);
        Assert.Contains("other_a", references.Names);
        Assert.Contains("late_a", references.Names);
        Assert.Contains("_empty", references.Names);
        Assert.DoesNotContain("flat_hidden", references.Names);
        Assert.DoesNotContain("ignored_a", references.Names);
        Assert.Contains(references.Patterns, p => p.IsMatch("flat_1"));
        Assert.Contains(references.Patterns, p => p.IsMatch("other_2"));
        Assert.Contains(references.Patterns, p => p.IsMatch("late_3"));
        Assert.False(workspace.IsDirty);
    }

    [Fact]
    public void RuntimeLoadEntryHasItsOwnMacroContextAndArchiveInventoryDoesNotExecute()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write(Entry, "set prefix compile\nsource shared.gw\nGameZWriteZBDFile world.zbd\n");
        fixture.Write("gamegen/m1_zbd.gs", "set prefix runtime\nsource shared.gw\n");
        fixture.Write("gamegen/shared.gw", "FindNode %prefix%_a\n");
        // Building interp.zbd captures every mission and support script, not just the current world's sources.
        fixture.Write("gamegen/m2.gs", "set prefix other\nsource shared.gw\nsource unavailable.gw\n");
        fixture.Write("gamegen/m2_zbd.gs", "set prefix other_runtime\nsource shared.gw\n");
        fixture.Write("gamegen/fragment.gw", "FindNode literal_protection\nFindNode %unknown%_fragment\n");
        SourceWorkspace workspace = new(fixture.Project);
        var references = SourceTerrainConversion.References(workspace,
            [Entry, "gamegen/m1_zbd.gs", "gamegen/shared.gw", "gamegen/m2.gs", "gamegen/m2_zbd.gs", "gamegen/fragment.gw"], Entry, Token);
        Assert.Contains("compile_a", references.Names); Assert.Contains("runtime_a", references.Names);
        Assert.Contains("literal_protection", references.Names);
        Assert.DoesNotContain("other_a", references.Names);
        Assert.DoesNotContain("other_runtime_a", references.Names);
        Assert.DoesNotContain("_fragment", references.Names);
        Assert.False(workspace.IsDirty);
    }

    [Fact]
    public void EntryAndDependencyPathSpellingsShareOneExecutionIdentity()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write(Entry, "set prefix flat\nsource child.gw\n");
        fixture.Write("gamegen/child.gw", "FindNode %prefix%_a\n");
        SourceWorkspace workspace = new(fixture.Project);
        var references = SourceTerrainConversion.References(workspace,
            ["GAMEGEN\\M1.GS", "gamegen\\child.gw", Entry], "GameGen\\m1.gs", Token);
        Assert.Contains("flat_a", references.Names);
        Assert.False(workspace.IsDirty);
    }

    [Theory]
    [InlineData("set prefix flat\nFindNode %prefix%_a\n")]
    [InlineData("source child.gw\n")]
    public void ScriptsNeedingAnExecutionContextRefuseTheLegacyIncompleteCall(string source)
    {
        using SourceWorldFixture fixture = new(); fixture.Write(Entry, source);
        SourceWorkspace workspace = new(fixture.Project);
        var error = Assert.Throws<InvalidDataException>(() => SourceTerrainConversion.References(workspace, [Entry], Token));
        Assert.Contains("entry script", error.Message);
        Assert.Contains("refused", error.Message);
        Assert.False(workspace.IsDirty);
    }

    [Fact]
    public void MissingOrUnreadableExecutedScriptsRefuseWithoutPartialProtection()
    {
        using SourceWorldFixture fixture = new(); fixture.Write(Entry, "set prefix flat\nsource child.gw\n");
        SourceWorkspace workspace = new(fixture.Project);
        Assert.Contains("unavailable", Assert.Throws<InvalidDataException>(() =>
            SourceTerrainConversion.References(workspace, [Entry], Entry, Token)).Message);
        fixture.Write("gamegen/child.gw", "FindNode %prefix%_a\n");
        using (FileStream held = new(fixture.Path("gamegen/child.gw"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.ThrowsAny<IOException>(() => SourceTerrainConversion.References(workspace, [Entry], Entry, Token));
        var references = SourceTerrainConversion.References(workspace, [Entry], Entry, Token);
        Assert.Contains("flat_a", references.Names);
        Assert.False(workspace.IsDirty); Assert.Equal(0, workspace.Revision); Assert.False(workspace.CanUndo);
    }

    [Fact]
    public void ExecutedOperandWorkIsBoundedEvenWhenNamesAreRepeated()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write(Entry, string.Concat(Enumerable.Repeat("source child.gw\n", 80)));
        fixture.Write("gamegen/child.gw", "FindNode " + new string('x', 1_000_000) + "\n");
        SourceWorkspace workspace = new(fixture.Project);
        var error = Assert.Throws<InvalidDataException>(() => SourceTerrainConversion.References(workspace, [Entry], Entry, Token));
        Assert.Contains("executed-operand work budget", error.Message);
        Assert.False(workspace.IsDirty);
    }

    [Fact]
    public void CancellationAndExpansionRefusalEscapeTheSharedTrace()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write(Entry, "set part " + new string('a', 600) + "\nFindNode %part%%part%\n");
        SourceWorkspace workspace = new(fixture.Project);
        Assert.Contains("macro expands", Assert.Throws<InvalidDataException>(() =>
            SourceTerrainConversion.References(workspace, [Entry], Entry, Token)).Message);
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => SourceTerrainConversion.References(workspace, [Entry], Entry, canceled.Token));
        Assert.False(workspace.IsDirty);
    }

    private static void Insert(SourceWorldFixture fixture, string commands) => fixture.Write(Entry,
        File.ReadAllText(fixture.Path(Entry)).Replace("GameZWriteZBDFile", commands + "GameZWriteZBDFile", StringComparison.Ordinal));

    private static WorldAssembler Assemble(SourceWorkspace workspace)
    {
        WorldAssembler assembler = new(new Files(workspace), Token);
        assembler.Assemble("m1.gs"); return assembler;
    }

    private sealed class Files(SourceWorkspace workspace) : IProjectFiles
    {
        public bool Exists(string relative) => workspace.Exists(relative);
        public byte[] Read(string relative, CancellationToken token) => workspace.Read(relative, token) ?? throw new FileNotFoundException(relative);
    }
}
