using System.Text.Json.Nodes;
using zStudio.Core;
using zStudio.Core.Export;
using zStudio.Core.Formats;
using zStudio.Core.Sources;
using Xunit;

namespace zStudio.Tests;

public sealed class SourceResourceExportTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SourceWorldAssetExportUsesFrozenSourceTexturesWithoutArchives()
    {
        using SourceWorldFixture fixture = new();
        var source = await SourceWorlds.LoadSceneAsync(fixture.Project, "m1", token: Token);
        byte[] expected = await File.ReadAllBytesAsync(fixture.Path("data/m1/textures/rock.png"), Token);
        // Export the accepted snapshot even if the next disk revision has a different image.
        fixture.Write("data/m1/textures/rock.png", "changed outside the accepted scene");
        using AssetResolver resolver = new(fixture.Project);
        var result = await new ExportService(resolver).ExportAsync(source,
            [source.Assets.Single(a => a.Kind == AssetKind.World)], Path.Combine(fixture.Root, "export"), false, token: Token);
        Assert.Equal(1, result.Completed); Assert.Empty(result.Errors);
        string image = Assert.Single(Directory.GetFiles(result.Directory, "*.png", SearchOption.AllDirectories));
        Assert.Equal(expected, await File.ReadAllBytesAsync(image, Token));
        string material = Assert.Single(Directory.GetFiles(result.Directory, "*.mtl", SearchOption.AllDirectories));
        Assert.Contains("map_Kd " + Path.GetFileName(image), await File.ReadAllTextAsync(material, Token));
        int root = source.Scene!.Nodes.Single(node => node.Name == "ground").Index;
        var exporter = new ExportService(resolver);
        var bundle = await exporter.ExportModelBundleAsync(source, root, Path.Combine(fixture.Root, "bundle"), token: Token);
        Assert.Equal(1, bundle.Models);
        Assert.True(File.Exists(Path.Combine(bundle.Directory, "local", "model_0.obj")));
        foreach (string texture in Directory.GetFiles(bundle.Directory, "*.png", SearchOption.AllDirectories))
            Assert.Equal(expected, await File.ReadAllBytesAsync(texture, Token));
        string acceptedIdentity = JsonNode.Parse(await File.ReadAllBytesAsync(bundle.Manifest, Token))!["sourceSha256"]!.GetValue<string>();
        Assert.NotEqual(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Array.Empty<byte>())), acceptedIdentity);
        fixture.Write("data/m1/textures/rock.png", expected);
        string script = await File.ReadAllTextAsync(fixture.Path("gamegen/m1.gs"), Token);
        var changed = await SourceWorlds.LoadSceneAsync(fixture.Project, "m1",
            new Dictionary<string, byte[]> { ["gamegen/m1.gs"] = System.Text.Encoding.ASCII.GetBytes(script.Replace("# no vehicles", "FindNode ground\r\nObject3DTranslate 10 20 30", StringComparison.Ordinal)) }, Token);
        var changedBundle = await exporter.ExportModelBundleAsync(changed, root, Path.Combine(fixture.Root, "bundle"), token: Token);
        string changedIdentity = JsonNode.Parse(await File.ReadAllBytesAsync(changedBundle.Manifest, Token))!["sourceSha256"]!.GetValue<string>();
        Assert.NotEqual(acceptedIdentity, changedIdentity);
        var repeated = await exporter.ExportModelBundleAsync(source, root, Path.Combine(fixture.Root, "bundle"), token: Token);
        Assert.Equal(acceptedIdentity, JsonNode.Parse(await File.ReadAllBytesAsync(repeated.Manifest, Token))!["sourceSha256"]!.GetValue<string>());
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.zbd", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(".zrd", true)]
    [InlineData(".zad", true)]
    [InlineData(".zrd", false)]
    public async Task NormalAndJsonExportsUseTheCurrentResourceRepresentation(string extension, bool text)
    {
        using Fixture fixture = new();
        byte[] original = text ? "VALUE ( 1 )\n"u8.ToArray() : ZrdWriter.Write(ZrdText.Parse("VALUE ( 1 )", Token), Token);
        string path = Path.Combine(fixture.Source, "sample" + extension);
        await File.WriteAllBytesAsync(path, original, Token);
        var document = await FormatRegistry.Default.OpenAsync(path, Token);
        Assert.Empty(document.Diagnostics);
        Assert.Equal(text ? "zrd-text" : null, document.SourceSyntax);
        var asset = Assert.Single(document.Assets);
        var expected = ZrdDecoder.ReadAsset(document, asset, Token).ToJson(Token);
        Assert.Equal(1L, expected["children"]![1]!["children"]![0]!["value"]!.GetValue<long>());
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
        AssetDocument document = new(Path.Combine(fixture.Source, "invalid.zrd"), new(bytes.Length, DateTime.UtcNow),
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
