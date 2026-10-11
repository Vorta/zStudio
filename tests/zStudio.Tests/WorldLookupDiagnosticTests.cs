using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

[Collection("Allocation-sensitive")]
public sealed class WorldLookupDiagnosticTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static SourceLookupChange Change(string name = "ground", string source = "gamegen/support/tex_fxm1.gw", string found = "world/ground") =>
        new(new("m1", SourceLookup.TextureEffect, name, source, 2, 2, found),
            new("m1", SourceLookup.TextureEffect, name, source, 2, 3, found));

    [Fact]
    public void OrdinaryDescriptionsRetainSlotsComparisonAndUncertainty()
    {
        var change = Change() with { Uncertain = true };
        Assert.Equal("FindNode ground in gamegen/support/tex_fxm1.gw", WorldLookups.Describe(change.After));
        Assert.Equal("m1: FindNode ground in gamegen/support/tex_fxm1.gw finds world/ground (slot 3) instead of world/ground (slot 2) in replaced files (possibly: the worlds are too large to tell every copy apart).",
            WorldLookups.Describe(change, " in replaced files"));
    }

    [Fact]
    public void AlreadyOwnedLongOperandsAreBoundedBeforeDescriptionCopies()
    {
        var small = Change(); var large = Change(new string('n', 8192), new string('s', 8192), new string('p', 8192));
        _ = WorldLookups.Describe(small); _ = WorldLookups.Describe(large);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 8; i++) _ = WorldLookups.Describe(small);
        long baseline = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 8; i++) _ = WorldLookups.Describe(large);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before - baseline, -4096, 64 * 1024);
        string shown = WorldLookups.Describe(large);
        Assert.True(shown.Length <= BoundedDiagnostics.MaximumMessageCharacters);
        Assert.Contains("…", shown); Assert.Contains("slot 3", shown); Assert.Contains("slot 2", shown);
        Assert.Equal(8192, large.After.Source.Length); Assert.Same(large.Before.Found, large.After.Found);
        Assert.Equal(large.Before.Key, large.After.Key);
    }

    [Fact]
    public void ReportAggregateDisclosesOmissionFirstAndKeepsFullOrderedLookupRecords()
    {
        var changes = Enumerable.Range(0, 1025).Select(i => Change("n" + i)).ToArray();
        var notes = SourceBuilder.LookupNotes(new(), [], changes, Token);
        Assert.Equal(BoundedDiagnostics.MaximumMessages, notes.Count);
        Assert.Equal(BoundedDiagnostics.OmissionNotice, notes[0]);
        Assert.Equal(1, notes.Count(n => n == BoundedDiagnostics.OmissionNotice));
        Assert.True(notes.Sum(n => n.Length) <= BoundedDiagnostics.MaximumRetainedCharacters);
        Assert.Contains("FindNode n0 ", notes[1]); Assert.Contains("FindNode n1022 ", notes[^1]);
        Assert.Equal(1025, changes.Length); Assert.Equal("n1024", changes[^1].After.Name);
    }

    [Fact]
    public void ExhaustedCollectorSkipsNestedDescriptionEvaluation()
    {
        BoundedDiagnostics notes = new();
        for (int i = 0; i < 1024; i++) notes.Add($"same warning");
        // Even duplicates spend formatting work. Invalid sentinels prove that no operand is evaluated after exhaustion.
        WorldLookups.Describe(notes, new(null!, null!));
        Assert.Equal(new[] { "same warning", BoundedDiagnostics.OmissionNotice }, notes.Messages);
    }

    [Fact]
    public void ForwardedMessagesKeepTheirCauseButRemainDefensivelyBounded()
    {
        string exact = new('x', 1024), oversized = new('y', 8192);
        string uncheckedNote = SourceBuilder.LookupsUnchecked("m1", new InvalidDataException(new string('e', 8192)));
        Assert.Contains("none of them is reported", uncheckedNote); Assert.Contains("…", uncheckedNote);
        Assert.InRange(uncheckedNote.Length, 193, 1024);
        var notes = SourceBuilder.LookupNotes(new(), [exact, oversized, uncheckedNote], [], Token);
        Assert.Equal(exact, notes[0]); Assert.Equal(1024, notes[1].Length); Assert.EndsWith("…", notes[1]);
        Assert.Equal(uncheckedNote, notes[2]);
        BoundedDiagnostics authored = new(); authored.Add($"{exact}");
        Assert.Equal(193, Assert.Single(authored.Messages).Length); // Prepared forwarding never changes authored operand policy.
    }

    [Fact]
    public void CancellationIsObservedBetweenRecordsAndAtEmptyInputWithFreshRetry()
    {
        using CancellationTokenSource cancellation = new();
        IEnumerable<SourceLookupChange> Changes()
        {
            yield return Change(); cancellation.Cancel(); yield return new(null!, null!);
        }
        var error = Assert.Throws<OperationCanceledException>(() => SourceBuilder.LookupNotes(new(), [], Changes(), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Throws<OperationCanceledException>(() => SourceBuilder.LookupNotes(new(), [], [], cancellation.Token));
        Assert.Single(SourceBuilder.LookupNotes(new(), [], [Change()], Token));
    }

    private sealed class Progress(Action<SourceProgress> report) : IProgress<SourceProgress>
    { public void Report(SourceProgress value) => report(value); }

    [Fact]
    public void FailedOutputSummaryBoundsAggregationAndRetainsOrdinaryWordingAndRecords()
    {
        SourceExportResult Good() => new("good.zbd", "archive", "built", 1, 1, []);
        SourceExportResult Bad(string path, string error) => new(path, "archive", "failed", 0, 0, [], error);
        Assert.Equal("Nothing was written because some outputs failed: one.zbd: bad resource; two.zbd: missing file",
            SourceBuilder.FailedOutputs([Good(), Bad("one.zbd", "bad resource"), Bad("two.zbd", "missing file")], Token));
        string longError = new('e', 8192);
        var failed = Enumerable.Range(0, 1025).Select(i => Bad($"m{i}/zrdr.zbd", longError)).ToArray();
        string summary = SourceBuilder.FailedOutputs(failed, Token);
        Assert.StartsWith("Nothing was written because some outputs failed: " + BoundedDiagnostics.OmissionNotice, summary);
        Assert.True(summary.Length < BoundedDiagnostics.MaximumRetainedCharacters + 4096);
        Assert.DoesNotContain(longError, summary); Assert.All(failed, result => Assert.Same(longError, result.Error));
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => SourceBuilder.FailedOutputs(failed, cancellation.Token));
    }

    [Fact]
    public async Task ActualFailedOutputPublishesNothingAndSubsequentValidSelectionCanRetry()
    {
        using SourceWorldFixture fixture = new(); fixture.WritePartDatabase();
        string destination = Path.Combine(fixture.Root, "export"), output = Path.Combine(destination, "m1", "gamez.zbd");
        await SourceBuilder.ExportAsync(fixture.Project, destination, ["m1/gamez.zbd"], token: Token);
        byte[] baseline = File.ReadAllBytes(output);
        fixture.WritePartDatabase(secondGround: true);
        fixture.Write("data/m1/zrdr/" + new string('x', 220) + "/entry.zrd", "()");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => SourceBuilder.ExportAsync(fixture.Project, destination,
            ["m1/gamez.zbd", "m1/zrdr.zbd"], overwrite: true, token: Token));
        Assert.StartsWith("Nothing was written because some outputs failed: m1/zrdr.zbd: ", error.Message);
        Assert.Contains("…", error.Message); Assert.True(error.Message.Length < 512);
        Assert.Equal(baseline, File.ReadAllBytes(output)); Assert.False(File.Exists(Path.Combine(destination, "m1", "zrdr.zbd")));
        var retry = await SourceBuilder.ExportAsync(fixture.Project, destination, ["m1/gamez.zbd"], overwrite: true, token: Token);
        Assert.Equal("built", Assert.Single(retry.Outputs).Status); Assert.False(baseline.SequenceEqual(File.ReadAllBytes(output)));
    }

    [Fact]
    public async Task ActualExportCancellationBeforePublishPreservesDestinationAndRetryReportsChanges()
    {
        using SourceWorldFixture fixture = new(); fixture.WritePartDatabase();
        fixture.Write("gamegen/m1_zbd.gs", "source support\\tex_fxm1.gw\nQuit\n");
        fixture.Write("gamegen/support/tex_fxm1.gw", "FindNode ground\nQuit\n");
        string destination = Path.Combine(fixture.Root, "export"), output = Path.Combine(destination, "m1", "gamez.zbd");
        await SourceBuilder.ExportAsync(fixture.Project, destination, ["m1/gamez.zbd"], token: Token);
        byte[] baseline = File.ReadAllBytes(output);
        fixture.WritePartDatabase(secondGround: true);
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        bool reachedPublishing = false;
        var progress = new Progress(value => { if (value.Item == "Publishing") { reachedPublishing = true; cancellation.Cancel(); } });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SourceBuilder.ExportAsync(fixture.Project, destination,
            ["m1/gamez.zbd"], overwrite: true, progress: progress, token: cancellation.Token));
        Assert.True(reachedPublishing); Assert.Equal(baseline, File.ReadAllBytes(output));
        var retry = await SourceBuilder.ExportAsync(fixture.Project, destination, ["m1/gamez.zbd"], overwrite: true, token: Token);
        var change = Assert.Single(retry.LookupChanges, c => c.After.Kind == SourceLookup.TextureEffect && c.After.Name == "ground");
        Assert.Equal("gamegen/support/tex_fxm1.gw", change.After.Source);
        Assert.Contains(WorldLookups.Describe(change, " in the files this export replaced"), retry.Notes);
        Assert.Equal("built", Assert.Single(retry.Outputs).Status); Assert.False(baseline.SequenceEqual(File.ReadAllBytes(output)));
    }
}
