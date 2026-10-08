using System.Globalization;
using System.Text;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class AnimationCompilationWorkTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Root = "data/m1/zrdr/anim.zad";

    [Fact]
    public void IgnoredSettingsCannotMultiplyDiagnosticAllocationForEveryMatchingRoot()
    {
        string unknown = new('x', 512);
        var files = Files("NAME ( \"a****\" ) " + string.Concat(Enumerable.Repeat(unknown + " ( ) ", 500)));
        string[] roots = Enumerable.Range(0, 200).Select(i => "a" + i.ToString("D4", CultureInfo.InvariantCulture)).ToArray();
        _ = AnimationCompiler.Compile(files, Root, [roots[0]], Token);

        long before = GC.GetAllocatedBytesForCurrentThread();
        var result = AnimationCompiler.Compile(files, Root, roots, Token);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // The pre-fix compiler allocates over 76 MB for this 259 KB source although its output is only 75 KB.
        Assert.InRange(allocated, 0, 35_000_000);
        Assert.Equal(201, result.Package.Entries.Count);
        Assert.Equal(roots, result.Package.Entries.Skip(1).Select(e => e.RootName));
        Assert.Contains(BoundedDiagnostics.OmissionNotice, result.Warnings);
        Assert.All(result.Warnings, warning => Assert.InRange(warning.Length, 1, BoundedDiagnostics.MaximumMessageCharacters));
    }

    [Fact]
    public void ExpansionBudgetCountsIgnoredSyntaxAcrossRootsBeforeCompilingAnotherEntry()
    {
        var files = Files("NAME ( \"a****\" ) " + string.Concat(Enumerable.Repeat("UNUSED ( ) ", 100)));
        var one = AnimationCompiler.Compile(files, Root, ["a0000"], null, FormatRegistry.MaximumDocumentBytes, Token, maximumExpansionWork: 25_000);
        Assert.Equal(2, one.Package.Entries.Count);

        string[] roots = Enumerable.Range(0, 20).Select(i => "a" + i.ToString("D4", CultureInfo.InvariantCulture)).ToArray();
        var error = Assert.Throws<InvalidDataException>(() => AnimationCompiler.Compile(files, Root, roots, null,
            FormatRegistry.MaximumDocumentBytes, Token, maximumExpansionWork: 25_000));
        Assert.Contains("definition expansion exceeds", error.Message);
        // A failed operation has no shared allowance or definition cache that poisons the next compilation.
        Assert.Equal(2, AnimationCompiler.Compile(files, Root, ["a0001"], null, FormatRegistry.MaximumDocumentBytes, Token,
            maximumExpansionWork: 25_000).Package.Entries.Count);
    }

    [Fact]
    public void RepeatedEventAttributeLookupsReuseTheFrozenDefinitionIndex()
    {
        var files = Files("NAME ( \"a****\" ) SEQUENCE_DEFINITION ( NAME ( move ) OBJECT_MOTION_FROM_TO ( NAME ( INPUT_NODE ) " +
            string.Concat(Enumerable.Repeat("UNUSED ( ) ", 1000)) + " ) )");
        string[] roots = Enumerable.Range(0, 200).Select(i => "a" + i.ToString("D4", CultureInfo.InvariantCulture)).ToArray();
        _ = AnimationCompiler.Compile(files, Root, [roots[0]], Token);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var result = AnimationCompiler.Compile(files, Root, roots, Token);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.InRange(allocated, 0, 20_000_000);
        Assert.Equal(201, result.Package.Entries.Count);
        Assert.All(result.Package.Entries.Skip(1), entry =>
        {
            var ev = Assert.Single(Assert.Single(entry.Sequences).Events);
            Assert.Equal(11, ev.Type);
            Assert.Equal(-200, ev.I32(16));
        });
    }

    [Fact]
    public void OmittedWarningsDoNotSuppressLaterSettingsOrEngineLoadRejection()
    {
        var files = Files("NAME ( gate ) " + string.Concat(Enumerable.Repeat("UNUSED ( ) ", 1500)) +
            "HEALTH ( 3 ) HEALTH ( 7 ) SEQUENCE_DEFINITION ( NAME ( first ) NAME ( last ) OBJECT_ACTIVE_STATE ( NAME ( missing ) STATE ( ACTIVE ) ) )");
        var result = AnimationCompiler.Compile(files, Root, ["gate"], Token);
        var entry = result.Package.Entries[1];
        Assert.Equal(7f, entry.F32(172));
        Assert.Equal("last", entry.Sequences[0].Name);
        Assert.Contains("names node missing", result.EngineRejection);
        Assert.Contains(BoundedDiagnostics.OmissionNotice, result.Warnings);
    }

    private static MemoryFiles Files(string definition) => new(Encoding.Latin1.GetBytes(
        "ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION ( " + definition + " ) ) )"));
    private sealed class MemoryFiles(byte[] content) : IProjectFiles
    {
        public bool Exists(string relative) => relative == Root;
        public byte[] Read(string relative, CancellationToken token) => Read(relative, token, ProjectReadLimits.Document);
        public byte[] Read(string relative, CancellationToken token, ProjectReadLimits limits) { token.ThrowIfCancellationRequested(); byte[] bytes = content; limits.Validate(bytes); return bytes; }
    }
}
