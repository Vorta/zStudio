using System.IO;
using System.Text;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class SourceObjectDiagnosticBudgetTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private sealed class MemoryFiles(Dictionary<string, byte[]> files) : IProjectFiles
    {
        public bool Exists(string relative) => files.ContainsKey(relative);
        public byte[] Read(string relative, CancellationToken token) => Read(relative, token, ProjectReadLimits.Document);
        public byte[] Read(string relative, CancellationToken token, ProjectReadLimits limits) { token.ThrowIfCancellationRequested(); byte[] result = files[relative]; limits.Validate(result); return result; }
    }

    [Fact]
    public void RepeatedLongFailedAttachmentsDoNotExpandNotesBeforeRepeatedLineRefusal()
    {
        using SourceWorldFixture fixture = new();
        string operand = new('x', 1_000_000);
        // Keep full million-character operands while remaining within the assembler's aggregate execution budget.
        // Fifty unbounded interpolations would still allocate about 100 MB inside PlanDelete alone.
        const int repeats = 50;
        var assembler = new WorldAssembler(new MemoryFiles(new()
        {
            ["gamegen/m1.gs"] = Encoding.Latin1.GetBytes("NewObject3D parent\n" + string.Concat(Enumerable.Repeat("source repeated.gs\n", repeats)) + "GameZWriteZBDFile world.zbd\n"),
            ["gamegen/repeated.gs"] = Encoding.Latin1.GetBytes("AddChild " + operand + "\n"),
        }), Token);
        var world = assembler.Assemble("m1.gs");
        var parent = Assert.Single(world.Nodes);
        Assert.Equal(repeats, assembler.Provenance[parent].Applied.Count);
        Assert.All(assembler.Provenance[parent].Applied, instruction => Assert.Equal(operand, Assert.Single(instruction.Args)));
        SourceObjectTarget target = new(new(fixture.Project), "m1", world, parent, assembler.Provenance, assembler.Executions);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var error = Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanDelete(target, Token));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Contains("runs 50 times", error.Message);
        Assert.InRange(allocated, 0, 4L << 20);
        Assert.False(target.Workspace.IsDirty);
    }

    [Fact]
    public void RepeatedAttachmentsAreHandledByInstructionIdentityAndProduceBoundedNotes()
    {
        using SourceWorldFixture fixture = new();
        string source = "NewObject3D child\nNewObject3D parent\n" + string.Concat(Enumerable.Repeat("AddChild child\n", 5000)) + "GameZWriteZBDFile world.zbd\n";
        fixture.Write("gamegen/m1.gs", source);
        var assembler = new WorldAssembler(new MemoryFiles(new() { ["gamegen/m1.gs"] = Encoding.Latin1.GetBytes(source) }), Token);
        var world = assembler.Assemble("m1.gs");
        var parent = world.Nodes.Single(n => n.Name == "parent");
        SourceWorkspace workspace = new(fixture.Project);
        SourceObjectTarget target = new(workspace, "m1", world, parent, assembler.Provenance, assembler.Executions);
        var plan = SourceObjectEdits.PlanDelete(target, Token);
        Assert.InRange(plan.Notes.Count, 1, BoundedDiagnostics.MaximumMessages);
        Assert.InRange(plan.Notes.Sum(n => n.Length), 1, BoundedDiagnostics.MaximumRetainedCharacters);
        Assert.Single(plan.Notes, n => n.Contains("child was attached"));
        Assert.Contains(BoundedDiagnostics.OmissionNotice, plan.Notes);
        string changed = Encoding.Latin1.GetString(Assert.Single(plan.Changes).Content);
        var instructions = GameGenScriptSyntax.Parse(changed, TestContext.Current.CancellationToken).Lines.Where(l => l.IsInstruction).Select(l => l.Tokens[0]).ToList();
        Assert.Equal(["NewObject3D", "GameZWriteZBDFile"], instructions);
        Assert.False(workspace.IsDirty);
    }
}
