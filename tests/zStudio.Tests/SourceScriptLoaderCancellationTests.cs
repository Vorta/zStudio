using System.Buffers.Binary;
using System.Text;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class SourceScriptLoaderCancellationTests
{
    [Theory]
    [InlineData("source.gs")]
    [InlineData("source.gw")]
    public void ActualSourceLoaderPropagatesCancellationAndNextOpenKeepsAuthoredRows(string path)
    {
        const string text = "# heading\r\nFindNode tank\r\nObject3DTranslate 1, 2, 3 # retained\r\n";
        byte[] bytes = Encoding.Latin1.GetBytes(text), original = [.. bytes];
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        Assert.Equal(FormatRegistry.SourceScriptDescription,
            FormatRegistry.Probe(bytes.AsSpan(0, Math.Min(36, bytes.Length)), bytes.AsSpan(bytes.Length - 8), bytes.Length, Path.GetExtension(path)).Description);
        Assert.Throws<OperationCanceledException>(() => FormatRegistry.Default.OpenBytes(path, bytes, token: canceled.Token));
        Assert.Equal(original, bytes);

        var document = FormatRegistry.Default.OpenBytes(path, bytes, token: TestContext.Current.CancellationToken);
        Assert.Empty(document.Diagnostics);
        Assert.Equal("gamegen-script", document.SourceSyntax);
        var script = Assert.IsType<ScriptContent>(Assert.Single(document.Assets).Content);
        Assert.Equal(text, script.GetText(TestContext.Current.CancellationToken));
        Assert.Equal(new[] { "FindNode", "tank" }, script.Instructions[0]);
        Assert.Equal(new[] { "Object3DTranslate", "1", "2", "3" }, script.Instructions[1]);
        Assert.Equal(original, document.Bytes.ToArray());
    }

    [Fact]
    public void StructuralProbeStillPrecedesSourceFilenameAndRetainsUnsupportedBytes()
    {
        byte[] bytes = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x08971119);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 99);
        var document = FormatRegistry.Default.OpenBytes("compiled.gs", bytes, token: TestContext.Current.CancellationToken);
        Assert.Equal(Recognition.UnsupportedVersion, document.Probe.Recognition);
        Assert.Null(document.SourceSyntax);
        Assert.Equal(AssetKind.Raw, Assert.Single(document.Assets).Kind);
        Assert.Equal(bytes, document.Bytes.ToArray());
    }
}
