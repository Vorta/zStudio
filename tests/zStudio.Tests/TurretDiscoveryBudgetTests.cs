using System.Buffers.Binary;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed partial class AnimationTests
{
    [Fact]
    public void ReaderBackedAbsentTurretsSpendTheWholeMissionBudgetWithoutChangingInputs()
    {
        var token = TestContext.Current.CancellationToken;
        var context = DiscoveryContext();
        var ai = DiscoveryPatterns(16);
        string[] before = NodeState(context.Scene); string input = ai.ToJsonString();
        var error = Assert.Throws<IOException>(() => MissionSceneLoader.BuildWithBudget(context.World, new(token),
            context.Package, null, null, null, token: token, ai: ai, maximumBindingWork: 4000));
        Assert.Contains("Animation binding", error.Message); // Must escape the malformed-ai fallback.
        Assert.Equal(before, NodeState(context.Scene)); Assert.Equal(input, ai.ToJsonString());
        var retry = MissionSceneLoader.Build(context.World, context.Package, null, null, null, token: token, ai: ai);
        Assert.Equal(before, NodeState(retry.Scene));
        Assert.DoesNotContain(retry.Diagnostics, d => d.Contains("reset animation", StringComparison.Ordinal));
        Assert.NotNull(MissionSceneLoader.BuildWithBudget(context.World, new(token), context.Package,
            null, null, null, token: token, ai: DiscoveryPatterns(1), maximumBindingWork: 4000));
    }

    [Fact]
    public void TurretScanCancellationStopsBeforeAnyResetAndFreshRetrySucceeds()
    {
        var token = TestContext.Current.CancellationToken;
        var context = DiscoveryContext(); string[] before = NodeState(context.Scene);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        AnimationBindingOperation work = new(context, cancel.Token, reserved: used => { if (used >= 500) cancel.Cancel(); });
        Assert.ThrowsAny<OperationCanceledException>(() => MissionSceneLoader.InitializeTurrets(context.Scene, context,
            DiscoveryPatterns(16), [], [], cancel.Token, work));
        Assert.Equal(before, NodeState(context.Scene));
        MissionSceneLoader.InitializeTurrets(context.Scene, context, DiscoveryPatterns(16), [], [], token);
        Assert.Equal(before, NodeState(context.Scene));
    }

    [Fact]
    public void InvalidPatternWarningsAreBoundedBeforeFormattingAndDiscloseOmissions()
    {
        var context = DiscoveryContext(); JsonArray rows = [];
        for (int i = 0; i < 1100; i++) { rows.Add(Str(new string('x', 512) + i)); rows.Add(Arr()); }
        var ai = Arr(Str("TURRET"), new JsonObject { ["type"] = "array", ["children"] = rows });
        var mission = MissionSceneLoader.Build(context.World, context.Package, null, null, null,
            token: TestContext.Current.CancellationToken, ai: ai);
        Assert.Contains(BoundedDiagnostics.OmissionNotice, mission.Diagnostics);
        Assert.All(mission.Diagnostics, d => Assert.InRange(d.Length, 0, BoundedDiagnostics.MaximumMessageCharacters));
        Assert.Equal(NodeState(context.Scene), NodeState(mission.Scene));
    }

    [Fact]
    public void IndexedResetNamesKeepFirstExactEntryAndExplicitNamedDefaultPrecedence()
    {
        var context = TurretFixture();
        var first = ResetEntry(2, "override"); Position(first, 123); context.Package.Entries.Add(first);
        var duplicate = ResetEntry(3, "override"); Position(duplicate, 456); context.Package.Entries.Add(duplicate);
        var otherCase = ResetEntry(4, "OVERRIDE"); Position(otherCase, 789); context.Package.Entries.Add(otherCase);
        var result = BuildTurrets(context, "override");
        Assert.Equal(123, SceneBuilder.LocalTransform(result.Scene.Nodes[2]).M41);
        Assert.Equal(123, SceneBuilder.LocalTransform(result.Scene.Nodes[7]).M41);
        Assert.Equal("override", result.Scene.Nodes[2].Metadata.Text("preview_turret_reset"));
        static void Position(AnimationEntry entry, int x)
        { var move = AnimationCatalog.Create(7); move.SetShort(28, -100); move.SetVector(16, new(x, 0, 0)); entry.Primary.Events.Add(move); }
    }

    private static JsonObject DiscoveryPatterns(int count)
    {
        List<ZrdNode> rows = [];
        for (int i = 0; i < count; i++) { rows.Add(S($"x{i:00000}*")); rows.Add(A([])); }
        var resource = A([S("TURRET"), A(rows)]);
        var token = TestContext.Current.CancellationToken;
        return ZrdDecoder.Read(ZrdWriter.Write(resource, token), token).ToJson(token);
        static ZrdNode S(string value) => new(Guid.NewGuid(), ZrdKind.String, 0, value, []);
        static ZrdNode A(IReadOnlyList<ZrdNode> children) => new(Guid.NewGuid(), ZrdKind.Array, 0, "", children);
    }

    private static AnimationPreviewContext DiscoveryContext()
    {
        var token = TestContext.Current.CancellationToken;
        byte[] prefix = new byte[72]; BinaryPrimitives.WriteUInt32LittleEndian(prefix, 0x08170616);
        BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(4), 28);
        AnimationPackage package = new() { Prefix = prefix, Tail = [] };
        var empty = new AnimationEntry(new byte[308], 0, 72); empty.SetFloat(164, -1); package.Entries.Add(empty);
        package = AnimationPackage.Read(AnimationWriter.Write(package, token), token);
        GameZWorld world = new(); WorldNode top = new("world", WorldNodeClass.World); world.Nodes.Add(top);
        for (int i = 0; i < 64; i++)
        {
            WorldNode node = new($"n{i:000000}", WorldNodeClass.Object3D) { Flags = WorldGltf.DefaultCarried };
            node.SetPayloadInt(0, 0x28); top.Children.Add(node); node.Parents.Add(top); world.Nodes.Add(node);
        }
        var document = FormatRegistry.Default.OpenBytes("gamez.zbd", GameZWriter.Write(world, token), token: token);
        Assert.NotNull(document.Scene);
        return new() { World = document, Package = package };
    }
}
