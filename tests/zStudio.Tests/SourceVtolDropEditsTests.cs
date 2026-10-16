using System.Text;
using zStudio.Core.Animation;
using zStudio.Core.Sources;
using zStudio.Core.Worlds;
using Xunit;

namespace zStudio.Tests;

public sealed class SourceVtolDropEditsTests
{
    [Fact]
    public void WindowMovePreservesSourceTokensAndRefusesStaleOrSharedDefinitions()
    {
        var token = TestContext.Current.CancellationToken;
        var files = VtolDropFixture.Files();
        var package = AnimationCompiler.Evaluate(files, VtolDropFixture.Root, ["ground"], token: token).Package;
        var candidate = Assert.Single(VtolDropWindows.Discover(package, token).Candidates);
        var context = VtolDropWindowsTests.Context(package);
        var snapshot = VtolDropWindows.Analyze(context, candidate, token);
        var plan = VtolDropWindows.PlanMove(snapshot, snapshot.Windows[0].Id, 2);
        _ = VtolDropWindows.PreviewMove(context, snapshot, plan, token);
        var change = Assert.Single(SourceVtolDropEdits.Prepare(files, VtolDropFixture.Root, ["ground"], package, plan, token));
        Assert.Equal(VtolDropFixture.Root, change.Relative);
        Assert.Equal(VtolDropFixture.Definitions().Replace("SEQUENCE_OFFSET 1", "SEQUENCE_OFFSET 2.0", StringComparison.Ordinal), Encoding.Latin1.GetString(change.Content));
        Assert.Equal(1, package.Entries[1].Sequences[1].Events[1].Threshold);
        Assert.Contains("another animation value", Assert.Throws<InvalidDataException>(() => SourceVtolDropEdits.Prepare(
            new UnexpectedChange(files), VtolDropFixture.Root, ["ground"], package, plan, token)).Message);
        files.Content[VtolDropFixture.Root] = change.Content;
        var rebuilt = AnimationCompiler.Evaluate(files, VtolDropFixture.Root, ["ground"], token: token).Package;
        Assert.Equal(2, rebuilt.Entries[1].Sequences[1].Events[1].Threshold);
        Assert.Equal(2, rebuilt.Entries[1].Sequences[1].Events[2].Threshold);
        Assert.Contains("Rebuild", Assert.Throws<InvalidDataException>(() => SourceVtolDropEdits.Prepare(files, VtolDropFixture.Root, ["ground"], package, plan, token)).Message);

        // A source file listed twice produces two entries from the same token. The selected entry must not silently
        // move its sibling, even though the compiler's first-name lookup still gives the first call a valid target.
        files.Content["data/m1/zrdr/flight.zad"] = Encoding.Latin1.GetBytes(VtolDropFixture.Definitions());
        files.Content[VtolDropFixture.Root] = Encoding.ASCII.GetBytes("ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION_FILE ( flight.zad ) ANIMATION_DEFINITION_FILE ( flight.zad ) ) )");
        var shared = AnimationCompiler.Evaluate(files, VtolDropFixture.Root, ["ground"], token: token).Package;
        var schedule = shared.Entries[1].Sequences[1];
        var call = schedule.Events[1]; var stop = schedule.Events[2];
        var window = new VtolDropWindow(call.Id, 1, schedule.Id, call.Id, stop.Id, 1, 2, 0, 3, true);
        var sharedCandidate = new VtolDropCandidate(1, "delivery", "ground", shared.Entries[1].Sequences[0].Id,
            schedule.Id, 2, [window], VtolDropWindows.Fingerprint(shared, token));
        Assert.Contains("several animation entries", Assert.Throws<InvalidDataException>(() => SourceVtolDropEdits.Prepare(
            files, VtolDropFixture.Root, ["ground"], shared, new(sharedCandidate, window, 2) { RuntimeVerifiedStart = 2 }, token)).Message);
    }

    // Simulate a second source change entering the token patch after its baseline was evaluated. The shared
    // comparison must refuse it even when the selected release timing and its source binding still match.
    private sealed class UnexpectedChange(IProjectFiles files) : IProjectFiles
    {
        private int rootReads;
        public bool Exists(string relative) => files.Exists(relative);
        public byte[] Read(string relative, CancellationToken token, ProjectReadLimits limits)
        {
            byte[] bytes = files.Read(relative, token, limits);
            if (relative == VtolDropFixture.Root && ++rootReads == 2)
                return Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(bytes).Replace("RESET_TIME ( -1 )", "RESET_TIME ( -2 )", StringComparison.Ordinal));
            return bytes;
        }
    }
}
