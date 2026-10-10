using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class MissionTypedDecodeBudgetTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static ZrdNode A(params ZrdNode[] values) => new(Guid.NewGuid(), ZrdKind.Array, 0, "", values);
    private static ZrdNode I(int value) => new(Guid.NewGuid(), ZrdKind.Int, unchecked((uint)value), "", []);
    private static ZrdNode F(float value) => new(Guid.NewGuid(), ZrdKind.Float, BitConverter.SingleToUInt32Bits(value), "", []);
    private static ZrdNode S(string text) => new(Guid.NewGuid(), ZrdKind.String, 0, text, []);
    private static ZrdNode Vector() => A(F(1), F(2), F(3));
    private static ZrdNode Fixture(string name) => name switch
    {
        "puppies.zrd" => A(A(A(S("NANITE"), I(1), Vector(), Vector(), F(1)))),
        "aiv.zrd" => A(S("tank_01"), A(I(0), Vector(), F(0))),
        _ => A(S("node_00"), A(I(0), Vector(), A(I(-1), I(-1), I(-1)))),
    };
    private static ZbdDocument Open(string member, byte[] payload, long budget)
    {
        byte[] bytes = ResourceEditingTests.Archive((member, payload));
        ZbdDocument document = new(Path.Combine(Path.GetTempPath(), "typed-budget-reader.zbd"), new(bytes.Length, DateTime.MinValue),
            new(FormatFamily.Archive, 1, Recognition.Supported, "fixture"), bytes);
        new ArchiveReader(budget).Read(document, Token);
        return document;
    }
    private static (int Records, string[] Notes) Consume(string member, ZbdDocument document)
    {
        var asset = Assert.Single(document.Assets);
        if (member == "puppies.zrd")
        {
            var session = PickupPlacementEditSession.Create([new(MissionDifficulty.Medium, document, asset)], token: Token);
            return (session.Records.Count, session.Diagnostics.ToArray());
        }
        if (member == "aiv.zrd")
        {
            var session = PickupPlacementEditSession.Create([], token: Token);
            session.AddCoordinates([(document, asset)], Token);
            return (session.OtherCoordinates.Count, session.Diagnostics.ToArray());
        }
        var snapshot = MissionAiNetworks.Read([(document, asset)], Token);
        return (snapshot.Networks.Sum(n => n.Nodes.Count), snapshot.Diagnostics.Select(d => d.Message).ToArray());
    }

    [Theory]
    [InlineData("puppies.zrd")]
    [InlineData("aiv.zrd")]
    [InlineData("net_01.zrd")]
    public void MissionConsumersRespectTheArchiveReadersTypedRefusal(string member)
    {
        byte[] payload = ZrdWriter.Write(Fixture(member), Token);
        var normal = Open(member, payload, ArchiveZrdBudget.MaximumAllocation);
        Assert.Equal(1, Consume(member, normal).Records);
        var limited = Open(member, payload, 0);
        var asset = Assert.Single(limited.Assets);
        Assert.Null(asset.Content); Assert.Equal(AssetKind.Raw, asset.Kind);
        Assert.True(asset.Metadata["typed_decode_limited"]!.GetValue<bool>());
        var result = Consume(member, limited);
        Assert.Equal(0, result.Records);
        Assert.Contains(result.Notes, n => n.Contains("shared typed-decoding budget", StringComparison.Ordinal));
        // Refusal concerns automatic decoding: exact member data and archive publication remain available.
        Assert.Equal(payload, limited.Slice(asset.Offset, asset.Length).ToArray());
        ResourceEditSession edits = new(limited);
        Assert.Equal(limited.Bytes.ToArray(), ArchiveWriter.Write(limited, edits.Current.Members, Token));
    }

    [Theory]
    [InlineData("puppies.zrd")]
    [InlineData("aiv.zrd")]
    [InlineData("net_01.zrd")]
    public void RefusedMembersAreNotDecodedAgainBeforeMissionShapeValidation(string member)
    {
        _ = Consume(member, Open(member, ZrdWriter.Write(Fixture(member), Token), 0));
        byte[] payload = ZrdWriter.Write(S(new string('x', 2 * 1024 * 1024)), Token);
        var limited = Open(member, payload, 0);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var result = Consume(member, limited);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, result.Records);
        Assert.Contains(result.Notes, n => n.Contains("shared typed-decoding budget", StringComparison.Ordinal));
        Assert.InRange(allocated, 0, 128 * 1024);
    }
}
