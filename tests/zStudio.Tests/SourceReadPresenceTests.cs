using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class SourceReadPresenceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void InaccessibleExistingDependencyCannotBecomeAMissingPreparedRead()
    {
        if (!OperatingSystem.IsWindows()) return;
        using SourceWorldFixture fixture = new();
        SourceWorkspace workspace = new(fixture.Project);
        const string relative = "data/private/bridge.gltf";
        fixture.Write(relative, "original");
        DirectoryInfo folder = new(fixture.Path("data/private"));
        var denied = folder.GetAccessControl();
        FileSystemAccessRule rule = new(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory | FileSystemRights.ReadAttributes,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Deny);
        denied.AddAccessRule(rule);
        var prepared = workspace.BeginPreparedEdit();
        folder.SetAccessControl(denied);
        try
        {
            // The old existence probe reports absence even though the file was written above.
            Assert.False(File.Exists(fixture.Path(relative)));
            Assert.Throws<UnauthorizedAccessException>(() => prepared.Workspace.Read(relative, Token));
            Assert.Throws<UnauthorizedAccessException>(() => new SourceWorlds.DiskFiles(fixture.Project, null).Exists(relative));
            Assert.Throws<UnauthorizedAccessException>(() => new SourceBuilder.Snapshot(fixture.Project).Exists(relative));
            Assert.Throws<UnauthorizedAccessException>(() => SourceProject.Files(fixture.Project, "data/private", _ => true, token: Token));
            Assert.False(workspace.IsDirty);
            Assert.False(workspace.CanUndo);
            Assert.Equal(0, workspace.Revision);
        }
        finally { denied.RemoveAccessRule(rule); folder.SetAccessControl(denied); }
        Assert.Equal("original", System.Text.Encoding.UTF8.GetString(prepared.Workspace.Read(relative, Token)!));
        Assert.Null(prepared.Workspace.Read("data/absent/bridge.gltf", Token));
        Assert.False(new SourceWorlds.DiskFiles(fixture.Project, null).Exists("data/absent/bridge.gltf"));
        Assert.Empty(SourceProject.Files(fixture.Project, "data/absent", _ => true, token: Token));
    }
}
