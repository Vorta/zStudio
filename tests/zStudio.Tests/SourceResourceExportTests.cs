using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Export;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class SourceResourceExportTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(".zrd", true, false)]
    [InlineData(".zad", true, false)]
    [InlineData(".zrd", true, true)]
    [InlineData(".zad", true, true)]
    [InlineData(".zrd", false, false)]
    [InlineData(".zrd", false, true)]
    public async Task NormalAndJsonExportsUseTheCurrentResourceRepresentation(string extension, bool text, bool edited)
    {
        using Fixture fixture = new();
        byte[] original = text ? "VALUE ( 1 )\n"u8.ToArray() : ZrdWriter.Write(ZrdText.Parse("VALUE ( 1 )", Token), Token);
        string path = Path.Combine(fixture.Source, "sample" + extension);
        await File.WriteAllBytesAsync(path, original, Token);
        var document = await FormatRegistry.Default.OpenAsync(path, Token);
        Assert.Empty(document.Diagnostics);
        if (edited)
        {
            var session = new ResourceEditSession(document);
            var member = Assert.Single(session.Current.Members);
            var value = session.Tree(member, Token).Children[1].Children[0];
            session.Accept(await session.PrepareZrdAsync(member.Id, value.Id, "set", ZrdKind.Int, "2", token: Token));
            document = session.Current.Document;
        }
        Assert.Equal(text ? "zrd-text" : null, document.SourceSyntax);
        var asset = Assert.Single(document.Assets);
        var expected = ZrdDecoder.ReadAsset(document, asset, Token).ToJson(Token);
        Assert.Equal(edited ? 2L : 1L, expected["children"]![1]!["children"]![0]!["value"]!.GetValue<long>());
        // The recorded syntax also selects the shared parser when the optional retained tree is unavailable.
        asset.Content = null;
        Assert.True(JsonNode.DeepEquals(expected, ZrdDecoder.ReadAsset(document, asset, Token).ToJson(Token)));
        using AssetResolver resolver = new(fixture.Source);
        foreach (bool jsonOnly in new[] { false, true })
        {
            var result = await new ExportService(resolver).ExportAsync(document, [asset], fixture.Destination, jsonOnly, token: Token);
            Assert.Equal(1, result.Completed);
            Assert.Empty(result.Errors);
            string raw = Path.Combine(result.Directory, "Zrd", "00000_sample" + extension);
            var json = JsonNode.Parse(await File.ReadAllBytesAsync(raw + ".json", Token))!;
            Assert.True(JsonNode.DeepEquals(expected, jsonOnly ? json["tree"] : json));
            Assert.Equal(!jsonOnly, File.Exists(raw));
            if (!jsonOnly) Assert.Equal(document.Bytes.ToArray(), await File.ReadAllBytesAsync(raw, Token));
            Assert.Empty(Directory.GetFiles(result.Directory, "*.tmp", SearchOption.AllDirectories));
        }
        Assert.Equal(original, await File.ReadAllBytesAsync(path, Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADecodeFailureLeavesNoRawOrJsonAssetInTheExportDestination(bool text)
    {
        using Fixture fixture = new();
        byte[] bytes = text ? "VALUE ("u8.ToArray() : new byte[] { 99, 0, 0, 0, 0, 0, 0, 0 };
        ZbdDocument document = new(Path.Combine(fixture.Source, "invalid.zrd"), new(bytes.Length, DateTime.UtcNow),
            new(FormatFamily.Zrd, null, Recognition.Supported, "fixture"), bytes) { SourceSyntax = text ? "zrd-text" : null };
        document.Add(AssetKind.Zrd, 0, "invalid.zrd", 0, bytes.Length);
        using AssetResolver resolver = new(fixture.Source);
        var result = await new ExportService(resolver).ExportAsync(document, document.Assets, fixture.Destination, false, token: Token);
        Assert.Equal(0, result.Completed);
        Assert.Single(result.Errors);
        Assert.Equal([Path.Combine(result.Directory, "export-report.json")], Directory.GetFiles(result.Directory, "*", SearchOption.AllDirectories));
        var report = JsonNode.Parse(await File.ReadAllBytesAsync(Path.Combine(result.Directory, "export-report.json"), Token))!;
        Assert.Equal(0, report["completed"]!.GetValue<int>());
        Assert.Single(report["errors"]!.AsArray());
    }

    [Fact]
    public void RetainedSourceTreesDoNotBypassTypedDecodeRefusal()
    {
        var document = FormatRegistry.Default.OpenBytes("sample.zad", "VALUE ( 1 )"u8.ToArray(), token: Token);
        var asset = Assert.Single(document.Assets);
        asset.Metadata["typed_decode_limited"] = true;
        Assert.Contains("typed-decoding budget", Assert.Throws<InvalidDataException>(() => ZrdDecoder.ReadAsset(document, asset, Token)).Message);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "zstudio-resource-export-" + Guid.NewGuid().ToString("N"));
        internal string Source => Path.Combine(root, "source");
        internal string Destination => Path.Combine(root, "export");
        internal Fixture() => Directory.CreateDirectory(Source);
        public void Dispose() => Directory.Delete(root, true);
    }
}
