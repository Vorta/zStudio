using System.Text;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class TerrainReferenceStructureTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Main = "data/m1/models/m1.gltf";
    private const string Auxiliary = "data/m1/models/aux.gltf";
    private static readonly byte[] Root = Encoding.UTF8.GetBytes("""
        {"asset":{"version":"2.0"},"nodes":[{}, {"extras":{"recoil":{"ref":"aux.gltf"}}}],"scenes":[{"nodes":[0]}],"scene":0}
        """);

    [Theory]
    [InlineData("[0]", "not an object")]
    [InlineData("[{}, {}, {}]", "at most 2")]
    [InlineData("{}", "must be an array")]
    public void InactiveReferencesReceiveColdStructuralAdmission(string nodes, string message)
    {
        // The normal reader accepts the main scene without opening its inactive engine reference.
        var main = GltfDocument.Read(Root, _ => throw new InvalidOperationException("inactive reference was resolved"), Token);
        Assert.Single(main.Roots);
        byte[] auxiliary = Encoding.UTF8.GetBytes("{\"nodes\":" + nodes + "}");
        List<string> read = [];
        byte[]? Read(string path, ProjectReadLimits limits)
        { read.Add(path); var bytes = path == Main ? Root : auxiliary; limits.Validate(bytes); return bytes; }
        var error = Assert.Throws<InvalidDataException>(() => SourceTerrain.Referenced(Main, Read, Token, maximumNodes: 2));
        Assert.Contains(message, error.Message);
        Assert.Equal(new[] { Main, Auxiliary }, read);
    }

    [Fact]
    public void ActualCreateRefusesMalformedUnusedAuxiliaryWithoutAnUndoEntry()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write(Main, Encoding.UTF8.GetString(Root));
        fixture.Write(Auxiliary, "{\"nodes\":[0]}");
        SourceWorkspace workspace = new(fixture.Project);
        var error = Assert.Throws<InvalidDataException>(() => SourceTerrain.Create(workspace, Main, fixture.Tank, ["hull"], token: Token));
        Assert.Contains("not an object", error.Message);
        Assert.False(workspace.IsDirty); Assert.False(workspace.CanUndo); Assert.Equal(0, workspace.Revision);
        Assert.Equal(Root, File.ReadAllBytes(fixture.Path(Main)));
        fixture.Write(Auxiliary, "{\"nodes\":[]}");
        Assert.NotNull(SourceTerrain.Create(workspace, Main, fixture.Tank, ["hull"], token: Token));
        Assert.Equal(1, workspace.UndoCount);
    }

    [Fact]
    public void AggregateWorkChargesRepeatedIdentityBeforeDecodingOrNormalizingIt()
    {
        byte[] auxiliary = Encoding.UTF8.GetBytes("{\"nodes\":[]}");
        byte[]? Read(string path, ProjectReadLimits limits) => path == Main ? Root : auxiliary;
        long used = 0;
        Assert.Contains(Auxiliary, SourceTerrain.Referenced(Main, Read, Token, reserved: value => used = value));
        var error = Assert.Throws<InvalidDataException>(() => SourceTerrain.Referenced(Main, Read, Token, maximumWork: used - 1));
        Assert.Contains("reference inspection work", error.Message);
        Assert.Contains(Auxiliary, SourceTerrain.Referenced(Main, Read, Token, maximumWork: used));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void CancellationAtAdmissionOrDuringReferencesLeavesRetryComplete(int reservation)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        int calls = 0;
        byte[]? Read(string path, ProjectReadLimits limits) => path == Main ? Root : "{\"nodes\":[]}"u8.ToArray();
        Assert.ThrowsAny<OperationCanceledException>(() => SourceTerrain.Referenced(Main, Read, cancel.Token,
            reserved: _ => { if (calls++ == reservation) cancel.Cancel(); }));
        Assert.Contains(Auxiliary, SourceTerrain.Referenced(Main, Read, Token));
    }
}
