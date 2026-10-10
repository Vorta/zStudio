using System.Text;
using System.Text.Json;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class SourceDiffPresentationTests
{
    [Theory]
    [InlineData('&')]
    [InlineData('\u00e9')]
    public void EscapedRowsAreAdmittedBeforeDecodingWithoutLosingTheFullChangeCount(char escaped)
    {
        string[] lines = [.. Enumerable.Range(0, 2000).Select(i => $"#{i:D4} {new string(escaped, 390)}A")];
        byte[] disk = Encoding.Latin1.GetBytes(string.Join('\n', lines));
        byte[] working = Encoding.Latin1.GetBytes(string.Join('\n', lines.Select((line, i) => i % 4 == 0 ? line[..^1] + "B" : line)));
        var report = SourceDiff.Describe("data/changes.zrd", disk, working, 2000, TestContext.Current.CancellationToken);
        Assert.Equal(1000, report.ChangedLines); Assert.True(report.Truncated);
        Assert.InRange(report.Lines.Count, 201, 1999);
        Assert.True(JsonSerializer.SerializeToNode(report)!.ToJsonString().Length < SourceDiff.MaximumPresentationBytes);
        Assert.All(report.Lines, line => Assert.False(line.TextTruncated));
        var single = SourceDiff.Describe("data/changes.zrd", disk, working, 1, TestContext.Current.CancellationToken);
        Assert.Single(single.Lines); Assert.Equal(1000, single.ChangedLines); Assert.True(single.Truncated);
        Assert.Equal(report.Lines[0], single.Lines[0]);
    }

    [Fact]
    public void SmallExplicitAllowanceAndClippedLineTextAreDisclosed()
    {
        byte[] disk = Encoding.ASCII.GetBytes(new string('&', 1000) + "\nold\nend");
        byte[] working = Encoding.ASCII.GetBytes(new string('&', 1000) + "\nnew\nend");
        var report = SourceDiff.Describe("data/changes.zrd", disk, working, 2000, TestContext.Current.CancellationToken, 4096);
        Assert.Equal(2, report.ChangedLines); Assert.False(report.Truncated);
        Assert.True(report.Lines[0].TextTruncated); Assert.Equal(401, report.Lines[0].Text.Length);
        Assert.True(JsonSerializer.SerializeToNode(report)!.ToJsonString().Length < 4096);
        var omitted = SourceDiff.Describe("data/changes.zrd", disk, working, 2000, TestContext.Current.CancellationToken, 1024);
        Assert.Empty(omitted.Lines); Assert.True(omitted.Truncated); Assert.Equal(2, omitted.ChangedLines);
    }
}
