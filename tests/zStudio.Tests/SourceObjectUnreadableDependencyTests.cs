using System.IO;
using System.Numerics;
using System.Security.AccessControl;
using System.Security.Principal;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class SourceObjectUnreadableDependencyTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Bridge = "data/m2/models/bft/bridge.gltf";
    private const string Rotation = "gamegen/rotate.gs";

    [Fact]
    public void InaccessibleReferencedModelCannotLookAbsentToCrossMissionProtection()
    {
        if (!OperatingSystem.IsWindows()) return;
        using SourceWorldFixture fixture = new();
        Configure(fixture);
        const string hiddenModel = "data/common/models/blocked/bridge.gltf";
        fixture.Write(Bridge, """
            {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0]}],"nodes":[{"extras":{"recoil":{"ref":"../../../common/models/blocked/bridge.gltf"}}}]}
            """);
        fixture.Write(hiddenModel, """
            {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0]}],"nodes":[{"extras":{"recoil":{"ref":"../../../m2/models/bft/tank.gltf"}}}]}
            """);
        SourceWorkspace workspace = new(fixture.Project);
        WorldAssembler assembler = new(new SourceWorlds.DiskFiles(fixture.Project, null), Token);
        var world = assembler.Assemble("m1.gs");
        var hull = world.Nodes.Single(n => n.Name == "hull");
        var pose = ObjectTransform.Of(hull);
        SourceEditPlan Edit() => SourceObjectEdits.PlanTransform(workspace, hull.Name, assembler.Provenance[hull], assembler.Executions,
            pose with { Position = new(2, 0, 0) }, Token, "m1", pose, world: world, write: assembler.WriteInstruction);
        Assert.Contains("Object3DRotate", Assert.Throws<InvalidDataException>(Edit).Message);

        DirectoryInfo folder = new(fixture.Path("data/common/models/blocked"));
        var denied = folder.GetAccessControl();
        FileSystemAccessRule deny = new(WindowsIdentity.GetCurrent().User!, FileSystemRights.ListDirectory | FileSystemRights.ReadAttributes,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Deny);
        denied.AddAccessRule(deny);
        folder.SetAccessControl(denied);
        try
        {
            // File.Exists conceals this access failure as absence; the shared source reader must not do so.
            Assert.False(File.Exists(fixture.Path(hiddenModel)));
            var error = Assert.Throws<IOException>(Edit);
            Assert.Contains(hiddenModel, error.Message);
            Assert.IsType<UnauthorizedAccessException>(error.InnerException);
            Assert.False(workspace.IsDirty);
            Assert.False(workspace.CanUndo);
            Assert.Equal(0, workspace.Revision);
        }
        finally { denied.RemoveAccessRule(deny); folder.SetAccessControl(denied); }
        Assert.True(File.Exists(fixture.Path(hiddenModel)));
        Assert.Contains("Object3DRotate", Assert.Throws<InvalidDataException>(Edit).Message);
    }

    [Theory]
    [InlineData("gamegen/m2.gs")]
    [InlineData(Rotation)]
    [InlineData(Bridge)]
    public void LockedOtherMissionDependenciesRefuseBeforePublishingAndRetryNormally(string locked)
    {
        using SourceWorldFixture fixture = new();
        Configure(fixture);
        SourceWorkspace workspace = new(fixture.Project);
        WorldAssembler assembler = new(new SourceWorlds.DiskFiles(fixture.Project, null), Token);
        var world = assembler.Assemble("m1.gs");
        var hull = world.Nodes.Single(n => n.Name == "hull");
        var pose = ObjectTransform.Of(hull);
        byte[] modelBefore = File.ReadAllBytes(fixture.Path(fixture.Tank));
        var other = new WorldAssembler(new SourceWorlds.DiskFiles(fixture.Project, null), Token).Assemble("m2.gs");
        Assert.Equal(new Vector3(0, 45, 0), ObjectTransform.Of(other.Nodes.Single(n => n.Name == "hull")).RotationDegrees);
        SourceEditPlan Edit() => SourceObjectEdits.PlanTransform(workspace, hull.Name, assembler.Provenance[hull], assembler.Executions,
            pose with { Position = new(2, 0, 0) }, mission: "m1", current: pose, world: world, write: assembler.WriteInstruction, token: Token);

        // Positive control: this precise other-mission instruction prevents the shared-file edit while readable.
        Assert.Contains("Object3DRotate", Assert.Throws<InvalidDataException>(Edit).Message);
        using (FileStream held = new(fixture.Path(locked), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var failure = Assert.Throws<IOException>(Edit);
            Assert.Contains(locked, failure.Message);
            Assert.Contains("could not be read", failure.Message);
            Assert.IsAssignableFrom<IOException>(failure.InnerException);
            Assert.False(workspace.IsDirty);
            Assert.False(workspace.CanUndo);
            Assert.Equal(0, workspace.Revision);
        }
        // A failed attempt cannot cache absence: after releasing the lock the ordinary semantic refusal returns.
        Assert.Contains("Object3DRotate", Assert.Throws<InvalidDataException>(Edit).Message);
        Assert.Equal(modelBefore, File.ReadAllBytes(fixture.Path(fixture.Tank)));
        Assert.False(workspace.IsDirty);

        // Removing the competing instruction makes the same edit valid, even with genuinely absent search candidates
        // and an absent sourced script. The previous failure leaves neither history nor a stale failed-read cache.
        fixture.Write(Rotation, "# no transform\n");
        var plan = Edit();
        Assert.Single(plan.Changes);
        Assert.NotNull(workspace.Apply(plan.Label, plan.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), Token));
        var rebuilt = new WorldAssembler(new SourceWorlds.DiskFiles(fixture.Project, workspace.Overlay()), Token).Assemble("m2.gs");
        Assert.Equal(new Vector3(2, 0, 0), ObjectTransform.Of(rebuilt.Nodes.Single(n => n.Name == "hull")).Position);
        Assert.Equal(modelBefore, File.ReadAllBytes(fixture.Path(fixture.Tank))); // Still an unsaved source edit.
    }

    private static void Configure(SourceWorldFixture fixture)
    {
        string m1 = File.ReadAllText(fixture.Path("gamegen/m1.gs"));
        fixture.Write("gamegen/m1.gs", m1.Replace("GameZWriteZBDFile", "SetModelDirectory ../data/m2/models/bft\nLoadGameGen tank.gltf tank\nGameZWriteZBDFile"));
        fixture.Write(Bridge, """
            {"asset":{"version":"2.0"},"scene":0,"scenes":[{"nodes":[0]}],"nodes":[{"extras":{"recoil":{"ref":"tank.gltf"}}}]}
            """);
        fixture.Write("gamegen/m2.gs", "NewWorld world\nSetModelDirectory ../data/m2/models/bft\nSetModelDirectory ../data/missing\nLoadGameGen bridge.flt tank\nFindNode tank\nFindSubNode hull\nsource absent.gs\nsource rotate.gs\nGameZWriteZBDFile world.zbd\n");
        fixture.Write(Rotation, "Object3DRotate 0 45 0\n");
    }
}
