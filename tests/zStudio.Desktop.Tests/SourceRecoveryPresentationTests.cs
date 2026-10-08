using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Desktop;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

public sealed class SourceRecoveryPresentationTests
{
    [Theory]
    [InlineData(false, 12)]
    [InlineData(true, 8)]
    public void FileSummariesBoundEachFieldBeforeJoiningAndDiscloseOmittedRows(bool detailed, int omitted)
    {
        SourceRecoveryFile[] files = [.. Enumerable.Range(0, 20).Select(i =>
            new SourceRecoveryFile("data/" + new string('&', 4096) + $"/{i}.zrd", SourceRecoveryFileState.Other, true))];
        string text = MainWindow.RecoveryFilesText(files, detailed);
        Assert.StartsWith($"20 files ({omitted} more not shown): ", text);
        Assert.InRange(text.Length, 1, 4096);
        Assert.DoesNotContain(new string('&', 257), text);
        Assert.All(files, f => Assert.True(f.Relative.Length > 4096));
        string ordinary = MainWindow.RecoveryFilesText([new("gamegen/m1.gs", SourceRecoveryFileState.Before, true)], detailed);
        Assert.Contains("gamegen/m1.gs", ordinary);
        Assert.Contains(detailed ? "as before the save" : "Before", ordinary);
        if (detailed) Assert.Contains("original kept", ordinary);
    }

    [Fact]
    public void ConflictSummaryBoundsBothFieldsAndLeavesCompleteResultsIntact()
    {
        SourceRecoveryConflict[] conflicts = [.. Enumerable.Range(0, 20).Select(i =>
            new SourceRecoveryConflict("data/" + new string('&', 4096) + $"/{i}.zrd", new string('&', 4096)))];
        string text = MainWindow.RecoveryConflictsText(conflicts);
        Assert.StartsWith("20 conflicts (12 more not shown): ", text);
        Assert.InRange(text.Length, 1, 4608);
        Assert.DoesNotContain(new string('&', 257), text);
        Assert.All(conflicts, c => { Assert.True(c.Relative.Length > 4096); Assert.Equal(4096, c.Reason.Length); });
        Assert.Contains("data/a.zrd: changed outside", MainWindow.RecoveryConflictsText([new("data/a.zrd", "changed outside")]));
    }
}
