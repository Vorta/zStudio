using zStudio.Core;
using zStudio.Core.Formats;
using zStudio.Core.Sources;
using zStudio.Core.Worlds;
using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Nodes;
using zStudio.Core.Export;
using Xunit;

namespace zStudio.Tests;

public sealed class Mw3SourceProjectTests
{
    // Consequential editing regression: requantization must never use the transparent key for an opaque texel.
    [Fact]
    public void EditedIndexedImageKeepsOpaqueDarkPixelsOutOfTheTransparentPaletteKey()
    {
        var token = TestContext.Current.CancellationToken;
        using MemoryStream source = new(); using BinaryWriter writer = new(source);
        writer.Write(0); writer.Write(1); writer.Write(0); writer.Write(1); writer.Write(0); writer.Write(0);
        byte[] name = new byte[32]; name[0] = (byte)'k'; writer.Write(name); writer.Write(64); writer.Write(-1);
        writer.Write(2); writer.Write((ushort)8); writer.Write((ushort)8); writer.Write(0); writer.Write((ushort)2); writer.Write((ushort)0);
        writer.Write(Enumerable.Repeat((byte)1, 64).ToArray()); writer.Write((ushort)0); writer.Write(ushort.MaxValue);
        var doc = FormatRegistry.Default.OpenBytes("texture.zbd", source.ToArray(), token: token); Dictionary<string, byte[]> files = [];
        var recipe = Mw3Project.Extract(doc, "data/texture", (p, b) => files.Add(p, b), token);
        byte[] pixels = new byte[8 * 8 * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i + 1] = 255; pixels[i + 3] = 255; }
        pixels[1] = 0; pixels[2] = 8; // Dark blue would otherwise choose the reserved zero over the green representative.
        files["data/texture/00000.png"] = PngEncoder.Encode(new(8, 8, pixels), token);
        files["gamegen/texture.json"] = Mw3SourceRecipe.Text(recipe);
        var built = FormatRegistry.Default.OpenBytes("texture.zbd", Mw3Project.BuildRecipe("gamegen/texture.json", p => files[p], token), token: token);
        var image = TextureDecoder.Decode(built, built.Assets.Single(), token);
        Assert.All(Enumerable.Range(0, 64), i => Assert.Equal((byte)255, image.Rgba[i * 4 + 3]));
        Assert.True(((TextureInfo)built.Assets.Single().Content!).PaletteCount > 0);
    }

    [Fact]
    public async Task StandaloneMw3ProjectChecksExportsDeterministicallyAndRejectsUnsafeDestinations()
    {
        string scratch = Path.Combine(Path.GetTempPath(), "zstudio-mw3-fixture-" + Guid.NewGuid().ToString("N"));
        var token = TestContext.Current.CancellationToken;
        try
        {
            string corpus = Path.Combine(scratch, "corpus"), project = Path.Combine(scratch, "project"); Mw3TestCorpus.Create(corpus);
            var report = await SourceExtractor.ExtractAsync(corpus, project, token: token); Assert.Equal(GameVariant.MechWarrior3, report.Game);
            // Sources must suffice after the original corpus has moved away.
            Directory.Move(corpus, corpus + "-offline");
            Assert.Equal(0, (await SourceBuilder.CheckAsync(project, token: token)).Failed);
            await Assert.ThrowsAsync<InvalidDataException>(() => SourceBuilder.ExportAsync(project, Path.Combine(project, "out"), token: token));
            await Assert.ThrowsAsync<InvalidDataException>(() => SourceBuilder.CheckAsync(project, token: token, dithering: TextureDithering.Ordered));
            string first = Path.Combine(scratch, "first"), second = Path.Combine(scratch, "second");
            await SourceBuilder.ExportAsync(project, first, token: token); await SourceBuilder.ExportAsync(project, second, token: token);
            foreach (var path in Directory.GetFiles(first, "*", SearchOption.AllDirectories))
                Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(Path.Combine(second, Path.GetRelativePath(first, path))));
            await Assert.ThrowsAsync<IOException>(() => SourceBuilder.ExportAsync(project, first, token: token));
            string resource = Path.Combine(project, "data", "reader.sources", "00000.zrd");
            await File.WriteAllTextAsync(resource, "( VALUE 2 )", token);
            await SourceBuilder.ExportAsync(project, second, overwrite: true, token: token);
            var edited = await FormatRegistry.Default.OpenAsync(Path.Combine(second, "reader.zbd"), token);
            Assert.Equal(2u, ((ZrdNode)edited.Assets.Single().Content!).Children.Single().Children[1].Bits);
        }
        finally { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
    }

    [Fact]
    public void PositionSourcesRejectGrowthAndDiscardedMaterialChanges()
    {
        var token = TestContext.Current.CancellationToken;
        Vector3[] vertices = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY];
        GameModel Model(Vector3[] positions) => new(0, positions, [], [], [new(0, 0, [0, 1, 2], [], [], new())], new JsonObject { ["lights"] = new JsonArray() });
        byte[] raw = new byte[36]; for (int i = 0; i < 3; i++) { BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(i * 12), vertices[i].X); BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(i * 12 + 4), vertices[i].Y); }
        Dictionary<string, byte[]> files = []; var tape = new Mw3SourceRecipe.Tape(raw, "data/model", (p, b) => files.Add(p, b), token);
        tape.Positions(Model(vertices)); var recipe = tape.Finish();
        Assert.Equal(raw, Mw3SourceRecipe.Build(recipe, p => files[p], token));
        files["data/model/model-00000.gltf"] = WorldGltf.ExportMw3Model(Model([new(0.25f, 0.25f, 0), Vector3.UnitX, Vector3.UnitY]), "unused.bin", token).Json;
        Assert.Equal(0.25f, BinaryPrimitives.ReadSingleLittleEndian(Mw3SourceRecipe.Build(recipe, p => files[p], token)));
        var changed = WorldGltf.ExportMw3Model(Model([new(10, 0, 0), Vector3.UnitX, Vector3.UnitY]), "unused.bin", token);
        files["data/model/model-00000.gltf"] = changed.Json;
        Assert.Contains("bounds", Assert.Throws<InvalidDataException>(() => Mw3SourceRecipe.Build(recipe, p => files[p], token)).Message);
        var original = WorldGltf.ExportMw3Model(Model(vertices), "unused.bin", token); var gltf = JsonNode.Parse(original.Json)!;
        gltf["materials"]![0]!["doubleSided"] = true; files["data/model/model-00000.gltf"] = Mw3SourceRecipe.Text(gltf);
        Assert.Contains("material", Assert.Throws<InvalidDataException>(() => Mw3SourceRecipe.Build(recipe, p => files[p], token)).Message);
        gltf = JsonNode.Parse(original.Json)!;
        gltf["images"] = new JsonArray(new JsonObject { ["uri"] = "data:image/png;base64,AA==" });
        gltf["textures"] = new JsonArray(new JsonObject { ["source"] = 0 });
        gltf["materials"]![0]!["pbrMetallicRoughness"]!["baseColorTexture"] = new JsonObject { ["index"] = 0 };
        files["data/model/model-00000.gltf"] = Mw3SourceRecipe.Text(gltf);
        Assert.Throws<InvalidDataException>(() => Mw3SourceRecipe.Build(recipe, p => files[p], token));
        Dictionary<string, byte[]> arrays = []; var arrayTape = new Mw3SourceRecipe.Tape(new byte[24], "data/arrays", (p, b) => arrays.Add(p, b), token);
        arrayTape.Array(0, 6, "f32", "normals"); var arrayRecipe = arrayTape.Finish();
        var part = JsonNode.Parse(arrays["data/arrays/records-00000.json"])!; part[0]!["values"]!.AsArray().Add(0);
        arrays["data/arrays/records-00000.json"] = Mw3SourceRecipe.Text(part);
        Assert.Contains("element count", Assert.Throws<InvalidDataException>(() => Mw3SourceRecipe.Build(arrayRecipe, p => arrays[p], token)).Message);
    }

    // Must-have workflow: initialization must produce a complete standalone project, not a dependency on the corpus.
    [Fact]
    public async Task OptionalMw3CorpusReconstructsAndRebuildsEveryOutput()
    {
        string? corpus = Environment.GetEnvironmentVariable("ZSTUDIO_MW3_CORPUS"); if (corpus == null) return;
        var token = TestContext.Current.CancellationToken;
        string scratch = Path.Combine(Path.GetTempPath(), "zstudio-mw3-sources-" + Guid.NewGuid().ToString("N"));
        string project = Path.Combine(scratch, "project"), destination = Path.Combine(scratch, "export");
        try
        {
            var before = Directory.GetFiles(corpus, "*.zbd", SearchOption.AllDirectories).ToDictionary(p => p, p => SourceProject.Sha256(File.ReadAllBytes(p)));
            var reconstructed = await SourceExtractor.ExtractAsync(corpus, project, token: token);
            Assert.Equal(GameVariant.MechWarrior3, reconstructed.Game); Assert.Empty(reconstructed.NotReconstructed);
            var plans = SourceBuilder.Plan(project, token: token); Assert.Equal(before.Count, plans.Count);
            var report = await SourceBuilder.ExportAsync(project, destination, token: token);
            Assert.Equal(0, report.Failed); Assert.Equal(before.Count, report.Built);
            foreach (var input in before)
            {
                string relative = Path.GetRelativePath(corpus, input.Key); string output = Path.Combine(destination, relative);
                var original = await FormatRegistry.Default.OpenAsync(input.Key, token); var rebuilt = await FormatRegistry.Default.OpenAsync(output, token);
                Assert.DoesNotContain(rebuilt.Diagnostics, d => d.Severity == "Error");
                if (original.Probe.Family is FormatFamily.GameZ or FormatFamily.Animation)
                    Assert.True(original.Bytes.Span.SequenceEqual(rebuilt.Bytes.Span), relative);
                if (original.Probe.Family == FormatFamily.Archive)
                {
                    Assert.Equal(original.Assets.Count, rebuilt.Assets.Count);
                    foreach (var pair in original.Assets.Zip(rebuilt.Assets))
                    {
                        Assert.Equal(pair.First.Name, pair.Second.Name);
                        Assert.True(original.Slice(pair.First.Offset, pair.First.Length).Span.SequenceEqual(rebuilt.Slice(pair.Second.Offset, pair.Second.Length).Span), relative + ": " + pair.First.Name);
                    }
                }
                if (original.Probe.Family == FormatFamily.TexturePack)
                    foreach (var pair in original.Assets.Zip(rebuilt.Assets))
                    {
                        Assert.Equal(TextureDecoder.Decode(original, pair.First, token).Rgba, TextureDecoder.Decode(rebuilt, pair.Second, token).Rgba);
                        Assert.Equal(((TextureInfo)pair.First.Content!).Flags & 14, ((TextureInfo)pair.Second.Content!).Flags & 14);
                    }
                Assert.Equal(input.Value, SourceProject.Sha256(File.ReadAllBytes(input.Key)));
            }
        }
        finally { if (Directory.Exists(scratch)) Directory.Delete(scratch, true); }
    }
}
