using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Desktop;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

public sealed class SourceWarningPresentationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(64)]
    [InlineData(65)]
    public void WarningPreviewDisclosesExactlyTheOmittedRows(int count)
    {
        string[] warnings = [.. Enumerable.Range(0, count).Select(i => $"warning{i}")];
        var shown = MainWindow.SourceWarningMessages(warnings, "m1/gamez.zbd").ToArray();
        Assert.Equal(Math.Min(count, 64) + (count > 64 ? 1 : 0), shown.Length);
        Assert.Equal(warnings.Take(64).Select(w => "m1/gamez.zbd: " + w), shown.Take(Math.Min(count, 64)));
        if (count > 64) Assert.Equal("m1/gamez.zbd: Showing 64 of 65 warnings; 1 more not shown.", shown[^1]);
        else Assert.DoesNotContain(shown, message => message.Contains("not shown", StringComparison.Ordinal));
    }

    [Fact]
    public void WarningOperandsAreBoundedBeforeCombiningAndSourceDataIsRetained()
    {
        string operand = new('x', 1024 * 1024);
        string[] warnings = Enumerable.Repeat(operand, 65).ToArray();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var shown = MainWindow.SourceWarningMessages(warnings, operand).ToArray();
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 512 * 1024);
        Assert.All(shown, message => Assert.InRange(message.Length, 1, 1024));
        Assert.Contains("Showing 64 of 65 warnings; 1 more not shown.", shown[^1]);
        Assert.All(warnings, warning => Assert.Same(operand, warning));
    }

    [Fact]
    public void ExportSummaryCountsEachOutputCapAndNotesEvenWhenAnOutputFailed()
    {
        SourceExportReport report = new(null,
        [new("m1/gamez.zbd", "world", "built", 0, 0, Enumerable.Repeat("a", 70).ToArray()),
         new("m2/gamez.zbd", "world", "failed", 0, 0, ["b", "c"], "failure")])
        { Notes = Enumerable.Repeat("note", 65).ToArray() };
        Assert.Equal("\nFailures are listed in Problems.\n130 of 137 warnings are listed in Problems; 7 more not shown.", MainWindow.ExportProblemSummary(report));
        Assert.Equal("", MainWindow.ExportProblemSummary(new(null, [])));
        Assert.Equal("\n1 warning is listed in Problems.", MainWindow.ExportProblemSummary(new(null, [new("m1/gamez.zbd", "world", "built", 0, 0, ["one"])])));
    }

    [Fact]
    public void ReconstructionNotesPreserveOrdinaryTextAndDiscloseTheIndependentCap()
    {
        Assert.Equal(["ordinary note"], MainWindow.SourceWarningMessages(["ordinary note"], "", 256, "notes"));
        var shown = MainWindow.SourceWarningMessages(Enumerable.Repeat("note", 257).ToArray(), "", 256, "notes").ToArray();
        Assert.Equal(257, shown.Length); Assert.Equal("Showing 256 of 257 notes; 1 more not shown.", shown[^1]);
        Assert.Equal("\n256 of 257 notes are listed in Problems; 1 more not shown.", MainWindow.SourceWarningSummary(257, 256, "notes"));
    }
}
