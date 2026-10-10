using System.Buffers.Binary;
using System.Text;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Export;
using Recoil.Zbd.Core.Formats;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ContentEditingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public void ScriptReaderWriterPreservePaddingNamesAndTail()
    {
        byte[] bytes = ContentFixture.Scripts(); var doc = FormatRegistry.Default.OpenBytes("interp.zbd", bytes, token: Token);
        Assert.Empty(doc.Diagnostics); Assert.Equal(bytes, PreparedScriptWriter.Write(doc.Scripts!, Token));
        Assert.Equal("strange command", doc.Scripts!.Entries[0].Instructions[0].Tokens[0]);
        Assert.Equal(new[] { "strange command", "", "é\n\\\"" }, doc.Scripts.Entries[0].Instructions[0].Tokens);
    }
    [Fact]
    public async Task ScriptEditsRetainIdentityAndUnrelatedBytesThroughStructuralUndo()
    {
        var doc = FormatRegistry.Default.OpenBytes("interp.zbd", ContentFixture.Scripts(), token: Token); var edits = new ScriptEditSession(doc);
        var entry = edits.Package.Entries[0]; var instruction = entry.Instructions[0]; var unrelated = edits.Package.Entries[1];
        edits.Accept(await edits.PrepareInstructionAsync(entry.Id, "set", instruction.Id, ["CustomUnknown", "a b", "", "é"], token: Token));
        Assert.Equal(instruction.Id, edits.Entry(entry.Id).Instructions[0].Id); Assert.Same(unrelated, edits.Package.Entries[1]);
        Assert.Equal(unrelated.Instructions[0].Raw.ToArray(), edits.Package.Entries[1].Instructions[0].Raw.ToArray());
        edits.Accept(await edits.PrepareEntryAsync("move", entry.Id, position: 1, token: Token)); Assert.Equal(entry.Id, edits.Package.Entries[1].Id);
        edits.Accept(await edits.PrepareInstructionAsync(entry.Id, "duplicate", instruction.Id, token: Token));
        Assert.NotEqual(instruction.Id, edits.Entry(entry.Id).Instructions[1].Id);
        edits.Accept(await edits.PrepareEntryAsync("delete", entry.Id, token: Token)); Assert.Single(edits.Package.Entries);
        edits.UndoRedo(false); Assert.Equal(entry.Id, edits.Package.Entries[1].Id);
        edits.UndoRedo(false); edits.UndoRedo(false); edits.UndoRedo(false);
        Assert.False(edits.IsDirty); Assert.Equal(doc.Bytes.ToArray(), edits.Current.Documents[doc.Path].Bytes.ToArray());
        edits.UndoRedo(true); Assert.Equal("CustomUnknown", edits.Entry(entry.Id).Instructions[0].Tokens[0]);
    }
    [Fact]
    public async Task InvalidOrSupersededScriptChangesCannotPublish()
    {
        var doc = FormatRegistry.Default.OpenBytes("interp.zbd", ContentFixture.Scripts(), token: Token); var edits = new ScriptEditSession(doc); var entry = edits.Package.Entries[0];
        await Assert.ThrowsAsync<InvalidDataException>(() => edits.PrepareInstructionAsync(entry.Id, "add", tokens: Enumerable.Repeat("x", 17).ToArray(), token: Token));
        await Assert.ThrowsAsync<InvalidDataException>(() => edits.PrepareInstructionAsync(entry.Id, "add", tokens: ["command", "\0"], token: Token));
        await Assert.ThrowsAsync<InvalidDataException>(() => edits.PrepareInstructionAsync(entry.Id, "add", tokens: ["command", "€"], token: Token));
        Assert.False(edits.CanUndo);
        var prepared = await edits.PrepareEntryAsync("rename", entry.Id, "first", token: Token);
        edits.Accept(await edits.PrepareEntryAsync("rename", entry.Id, "second", token: Token));
        Assert.Throws<InvalidOperationException>(() => edits.Accept(prepared)); Assert.Equal("second", edits.Entry(entry.Id).Name);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => edits.PrepareEntryAsync("delete", entry.Id, token: new CancellationToken(true)));

        // Timestamp changes share working instructions but retain a fresh reader-backed ScriptContent
        // in each document. Its token arrays, not just raw bytes or Current.State, decide history retention.
        var original = doc.Scripts!;
        var many = original.Entries[0] with { Instructions = Enumerable.Range(0, 128).Select(_ =>
            new ScriptInstruction(Guid.NewGuid(), new[] { "x" }.Concat(Enumerable.Repeat("", 15)).ToArray(), ReadOnlyMemory<byte>.Empty, null)).ToArray() };
        var shaped = FormatRegistry.Default.OpenBytes("shape.zbd", PreparedScriptWriter.Write(original with { Entries = [many] }, Token), token: Token);
        var bounded = new ScriptEditSession(shaped, 256 * 1024, 1024 * 1024);
        Guid script = bounded.Package.Entries[0].Id;
        for (uint i = 1; i <= 24; i++) bounded.Accept(await bounded.PrepareEntryAsync("timestamp", script, fileTime: i, token: Token));
        int undoCount = 0;
        while (bounded.CanUndo) { bounded.UndoRedo(false); undoCount++; }
        Assert.InRange(undoCount, 1, 23);
        while (bounded.CanRedo) bounded.UndoRedo(true);
        var held = bounded.Current;
        string[] largeTokens = ["x", .. Enumerable.Repeat(new string('a', 8192), 15)];
        var refused = await Assert.ThrowsAsync<InvalidDataException>(() => bounded.PrepareInstructionAsync(script, "set", bounded.Package.Entries[0].Instructions[0].Id, largeTokens, token: Token));
        Assert.Contains("allowance", refused.Message); Assert.Same(held, bounded.Current); Assert.True(bounded.CanUndo);
        bounded.Accept(await bounded.PrepareEntryAsync("timestamp", script, fileTime: 25, token: Token));
        bounded.UndoRedo(false); Assert.Same(held, bounded.Current);
        bounded.UndoRedo(true); Assert.Equal(script, bounded.Package.Entries[0].Id);
    }
    [Fact]
    public void MalformedScriptPackCannotEnableEditing()
    {
        byte[] source = ContentFixture.Scripts(); BinaryPrimitives.WriteUInt32LittleEndian(source.AsSpan(12 + 124), 1);
        var doc = FormatRegistry.Default.OpenBytes("bad.zbd", source, token: Token);
        Assert.NotEmpty(doc.Diagnostics); Assert.Throws<InvalidDataException>(() => new ScriptEditSession(doc));
    }
    [Theory]
    [InlineData(0, "count")]
    [InlineData(1, "size")]
    [InlineData(1, "string")]
    [InlineData(2, "terminator")]
    [InlineData(0, "index")]
    [InlineData(0, "outside")]
    public void DamagedScriptKeepsValidRecordsAndScopedDiagnosticsWithoutEnablingEdits(int index, string damage)
    {
        byte[] bytes = ContentFixture.DamagedScripts(index, damage), original = bytes.ToArray();
        var doc = FormatRegistry.Default.OpenBytes("damaged.zbd", bytes, token: Token);
        Assert.Equal(Enumerable.Range(0, 3).Where(i => i != index), doc.Assets.Select(a => a.Index));
        foreach (var asset in doc.Assets)
        {
            Assert.Equal(AssetKind.Script, asset.Kind); Assert.Equal($"script-{asset.Index}.zrd", asset.Name);
            Assert.Equal(new[] { "strange command", "", "é\n\\\"" }, Assert.IsType<ScriptContent>(asset.Content).Instructions.Single());
        }
        var diagnostic = Assert.Single(doc.Diagnostics);
        Assert.Equal("Error", diagnostic.Severity); Assert.Equal(index, diagnostic.AssetIndex);
        Assert.Equal((long)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12 + index * 128 + 124)), diagnostic.Offset);
        Assert.Contains($"script-{index}.zrd", diagnostic.Message); Assert.DoesNotContain("Parsing stopped", diagnostic.Message);
        Assert.Null(doc.Scripts); Assert.Throws<InvalidDataException>(() => new ScriptEditSession(doc)); Assert.Equal(original, doc.Bytes.ToArray());
    }
    [Fact]
    public void EmbeddedPaletteUsesStorageFlagRatherThanRuntimeOwnership()
    {
        byte[] bytes = ContentFixture.Texture(2, 1, false); var doc = FormatRegistry.Default.OpenBytes("a.zbd", bytes, token: Token);
        Assert.Empty(doc.Diagnostics); Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 255, 0, 255 }, TextureDecoder.Decode(doc, doc.Assets[0], Token).Rgba);
    }
    [Fact]
    public void IndexedReplacementKeepsSharedPaletteAndAliasedPeerUnchanged()
    {
        var source = FormatRegistry.Default.OpenBytes("a.zbd", ContentFixture.Texture(2, 1, true), token: Token);
        var before = TextureDecoder.Decode(source, source.Assets[1], Token);
        var image = new DecodedImage(2, 1, [0, 0, 0, 255, 0, 0, 255, 90]);
        var replacement = TexturePackWriter.Encode("sample", image, source, source.Assets[0], Token);
        byte[] bytes = TexturePackWriter.Write(new(source, new Dictionary<int, TexturePayload> { [0] = replacement.Payload }, []), Token);
        var after = FormatRegistry.Default.OpenBytes("a.zbd", bytes, token: Token);
        Assert.Empty(after.Diagnostics); Assert.Equal(image.Rgba, TextureDecoder.Decode(after, after.Assets[0], Token).Rgba);
        Assert.Equal(before.Rgba, TextureDecoder.Decode(after, after.Assets[1], Token).Rgba);
        Assert.Equal(source.Bytes.Slice(104).ToArray(), after.Bytes.Slice(104, source.Bytes.Length - 104).ToArray());
        Assert.Equal(-1, ((TextureInfo)after.Assets[0].Content!).PalettePage);
        Assert.Equal(0, ((TextureInfo)after.Assets[1].Content!).PalettePage);
    }
    [Fact]
    public void QuantizationIsDeterministicAndAlphaResamplingAvoidsColorFringes()
    {
        var source = FormatRegistry.Default.OpenBytes("a.zbd", ContentFixture.Texture(32, 32, true), token: Token);
        byte[] rgba = Enumerable.Range(0, 1024).SelectMany(i => new byte[] { (byte)(i % 32 * 8), (byte)(i / 32 * 8), (byte)(i % 8 * 32), (byte)(i % 256) }).ToArray();
        var first = TexturePackWriter.Encode("sample", new(32, 32, rgba), source, source.Assets[0], Token);
        var second = TexturePackWriter.Encode("sample", new(32, 32, rgba), source, source.Assets[0], Token);
        Assert.InRange(first.PaletteColors, 1, 256); Assert.True(first.QuantizedColors > 0); Assert.Equal(first.Payload.Bytes.ToArray(), second.Payload.Bytes.ToArray());
        var small = TexturePackWriter.Resize(new(2, 1, [255, 0, 0, 0, 0, 0, 255, 255]), 1, 1, Token);
        Assert.Equal(new byte[] { 0, 0, 255, 128 }, small.Rgba);
    }
    [Fact]
    public async Task VariantBatchUndoSaveAndOwnershipAreAtomic()
    {
        string root = Path.Combine(Path.GetTempPath(), "zstudio-texture-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string first = Path.Combine(root, "texture2.zbd"), second = Path.Combine(root, "texture1.zbd"), png = Path.Combine(root, "replace.png");
            await File.WriteAllBytesAsync(first, ContentFixture.Texture(2, 1, true), Token); await File.WriteAllBytesAsync(second, ContentFixture.Texture(1, 1, false), Token);
            await File.WriteAllBytesAsync(png, PngEncoder.Encode(new(2, 1, [0, 0, 255, 255, 0, 0, 255, 255]), Token), Token);
            using AssetResolver resolver = new(root); var doc = await resolver.OpenCachedAsync(first, Token); var edits = new TextureEditSession(doc);
            var candidate = await edits.PrepareAsync(png, 0, "", [new(first, 0), new(second, 0)], resolver, Token);
            Guid owner = Guid.NewGuid(); resolver.EditOwnership.Acquire(Guid.NewGuid(), "another writer", [second]);
            void Claim(IEnumerable<string> paths) => resolver.EditOwnership.Acquire(owner, "texture", paths);
            edits.BeforeEdit += Claim; Assert.Throws<InvalidOperationException>(() => edits.Accept(candidate)); Assert.False(edits.IsDirty); Assert.False(edits.CanUndo);
            edits.BeforeEdit -= Claim; edits.Accept(candidate); Assert.True(edits.IsDirty); Assert.Equal(2, edits.Documents.Count());
            Assert.Equal(1, ((TextureInfo)edits.Current.Documents[second].Assets[0].Content!).Width);
            edits.UndoRedo(false); Assert.False(edits.IsDirty); edits.UndoRedo(true);
            string output = Path.Combine(root, "output"); var destinations = edits.Documents.ToDictionary(d => d.Path, d => Path.Combine(output, Path.GetFileName(d.Path)));
            var saved = await edits.SaveAsync(destinations, Token); Assert.Empty(saved.Errors); Assert.Equal(2, saved.SavedPaths.Count); Assert.False(edits.IsDirty);
            File.Delete(first); File.Delete(second); Assert.False(edits.HasExternalChanges());
            edits.UndoRedo(false); Assert.True(edits.IsDirty); var undoSave = await edits.SaveAsync(token: Token); Assert.Empty(undoSave.Errors);
            Assert.Equal(ContentFixture.Texture(2, 1, true), await File.ReadAllBytesAsync(destinations[first], Token));
            await File.WriteAllTextAsync(destinations[first], "outside", Token); await Assert.ThrowsAsync<IOException>(() => edits.SaveAsync(token: Token));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task TextureSaveAsAliasesCannotBecomeIndependentVariantTargets()
    {
        string root = Path.Combine(Path.GetTempPath(), "zstudio-texture-alias-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "texture1.zbd"), copy = Path.Combine(root, "texture2.zbd"), png = Path.Combine(root, "image.png");
            await File.WriteAllBytesAsync(source, ContentFixture.Texture(2, 1, true), Token);
            await File.WriteAllBytesAsync(png, PngEncoder.Encode(new(2, 1, [0, 0, 255, 255, 0, 0, 255, 255]), Token), Token);
            using AssetResolver resolver = new(root); var edits = new TextureEditSession(await resolver.OpenCachedAsync(source, Token));
            Guid owner = Guid.NewGuid(); edits.Changed += () => resolver.SetWorkspaceSnapshots(owner, edits.PublishedDocuments);
            await edits.SaveAsync(new Dictionary<string, string> { [source] = copy }, Token);
            var before = edits.Current;
            var candidates = await edits.DiscoverTargetsAsync(0, resolver, Token);
            Assert.DoesNotContain(candidates, c => c.Path.Equals(copy, StringComparison.OrdinalIgnoreCase));
            await Assert.ThrowsAsync<InvalidDataException>(() => edits.PrepareAsync(png, 0, "", [new(source, 0), new(copy, 0)], resolver, Token));
            Assert.Same(before, edits.Current); Assert.False(edits.IsDirty); Assert.False(edits.CanUndo);
            edits.Accept(await edits.PrepareAsync(png, 0, "", null, resolver, Token));
            Assert.Equal(edits.Current.Documents[source].Bytes.ToArray(), (await resolver.OpenCachedAsync(copy, Token)).Bytes.ToArray());
            await edits.SaveAsync(token: Token);
            Assert.Equal(ContentFixture.Texture(2, 1, true), await File.ReadAllBytesAsync(source, Token));
            Assert.Equal(edits.Current.Documents[source].Bytes.ToArray(), await File.ReadAllBytesAsync(copy, Token));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task PngNoOpPreservesTextureBytesAndLargeUiDimensionsRemainSupported()
    {
        string root = Path.Combine(Path.GetTempPath(), "zstudio-texture-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "image.zbd"), png = Path.Combine(root, "image.png");
            await File.WriteAllBytesAsync(path, ContentFixture.Texture(1024, 3, true), Token);
            using AssetResolver resolver = new(root); var doc = await resolver.OpenCachedAsync(path, Token); var edits = new TextureEditSession(doc);
            await File.WriteAllBytesAsync(png, PngEncoder.Encode(TextureDecoder.Decode(doc, doc.Assets[0], Token), Token), Token);
            edits.Accept(await edits.PrepareAsync(png, 0, "", null, resolver, Token)); Assert.False(edits.CanUndo); Assert.Same(doc, edits.Current.Documents[path]);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task ReadOnlyScriptCorpusRoundTrips()
    {
        string? root = Environment.GetEnvironmentVariable("ZSTUDIO_CORPUS"); if (string.IsNullOrEmpty(root)) return;
        string path = Path.Combine(root, "interp.zbd"); if (!File.Exists(path)) return;
        var doc = await FormatRegistry.Default.OpenAsync(path, Token); Assert.Equal(doc.Bytes.ToArray(), PreparedScriptWriter.Write(doc.Scripts!, Token));
    }
    [Fact]
    public async Task EditedInstructionRetainsPaddingAndIdenticalInstructionsCanReorder()
    {
        var doc = FormatRegistry.Default.OpenBytes("interp.zbd", ContentFixture.Scripts(), token: Token); var edits = new ScriptEditSession(doc);
        var entry = edits.Package.Entries[0]; var first = entry.Instructions[0];
        edits.Accept(await edits.PrepareInstructionAsync(entry.Id,"set",first.Id,["changed"],token:Token));
        var reparsed = FormatRegistry.Default.OpenBytes("interp.zbd", edits.Current.Documents[doc.Path].Bytes.ToArray(), token:Token);
        Assert.Equal(first.Padding.ToArray(),reparsed.Scripts!.Entries[0].Instructions[0].Padding.ToArray());
        edits.Accept(await edits.PrepareInstructionAsync(entry.Id,"duplicate",first.Id,token:Token));
        byte[] bytes=edits.Current.Documents[doc.Path].Bytes.ToArray(); Guid second=edits.Entry(entry.Id).Instructions[1].Id;
        edits.Accept(await edits.PrepareInstructionAsync(entry.Id,"move",first.Id,position:1,token:Token));
        Assert.Equal(second,edits.Entry(entry.Id).Instructions[0].Id);Assert.Equal(bytes,edits.Current.Documents[doc.Path].Bytes.ToArray());
        edits.UndoRedo(false);Assert.Equal(first.Id,edits.Entry(entry.Id).Instructions[0].Id);
    }
    [Fact]
    public async Task ExistingFullWidthNamesRemainByteExactDuringUnrelatedEdits()
    {
        byte[] bytes=ContentFixture.Scripts();Array.Fill(bytes,(byte)'x',12,120);
        var doc=FormatRegistry.Default.OpenBytes("interp.zbd",bytes,token:Token);Assert.Equal(bytes,PreparedScriptWriter.Write(doc.Scripts!,Token));
        var edits=new ScriptEditSession(doc);edits.Accept(await edits.PrepareEntryAsync("timestamp",edits.Package.Entries[1].Id,fileTime:999,token:Token));
        Assert.Equal(bytes.AsSpan(12,120).ToArray(),edits.Current.Documents[doc.Path].Bytes.Span.Slice(12,120).ToArray());
        await Assert.ThrowsAsync<InvalidDataException>(()=>edits.PrepareInstructionAsync(edits.Package.Entries[0].Id,"add",tokens:["command",null!],token:Token));
    }
    [Fact]
    public async Task BatchSavePreflightCannotPublishAnyFileWhenAnotherTargetIsInvalid()
    {
        string root=Path.Combine(Path.GetTempPath(),"zstudio-save-preflight-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            string first=Path.Combine(root,"texture16.zbd"),second=Path.Combine(root,"texture8.zbd"),png=Path.Combine(root,"image.png");
            await File.WriteAllBytesAsync(first,ContentFixture.Texture(2,1,true),Token);await File.WriteAllBytesAsync(second,ContentFixture.Texture(1,1,true),Token);
            using AssetResolver resolver=new(root);var edits=new TextureEditSession(await resolver.OpenCachedAsync(first,Token));
            await File.WriteAllBytesAsync(png,PngEncoder.Encode(new(2,1,[0,0,255,255,0,0,255,255]),Token),Token);
            edits.Accept(await edits.PrepareAsync(png,0,"",[new(first,0),new(second,0)],resolver,Token));
            string output=Path.Combine(root,"new.zbd"),existing=Path.Combine(root,"existing.zbd");await File.WriteAllTextAsync(existing,"untouched",Token);
            await Assert.ThrowsAsync<IOException>(()=>edits.SaveAsync(new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase) { [first]=output,[second]=existing },Token));
            Assert.False(File.Exists(output));Assert.Equal("untouched",await File.ReadAllTextAsync(existing,Token));Assert.True(edits.IsDirty);Assert.Empty(Directory.GetFiles(root,"*.tmp"));
            await Assert.ThrowsAsync<IOException>(()=>edits.SaveAsync(new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase) { [first]=output,[second]=Path.Combine(root,"zbd_1999","bad.zbd") },Token));
            Assert.False(File.Exists(output));Assert.Equal(ContentFixture.Texture(2,1,true),await File.ReadAllBytesAsync(first,Token));
            string directory = Path.Combine(root, "directory.zbd"); Directory.CreateDirectory(directory);
            await Assert.ThrowsAsync<IOException>(() => edits.SaveAsync(new Dictionary<string,string> { [first]=output, [second]=directory }, Token));
            Assert.False(File.Exists(output)); Assert.Equal(second, edits.TargetPath(second)); Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        }
        finally { Directory.Delete(root,true); }
    }
    [Fact]
    public async Task PartialBatchSaveReportsPublishedAndRemainingFilesAndCanRetry()
    {
        if (!OperatingSystem.IsWindows()) return; // FileShare.Delete controls atomic replacement on Windows.
        string root=Path.Combine(Path.GetTempPath(),"zstudio-partial-save-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            string first=Path.Combine(root,"texture1.zbd"),second=Path.Combine(root,"texture2.zbd"),png=Path.Combine(root,"image.png");
            await File.WriteAllBytesAsync(first,ContentFixture.Texture(2,1,true),Token);await File.WriteAllBytesAsync(second,ContentFixture.Texture(1,1,true),Token);
            using AssetResolver resolver=new(root);var edits=new TextureEditSession(await resolver.OpenCachedAsync(first,Token));
            await File.WriteAllBytesAsync(png,PngEncoder.Encode(new(2,1,[0,0,255,255,0,0,255,255]),Token),Token);
            edits.Accept(await edits.PrepareAsync(png,0,"",[new(first,0),new(second,0)],resolver,Token));
            using (var locked=new FileStream(second,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                var result=await edits.SaveAsync(token:Token);Assert.Equal(new[] { first },result.SavedPaths);Assert.Equal(new[] { second },result.RemainingPaths);Assert.Single(result.Errors);Assert.True(edits.IsDirty);
            }
            Assert.Equal(edits.Current.Documents[first].Bytes.ToArray(),await File.ReadAllBytesAsync(first,Token));Assert.Equal(ContentFixture.Texture(1,1,true),await File.ReadAllBytesAsync(second,Token));
            Assert.False(edits.HasExternalChanges());var retry=await edits.SaveAsync(token:Token);Assert.Equal(new[] { second },retry.SavedPaths);Assert.Empty(retry.Errors);Assert.Empty(retry.RemainingPaths);Assert.False(edits.IsDirty);
        }
        finally { Directory.Delete(root,true); }
    }
    [Fact]
    public async Task PartialSaveAsRetainsUnpublishedDestinationsAndNeverRetriesIntoSources()
    {
        string root = Path.Combine(Path.GetTempPath(), "zstudio-partial-saveas-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string first = Path.Combine(root, "texture1.zbd"), second = Path.Combine(root, "texture2.zbd"), png = Path.Combine(root, "image.png");
            await File.WriteAllBytesAsync(first, ContentFixture.Texture(2, 1, true), Token); await File.WriteAllBytesAsync(second, ContentFixture.Texture(1, 1, true), Token);
            await File.WriteAllBytesAsync(png, PngEncoder.Encode(new(2, 1, [0, 0, 255, 255, 0, 0, 255, 255]), Token), Token);
            using AssetResolver resolver = new(root); var edits = new TextureEditSession(await resolver.OpenCachedAsync(first, Token));
            edits.Accept(await edits.PrepareAsync(png, 0, "", [new(first, 0), new(second, 0)], resolver, Token));
            string copy1 = Path.Combine(root, "copy1.zbd"), copy2 = Path.Combine(root, "copy2.zbd");
            var publish = edits.PublishFile;
            edits.PublishFile = (temp, target, createNew) => { if (target == copy2) throw new IOException("Injected publication failure after successful staging."); publish(temp, target, createNew); };
            var result = await edits.SaveAsync(new Dictionary<string, string> { [first] = copy1, [second] = copy2 }, Token);
            Assert.Equal(new[] { copy1 }, result.SavedPaths); Assert.Equal(new[] { copy2 }, result.RemainingPaths); Assert.Single(result.Errors);
            Assert.Equal(copy2, edits.TargetPath(second)); Assert.True(edits.IsDirty);
            edits.PublishFile = publish;
            // A competing file must never be overwritten by a pending Save As retry.
            await File.WriteAllTextAsync(copy2, "external", Token); Assert.True(edits.HasExternalChanges());
            await Assert.ThrowsAsync<IOException>(() => edits.SaveAsync(token: Token));
            Assert.Equal("external", await File.ReadAllTextAsync(copy2, Token)); File.Delete(copy2);
            Assert.False(edits.HasExternalChanges());
            var retry = await edits.SaveAsync(token: Token);
            Assert.Equal(new[] { copy2 }, retry.SavedPaths); Assert.Empty(retry.Errors); Assert.False(edits.IsDirty);
            Assert.Equal(ContentFixture.Texture(2, 1, true), await File.ReadAllBytesAsync(first, Token));
            Assert.Equal(ContentFixture.Texture(1, 1, true), await File.ReadAllBytesAsync(second, Token));
            Assert.Equal(edits.Current.Documents[second].Bytes.ToArray(), await File.ReadAllBytesAsync(copy2, Token));
            // Even an unchanged snapshot must remain dirty while its requested copy is unpublished.
            string copy3 = Path.Combine(root, "copy3.zbd"), copy4 = Path.Combine(root, "copy4.zbd");
            edits.PublishFile = (_, _, _) => throw new IOException("Publication unavailable.");
            var pending = await edits.SaveAsync(new Dictionary<string, string> { [first] = copy3, [second] = copy4 }, Token);
            Assert.Empty(pending.SavedPaths); Assert.True(edits.IsDirty); Assert.Equal(copy4, edits.TargetPath(second));
            edits.PublishFile = publish; Assert.Empty((await edits.SaveAsync(token: Token)).Errors); Assert.False(edits.IsDirty);
            File.Delete(second); string copy5 = Path.Combine(root, "copy5.zbd");
            await Assert.ThrowsAsync<InvalidDataException>(() => edits.SaveAsync(new Dictionary<string, string> { [first] = second, [second] = copy5 }, Token));
            Assert.False(File.Exists(second)); Assert.False(File.Exists(copy5)); Assert.Equal(copy3, edits.TargetPath(first)); Assert.False(edits.IsDirty);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task AmbiguousVariantNamesRequireAnExplicitRecordAndLeaveItsPeerUntouched()
    {
        string root=Path.Combine(Path.GetTempPath(),"zstudio-variant-identity-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            string first=Path.Combine(root,"texture1.zbd"),second=Path.Combine(root,"texture2.zbd"),png=Path.Combine(root,"image.png");
            await File.WriteAllBytesAsync(first,ContentFixture.Texture(2,1,true),Token);
            byte[] variant=ContentFixture.Texture(1,1,true);variant.AsSpan(64,32).Clear();Encoding.Latin1.GetBytes("sample").CopyTo(variant,64);await File.WriteAllBytesAsync(second,variant,Token);
            using AssetResolver resolver=new(root);var edits=new TextureEditSession(await resolver.OpenCachedAsync(first,Token));
            var targets=await edits.DiscoverTargetsAsync(0,resolver,Token);Assert.Equal(2,targets.Count(t=>t.Path==second&&t.Ambiguous));
            await File.WriteAllBytesAsync(png,PngEncoder.Encode(new(2,1,[0,0,255,255,0,0,255,255]),Token),Token);
            edits.Accept(await edits.PrepareAsync(png,0,"",[new(first,0),new(second,1)],resolver,Token));
            var changed=edits.Current.Documents[second];Assert.Equal(new byte[] { 255,0,0,255 },TextureDecoder.Decode(changed,changed.Assets[0],Token).Rgba);Assert.Equal(new byte[] { 0,0,255,255 },TextureDecoder.Decode(changed,changed.Assets[1],Token).Rgba);
            await Assert.ThrowsAsync<InvalidDataException>(()=>edits.PrepareAsync(png,null,"sample.png",null,resolver,Token));
        }
        finally { Directory.Delete(root,true); }
    }
}
