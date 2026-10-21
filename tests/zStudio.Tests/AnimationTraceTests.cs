using zStudio.Core.Animation;
using Xunit;

namespace zStudio.Tests;

public sealed partial class AnimationTests
{
    [Fact]
    public void EqualTimeDispatchesHaveDistinctStableOccurrencesAndContext()
    {
        var package = Fixture(); var sequence = package.Entries[0].Sequences[0];
        var callback = AnimationCatalog.Create(35); callback.SetInt(12,42); sequence.Events.Add(callback);
        var loop = AnimationCatalog.Create(30); loop.SetInt(16,3); sequence.Events.Add(loop);
        var player = new AnimationPlayer(Context(package),0); var first = player.EvaluateForTest(1,true);
        Assert.True(first.Trace.Count > 2); Assert.Equal(first.Trace.Count,first.Trace.Select(t => t.Occurrence).Distinct().Count());
        Assert.All(first.Trace,t => Assert.Equal(0,t.Entry));
        var issue = first.Issues.Single(d => d.Message.StartsWith("Game callback 42",StringComparison.Ordinal));
        Assert.Equal("Information",issue.Severity); Assert.Equal("Support",issue.Category); Assert.Equal(callback.Id,issue.Source.Event); Assert.Equal(sequence.Id,issue.Source.Sequence); Assert.Equal(1,issue.Source.Instance);
        player.EvaluateForTest(0,true); var replay = player.EvaluateForTest(1,true);
        Assert.Equal(first.Trace,replay.Trace); Assert.Equal(first.Issues,replay.Issues); Assert.Equal(first.TraceDropped,replay.TraceDropped);
    }
    [Fact]
    public void IdenticallyNamedSequencesDoNotMergeProblemIdentity()
    {
        var package = Fixture(); var entry = package.Entries[0]; var other = new AnimationSequence(new byte[64]) { Name = entry.Sequences[0].Name }; entry.Sequences.Add(other);
        foreach (var sequence in entry.Sequences) sequence.Events.Add(AnimationCatalog.Create(35));
        var frame = new AnimationPlayer(Context(package),0).EvaluateForTest(.1);
        var issues = frame.Issues.Where(d => d.Message.StartsWith("Game callback",StringComparison.Ordinal)).ToArray();
        Assert.Equal(2,issues.Length); Assert.NotEqual(issues[0].Source.Sequence,issues[1].Source.Sequence);
    }
}
