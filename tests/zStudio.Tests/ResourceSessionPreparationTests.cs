using System.Security.Cryptography;
using System.Text;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ResourceSessionPreparationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Text = "# untouched comment\r\n( VALUE ( 7 ) OTHER ( 9 ) )\r\n";

    [Fact]
    public async Task CancellationAfterSyntaxKeepsTheSourceAndRetryPreservesHistoryIdentities()
    {
        byte[] bytes = Encoding.ASCII.GetBytes(Text);
        var document = FormatRegistry.Default.OpenBytes("values.zrd", bytes, token: Token);
        Assert.Equal("zrd-text", document.SourceSyntax);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        bool hydrated = false;
        var error = Assert.Throws<OperationCanceledException>(() => new ResourceEditSession(document, canceled.Token, () =>
        {
            hydrated = true;
            canceled.Cancel();
        }));
        Assert.True(hydrated);
        Assert.Equal(canceled.Token, error.CancellationToken);
        Assert.Equal(bytes, document.Bytes.ToArray());

        var session = new ResourceEditSession(document, Token);
        Assert.Same(document, session.Current.Document);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), session.Current.Hash);
        var member = Assert.Single(session.Current.Members);
        var tree = session.Tree(member, Token);
        var value = tree.Children[0].Children[1].Children[0];
        Assert.Equal(7u, value.Bits);
        session.Accept(await session.PrepareZrdAsync(member.Id, value.Id, "set", value: "8", token: Token));
        Assert.Equal(Text.Replace("( 7 )", "( 8 )"), Encoding.ASCII.GetString(session.Current.Document.Bytes.Span));
        Assert.Equal(member.Id, Assert.Single(session.Current.Members).Id);
        session.UndoRedo(false);
        Assert.Equal(bytes, session.Current.Document.Bytes.ToArray());
        Assert.False(session.IsDirty);
        session.UndoRedo(true);
        Assert.Equal(value.Id, session.Tree(Assert.Single(session.Current.Members), Token).Children[0].Children[1].Children[0].Id);
        Assert.Equal(bytes, document.Bytes.ToArray());
    }

    [Fact]
    public void BinaryResourcePreparationKeepsAuthoredPayloadAndRejectsCanceledWork()
    {
        byte[] bytes = ZrdWriter.Write(ZrdNode.Create(ZrdKind.Int, "17"), Token);
        var document = FormatRegistry.Default.OpenBytes("values.zrd", bytes, token: Token);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => new ResourceEditSession(document, canceled.Token));
        var session = new ResourceEditSession(document, Token);
        Assert.Equal(bytes, Assert.Single(session.Current.Members).Data.ToArray());
        Assert.Same(document.Assets.Single().Content, Assert.Single(session.Current.Members).Tree);
        Assert.False(session.IsDirty);
    }

    [Fact]
    public void CancellableSlotProjectionPreservesFreedSlotAndNodeIdentities()
    {
        GameZWorld world = new();
        WorldNode first = new("same", WorldNodeClass.Object3D), second = new("same", WorldNodeClass.Object3D);
        world.Nodes.Add(first); world.Nodes.Add(second); world.FreedSlots.Add(1, new byte[196]);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => GameZWriter.NodeSlots(world, canceled.Token));
        var slots = GameZWriter.NodeSlots(world, Token);
        Assert.Equal(0, slots[first]); Assert.Equal(2, slots[second]); Assert.Equal(2, slots.Count);
    }

    [Fact]
    public void HierarchyCancellationDuringEnumerationLeavesGraphUnchangedAndRetryStillChecksCycles()
    {
        WorldNode first = new("first", WorldNodeClass.Object3D), second = new("second", WorldNodeClass.Object3D);
        first.Children.Add(second); second.Parents.Add(first);
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        IEnumerable<WorldNode> Nodes()
        {
            yield return first;
            canceled.Cancel();
            yield return second;
        }
        Assert.Throws<OperationCanceledException>(() => WorldUpdate.CheckHierarchy(Nodes(), canceled.Token));
        Assert.Same(second, Assert.Single(first.Children)); Assert.Same(first, Assert.Single(second.Parents));
        WorldUpdate.CheckHierarchy([first, second], Token);
        second.Children.Add(first);
        Assert.Throws<InvalidDataException>(() => WorldUpdate.CheckHierarchy([first, second], Token));
    }
}
