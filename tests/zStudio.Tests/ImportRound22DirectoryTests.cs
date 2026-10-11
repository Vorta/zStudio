using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ImportRound22DirectoryTests
{
    [Fact]
    public async Task ExportHoldsTheDestinationAndStagingTreeWhileItBuilds()
    {
        if (!OperatingSystem.IsWindows()) return;
        using SourceFixture fixture = new();
        await SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, token: TestContext.Current.CancellationToken);
        string destination = Path.Combine(fixture.Root, "exported"); bool reached = false;
        await SourceBuilder.ExportAsync(fixture.Project, destination, ["m1/zrdr.zbd"], progress: new OnReport(_ =>
        {
            if (reached) return;
            reached = true;
            string staging = Assert.Single(Directory.GetDirectories(destination, ".zstudio-staging-*"));
            Assert.ThrowsAny<IOException>(() => Directory.Move(staging, staging + "-moved"));
            Assert.ThrowsAny<IOException>(() => Directory.Move(destination, destination + "-moved"));
        }), token: TestContext.Current.CancellationToken);
        Assert.True(reached); Assert.Empty(Directory.GetDirectories(destination, ".zstudio-staging-*"));
        Assert.True(File.Exists(Path.Combine(destination, "m1/zrdr.zbd")));
    }

    [Fact]
    public async Task ReconstructionKeepsItsNewProjectDirectoryInPlace()
    {
        if (!OperatingSystem.IsWindows()) return;
        using SourceFixture fixture = new(); bool reached = false;
        await SourceExtractor.ExtractAsync(fixture.Corpus, fixture.Project, progress: new OnReport(_ =>
        {
            if (reached) return;
            reached = true;
            Assert.ThrowsAny<IOException>(() => Directory.Move(fixture.Project, fixture.Project + "-moved"));
        }), token: TestContext.Current.CancellationToken);
        Assert.True(reached); Assert.True(SourceProject.IsProject(fixture.Project));
    }

    [Fact]
    public void PreparedVerificationRetainsItsDependencyDirectoryUntilDisposed()
    {
        if (!OperatingSystem.IsWindows()) return;
        using Temporary tree = new();
        Directory.CreateDirectory(tree.Path("data")); Directory.CreateDirectory(tree.Path("gamegen"));
        File.WriteAllText(tree.Path("gamegen/m1.gs"), "Quit\n");
        SourceWorkspace workspace = new(tree.Path(""));
        var edit = workspace.BeginPreparedEdit();
        edit.Workspace.Apply("Prepared", [("gamegen/m1.gs", System.Text.Encoding.ASCII.GetBytes("Quit 1\n"))], TestContext.Current.CancellationToken);
        using (var verified = workspace.VerifyPreparedEdit(edit, TestContext.Current.CancellationToken))
        {
            Assert.ThrowsAny<IOException>(() => Directory.Move(tree.Path("gamegen"), tree.Path("moved")));
            Assert.NotNull(workspace.AcceptPreparedEdit(edit, TestContext.Current.CancellationToken, verified));
        }
        Directory.Move(tree.Path("gamegen"), tree.Path("moved"));
    }

    private sealed class OnReport(Action<SourceProgress> action) : IProgress<SourceProgress>
    {
        public void Report(SourceProgress value) => action(value);
    }

    [Fact]
    public void ALeaseKeepsEveryAncestorInPlaceAndAllowsNewChildren()
    {
        if (!OperatingSystem.IsWindows()) return;
        using Temporary tree = new();
        string parent = tree.Path("parent"), nested = tree.Path("parent/nested");
        using (DirectoryLease lease = new())
        {
            lease.Hold(nested, create: true);
            File.WriteAllBytes(Path.Combine(nested, "new.bin"), [1]);
            Assert.ThrowsAny<IOException>(() => Directory.Move(parent, tree.Path("moved")));
            Assert.ThrowsAny<IOException>(() => Directory.Move(nested, tree.Path("moved")));
        }
        Directory.Move(parent, tree.Path("moved"));
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(tree.Path("moved/nested/new.bin")));
    }

    [Fact]
    public void ASealedFileKeepsItsSourceAndInstalledParentsUntilDisposed()
    {
        if (!OperatingSystem.IsWindows()) return;
        using Temporary tree = new();
        string staged = tree.Path("staging/file.bin"), target = tree.Path("output/file.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(staged, [1, 2]);
        using (SealedFile file = SealedFile.Open(staged, JournalDigest.OfContent([1, 2])))
        {
            Assert.ThrowsAny<IOException>(() => Directory.Move(tree.Path("staging"), tree.Path("moved")));
            file.MoveTo(target);
            Assert.ThrowsAny<IOException>(() => Directory.Move(tree.Path("output"), tree.Path("moved")));
        }
        Directory.Move(tree.Path("output"), tree.Path("moved"));
        Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(tree.Path("moved/file.bin")));
    }

    [Theory]
    [InlineData("replace")]
    [InlineData("install")]
    public void ExportKeepsDestinationDirectoriesThroughPublicationAndRollback(string step)
    {
        if (!OperatingSystem.IsWindows()) return;
        using Temporary tree = new();
        string staging = tree.Path("staging"), destination = tree.Path("output");
        Directory.CreateDirectory(Path.Combine(staging, "m1")); Directory.CreateDirectory(Path.Combine(destination, "m1"));
        File.WriteAllBytes(Path.Combine(staging, "m1/gamez.zbd"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(destination, "m1/gamez.zbd"), [9]);
        bool reached = false;
        Assert.Throws<IOException>(() => SourceBuilder.Publish(staging, destination, [("m1/gamez.zbd", JournalDigest.OfContent([1, 2, 3]))], true, TestContext.Current.CancellationToken,
            (current, _) =>
            {
                if (current != step) return;
                reached = true;
                Assert.ThrowsAny<IOException>(() => Directory.Move(Path.Combine(destination, "m1"), tree.Path("moved")));
                throw new IOException("stop after checking the held directory");
            }));
        Assert.True(reached);
        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(Path.Combine(destination, "m1/gamez.zbd")));
        Assert.Empty(Directory.GetDirectories(destination, ".zstudio-backup-*"));
    }

    private sealed class Temporary : IDisposable
    {
        private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zstudio-directory-test-" + Guid.NewGuid().ToString("N"));
        public Temporary() => Directory.CreateDirectory(root);
        public string Path(string relative) => System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        public void Dispose() => Directory.Delete(root, true);
    }
}

public sealed partial class SourcePublisherTests
{
    [Theory]
    [InlineData("hold")]
    [InlineData("install")]
    public void ASourceSaveHoldsItsTargetDirectoryAcrossPublication(string at)
    {
        if (!OperatingSystem.IsWindows()) return;
        using Project project = new();
        bool reached = false;
        SourcePublisher publisher = new(project.Root)
        {
            Fault = (step, index) =>
            {
                if (step != at || index != 0) return;
                reached = true;
                Assert.ThrowsAny<IOException>(() => Directory.Move(project.Full("data/m1"), project.Full("data/renamed")));
            },
        };
        publisher.Publish(ThreeFiles(project), "Hold source parent", Token);
        Assert.True(reached); Assert.Empty(project.Leftovers());
        Assert.Equal("GRAVITY ( -4.9 )\n", System.Text.Encoding.ASCII.GetString(project.Read(Ai)));
    }

    [Fact]
    public void RecoveryHoldsItsTargetDirectoryBeforeTheCompletionHook()
    {
        if (!OperatingSystem.IsWindows()) return;
        using Project project = new();
        var interrupted = Failing(project, "install", 0, () => new SourcePublisher.Crash());
        Assert.Throws<SourcePublisher.Crash>(() => interrupted.Publish(ThreeFiles(project), "Interrupted", Token));
        SourcePublisher publisher = new(project.Root);
        string id = Assert.Single(publisher.FindInterrupted(Token)).SaveId;
        bool reached = false;
        publisher.Fault = (step, index) =>
        {
            if (step != "complete" || index != 0) return;
            reached = true;
            Assert.ThrowsAny<IOException>(() => Directory.Move(project.Full("data/m1"), project.Full("data/renamed")));
        };
        var result = publisher.Resolve(id, SourceRecoveryAction.Complete, Token);
        Assert.True(reached); Assert.True(result.Resolved); Assert.Empty(result.Conflicts); Assert.Empty(project.Leftovers());
    }
}
