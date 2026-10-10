using System.Text;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class BuildDiagnosticBudgetTests
{
    private sealed class MemoryFiles(Dictionary<string, byte[]> files) : IProjectFiles
    {
        public bool Exists(string relative) => files.ContainsKey(relative);
        public byte[] Read(string relative, CancellationToken token) => Read(relative, token, ProjectReadLimits.Document);
        public byte[] Read(string relative, CancellationToken token, ProjectReadLimits limits) { token.ThrowIfCancellationRequested(); byte[] result = files[relative]; limits.Validate(result); return result; }
    }
    private static WorldAssembler Assemble(string main, string repeated)
    {
        var assembler = new WorldAssembler(new MemoryFiles(new()
        {
            ["gamegen/main.gs"] = Encoding.UTF8.GetBytes(main),
            ["gamegen/repeated.gs"] = Encoding.UTF8.GetBytes(repeated),
        }), TestContext.Current.CancellationToken);
        assembler.Assemble("main.gs");
        return assembler;
    }

    [Theory]
    [InlineData("FindNode")]
    [InlineData("FindSubNode")]
    [InlineData("AddChild")]
    public void RepeatedMillionCharacterLookupDoesNotFormatItsFullOperand(string command)
    {
        _ = Assemble("source repeated.gs\nGameZWriteZBDFile world.zbd\n", command + " missing\n");
        string operand = new('x', 1_000_000);
        byte[] repeated = Encoding.UTF8.GetBytes(command + " " + operand + "\n");
        (WorldAssembler Assembler, long Allocated) Run(int count)
        {
            var files = new MemoryFiles(new()
            {
                ["gamegen/main.gs"] = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("source repeated.gs\n", count)) + "GameZWriteZBDFile world.zbd\n"),
                ["gamegen/repeated.gs"] = repeated,
            });
            long before = GC.GetAllocatedBytesForCurrentThread();
            var assembler = new WorldAssembler(files, TestContext.Current.CancellationToken);
            assembler.Assemble("main.gs");
            return (assembler, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        // Fifty executions retain the million-character identity but stay below the separate 64 Mi-character
        // execution allowance. Formatting the complete operand would still add about 80 MB over the ten-run case.
        var ten = Run(10); var fifty = Run(50);
        string warning = Assert.Single(fifty.Assembler.Warnings);
        Assert.Contains(command, warning); Assert.Contains("…", warning);
        Assert.InRange(warning.Length, 1, BoundedDiagnostics.MaximumMessageCharacters);
        Assert.InRange(fifty.Allocated - ten.Allocated, 0, 4 * 1024 * 1024);
        Assert.InRange(fifty.Allocated, 0, 16 * 1024 * 1024);
    }

    [Fact]
    public void ExhaustedFormattingBudgetStopsEvaluatingOperandsIncludingDuplicates()
    {
        BoundedDiagnostics diagnostics = new();
        int evaluated = 0;
        string Operand() { evaluated++; return "same warning"; }
        for (int i = 0; i < 10_000; i++) diagnostics.Add($"Warning: {Operand()}");
        Assert.Equal(BoundedDiagnostics.MaximumFormattingCharacters / BoundedDiagnostics.MaximumMessageCharacters, evaluated);
        Assert.Equal(["Warning: same warning", BoundedDiagnostics.OmissionNotice], diagnostics.Messages);
    }

    [Fact]
    public void RetainedCharactersAndEntriesIncludeOneReservedOmissionNotice()
    {
        BoundedDiagnostics diagnostics = new();
        string longText = new('z', 100_000);
        for (int i = 0; i < 10_000; i++) diagnostics.Add($"{i}: {longText}, {longText}, {longText}, {longText}, {longText}, {longText}");
        Assert.InRange(diagnostics.Messages.Count, 2, BoundedDiagnostics.MaximumMessages);
        Assert.InRange(diagnostics.Messages.Sum(m => m.Length), 1, BoundedDiagnostics.MaximumRetainedCharacters);
        Assert.All(diagnostics.Messages, m => Assert.InRange(m.Length, 1, BoundedDiagnostics.MaximumMessageCharacters));
        Assert.Single(diagnostics.Messages, m => m == BoundedDiagnostics.OmissionNotice);
        Assert.Equal(BoundedDiagnostics.OmissionNotice, diagnostics.Messages[^1]);
    }

    [Fact]
    public void ShortWarningsExhaustEntryBudgetWithoutExceedingIt()
    {
        BoundedDiagnostics diagnostics = new();
        for (int i = 0; i < 2000; i++) diagnostics.Add($"Warning {i}");
        Assert.Equal(BoundedDiagnostics.MaximumMessages, diagnostics.Messages.Count);
        Assert.Equal(BoundedDiagnostics.OmissionNotice, diagnostics.Messages[^1]);
    }

    [Fact]
    public void AttachingToExistingNotesAccountsTheirStorageBeforeAcceptingMore()
    {
        List<string> notes = Enumerable.Range(0, 2000).Select(i => $"{i}: " + new string('z', 2000)).ToList();
        BoundedDiagnostics diagnostics = new(notes);
        diagnostics.Add($"One more warning");
        Assert.Same(notes, diagnostics.Messages);
        Assert.InRange(notes.Sum(n => n.Length), 1, BoundedDiagnostics.MaximumRetainedCharacters);
        Assert.All(notes, n => Assert.InRange(n.Length, 1, BoundedDiagnostics.MaximumMessageCharacters));
        Assert.Single(notes, n => n == BoundedDiagnostics.OmissionNotice);
    }

    [Fact]
    public void FullMessageDisclosesTextDroppedByLaterAppend()
    {
        BoundedDiagnostics diagnostics = new();
        string a = new('a', 192);
        diagnostics.Add($"{a}{a}{a}{a}{a}1234567890123456789012345678901234567890123456789012345678901234discarded");
        string warning = Assert.Single(diagnostics.Messages);
        Assert.Equal(BoundedDiagnostics.MaximumMessageCharacters, warning.Length);
        Assert.EndsWith("…", warning);
    }

    [Fact]
    public void MissingModelWarningShowsOnlyEightDirectoriesAndDisclosesTheOthers()
    {
        string directories = string.Concat(Enumerable.Range(0, 12).Select(i => $"SetModelDirectory ..\\data\\directory_{i:D2}\n"));
        var assembler = Assemble(directories + "LoadGameGen missing.flt object\nGameZWriteZBDFile world.zbd\n", "");
        string warning = Assert.Single(assembler.Warnings);
        Assert.Contains("directory_11", warning); Assert.Contains("directory_04", warning);
        Assert.DoesNotContain("directory_03", warning); Assert.Contains("(4 more)", warning);
    }

    [Fact]
    public void DiagnosticShorteningDoesNotShortenSourceIdentityOrLookupOperands()
    {
        string prefix = new('x', 400), first = prefix + "a.gs", second = prefix + "b.gs";
        string node = new('x', 34);
        var assembler = new WorldAssembler(new MemoryFiles(new()
        {
            ["gamegen/main.gs"] = Encoding.UTF8.GetBytes($"source {first}\nsource {second}\nGameZWriteZBDFile world.zbd\n"),
            ["gamegen/" + first] = Encoding.UTF8.GetBytes($"NewObject3D {node}\nFindNode {prefix}a\nNodeSetDescription corrupted\n"),
            ["gamegen/" + second] = Encoding.UTF8.GetBytes("NewObject3D second\n"),
        }), TestContext.Current.CancellationToken);
        var world = assembler.Assemble("main.gs");
        Assert.Equal([node, "second"], world.Nodes.Select(n => n.Name));
        Assert.Contains("gamegen/" + first, assembler.ScriptFiles);
        Assert.Contains("gamegen/" + second, assembler.ScriptFiles);
        Assert.Single(assembler.Warnings);
    }

    [Fact]
    public void NestedModelBuildersShareFormattingAndRetentionBudget()
    {
        BoundedDiagnostics diagnostics = new();
        for (int i = 0; i < 1100; i++)
        {
            ModelBuilder builder = new() { Diagnostics = diagnostics.WithContext("model.gltf", "mesh", "part") };
            builder.Add(new([], [], [], [], new()));
        }
        Assert.Equal(["model.gltf: mesh part: A polygon with 0 corners was discarded.", BoundedDiagnostics.OmissionNotice], diagnostics.Messages);
    }
}
