using System.IO;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Desktop;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

public sealed class SourceWorldSessionPresenceTests
{
    [Fact]
    public void SessionChecksScriptMetadataWithoutOpeningItsPayloadAndObservesCancellation()
    {
        string root = Path.Combine(Path.GetTempPath(), "zstudio-session-presence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "data"));
        Directory.CreateDirectory(Path.Combine(root, "gamegen"));
        string script = Path.Combine(root, "gamegen", "m1.gs");
        File.WriteAllText(script, "Quit\n");
        try
        {
            SourceWorkspace workspace = new(root);
            using (var held = new FileStream(script, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            using (var session = new SourceWorldSession(workspace, "M1", TestContext.Current.CancellationToken))
                Assert.Equal("gamegen/m1.gs", session.ScriptPath);
            using CancellationTokenSource canceled = new(); canceled.Cancel();
            Assert.Throws<OperationCanceledException>(() => new SourceWorldSession(workspace, "m1", canceled.Token));
            File.Delete(script);
            Assert.Throws<InvalidDataException>(() => new SourceWorldSession(workspace, "m1", TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, true); }
    }
}
