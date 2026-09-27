using System.Security.Cryptography;
using System.Text.Json;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Export;
using Recoil.Zbd.Core.Formats;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ModelReplacementTests
{
    [Theory]
    [InlineData("source")]
    [InlineData("zbd_1999")]
    public async Task ModelBundleAndStandardExportsRejectProtectedDestinations(string folder)
    {
        string root = Path.Combine(Path.GetTempPath(), "zstudio-export-guard-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source"), destination = Path.Combine(root, folder, "nested", "export");
        using AssetResolver resolver = new(source);
        var token = TestContext.Current.CancellationToken;
        var document = FormatRegistry.Default.OpenBytes(Path.Combine(source, "gamez.zbd"), ModelFixture.GameZ(), token: token);
        var exporter = new ExportService(resolver);
        await Assert.ThrowsAsync<IOException>(() => exporter.ExportModelBundleAsync(document, 0, destination, token: token));
        await Assert.ThrowsAsync<IOException>(() => exporter.ExportAsync(document, document.Assets, destination, false, token: token));
        Assert.False(Directory.Exists(root));
    }
    [Fact]
    public async Task OptionalCorpusReplacementPreservesOtherModelsAndNodePayloads()
    {
        string? root = Environment.GetEnvironmentVariable("ZSTUDIO_CORPUS"); if (root == null) return;
        var token = TestContext.Current.CancellationToken;
        var source = await FormatRegistry.Default.OpenAsync(Path.Combine(root,"m1","gamez.zbd"),token);
        var changes = Enumerable.Range(1207,6).ToDictionary(i=>i,_=>ModelFixture.Mesh);
        var next = FormatRegistry.Default.OpenBytes(source.Path,ModelReplacementWriter.Replace(source,changes,"pu001_new",token),token:token);
        Assert.Empty(next.Diagnostics);
        foreach (var old in source.Assets.Where(a=>a.Kind==AssetKind.Model && !changes.ContainsKey(a.Index)))
        {
            var check = next.Assets.Single(a=>a.Kind==AssetKind.Model && a.Index==old.Index);
            Assert.Equal(source.Slice(old.Offset,old.Length).ToArray(),next.Slice(check.Offset,check.Length).ToArray());
        }
        Assert.Equal(source.Scene!.Nodes.Count,next.Scene!.Nodes.Count);
        foreach (var node in source.Scene.Nodes)
        {
            var check = next.Scene.Nodes[node.Index]; Assert.Equal(node.Data.ToJsonString(),check.Data.ToJsonString()); Assert.Equal(node.Children,check.Children); Assert.Equal(node.Parents,check.Parents);
        }
        foreach (string path in Directory.EnumerateFiles(Path.Combine(root,"m1"),"*texture*.zbd"))
        {
            var pack = await FormatRegistry.Default.OpenAsync(path,token); var changed = FormatRegistry.Default.OpenBytes(path,TexturePackWriter.Append(pack,"pu001_new",Image,token),token:token);
            Assert.Empty(changed.Diagnostics);
            foreach (var old in pack.Assets) { var check = changed.Assets[old.Index]; Assert.Equal(pack.Slice(old.Offset,old.Length).ToArray(),changed.Slice(check.Offset,check.Length).ToArray()); }
        }
    }
    private const string Obj = "v -.3 0 0\nv .3 0 0\nv 0 2 .1\nvt 0 0\nvt 1 0\nvt .5 1\nvn 0 0 1\nf 1/1/1 2/2/1 3/3/1\n";
    private static DecodedImage Image => new(2,2,Enumerable.Repeat(new byte[] { 24,80,128,255 },4).SelectMany(x=>x).ToArray());
    [Fact]
    public void BrokenPoolLinksAreRejected()
    {
        byte[] bytes = ModelFixture.GameZ();
        // Break the free chain at its head without changing count/capacity. A parser
        // alone accepts this; the retail allocator would lose the remaining slots.
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(36 + 16 + 44 + 42),-1);
        var source = FormatRegistry.Default.OpenBytes("gamez.zbd",bytes,token:TestContext.Current.CancellationToken);
        Assert.Throws<InvalidDataException>(()=>ModelReplacementWriter.Replace(source,new Dictionary<int,ImportedMesh> { [1]=ModelFixture.Mesh },"new",TestContext.Current.CancellationToken));
    }
    private static (float[] Node, float[] Model, float[] Child, byte[] Sphere) NodeBounds(ZbdDocument document, int node)
    {
        byte[] header = document.Slice(document.GameZLayout!.NodeOffset + node * 196L, 196).ToArray();
        float[] Box(int offset) => Enumerable.Range(0,6).Select(i=>System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(header.AsSpan(offset + i * 4))).ToArray();
        return (Box(116), Box(140), Box(164), header[100..116]);
    }
    [Fact]
    public void ReplacementBoundsFollowRetailNodeRules()
    {
        var token = TestContext.Current.CancellationToken;
        var source = FormatRegistry.Default.OpenBytes("gamez.zbd",ModelFixture.GameZ(),token:token);
        // Inside the old envelope: the replaced node gets the exact solid-model box, ancestors are untouched.
        var fitted = FormatRegistry.Default.OpenBytes("gamez.zbd",ModelReplacementWriter.Replace(source,new Dictionary<int,ImportedMesh> { [1]=ModelFixture.Mesh },"new",token),token:token);
        float[] meshBox = [-.3f,0,0,.3f,2,.1f];
        Assert.Equal(meshBox,NodeBounds(fitted,1).Node); Assert.Equal(meshBox,NodeBounds(fitted,1).Model);
        var root = NodeBounds(source,0); Assert.Equal(root.Node,NodeBounds(fitted,0).Node); Assert.Equal(root.Child,NodeBounds(fitted,0).Child);
        // Outside it: the child box propagates to every ancestor; the render-time sphere cache stays zero.
        var large = ModelFixture.Mesh with { Positions = ModelFixture.Mesh.Positions.Select(p=>p*100).ToArray() };
        var grown = FormatRegistry.Default.OpenBytes("gamez.zbd",ModelReplacementWriter.Replace(source,new Dictionary<int,ImportedMesh> { [1]=large },"new",token),token:token);
        Assert.Empty(grown.Diagnostics);
        var (min, max) = large.Bounds;
        float[] largeBox = [min.X,min.Y,min.Z,max.X,max.Y,max.Z], rootBox = [min.X,-3,-3,max.X,max.Y,max.Z];
        Assert.Equal(largeBox,NodeBounds(grown,1).Node); Assert.Equal(rootBox,NodeBounds(grown,0).Child); Assert.Equal(rootBox,NodeBounds(grown,0).Node);
        Assert.All(new[] { 0, 1 }, i => Assert.Equal(new byte[16],NodeBounds(grown,i).Sphere));
        // zDi::RebuildBounds radius approximation reproduces the corpus holder value (1.41 for 2.2 x 1 x 1.2).
        var holder = ModelFixture.Mesh with { Positions = [new(-1.1f,1,-.6f),new(1.1f,1,.6f),new(0,2,0)] };
        var sphere = FormatRegistry.Default.OpenBytes("gamez.zbd",ModelReplacementWriter.Replace(source,new Dictionary<int,ImportedMesh> { [1]=holder },"new",token),token:token).Scene!.Models[1].Metadata;
        Assert.Equal(1.41f,(float)sphere["bbox_diag"]!.GetValue<double>()); Assert.Equal(1.5f,(float)sphere["bbox_mid"]!["y"]!.GetValue<double>());
        // An ancestor without valid child bounds cannot be expanded by inference.
        byte[] bytes = ModelFixture.GameZ(); int flags = source.GameZLayout!.NodeOffset + 36;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(flags),0x300);
        var noChildBounds = FormatRegistry.Default.OpenBytes("gamez.zbd",bytes,token:token);
        Assert.Throws<InvalidDataException>(()=>ModelReplacementWriter.Replace(noChildBounds,new Dictionary<int,ImportedMesh> { [1]=large },"new",token));
    }
    [Fact]
    public async Task ProtectedSourceSaveAsRetargetsAndUndoRestoresTheCopy()
    {
        var token = TestContext.Current.CancellationToken;
        string root = Path.Combine(Path.GetTempPath(),"zstudio-model-protection-"+Guid.NewGuid().ToString("N")), sourceFolder=Path.Combine(root,"zbd_1999"), output=Path.Combine(root,"working"); Directory.CreateDirectory(sourceFolder);
        try
        {
            string path = Path.Combine(sourceFolder,"gamez.zbd"); await File.WriteAllBytesAsync(path,ModelFixture.GameZ(),token); await File.WriteAllBytesAsync(Path.Combine(sourceFolder,"texture2.zbd"),ModelFixture.Texture(),token);
            using AssetResolver resolver = new(sourceFolder); var source = await resolver.OpenCachedAsync(path,token); var edits = new ModelEditSession(source);
            var batch = new ModelImportBatch(Convert.ToHexString(SHA256.HashData(source.Bytes.Span)),"new",Image,new Dictionary<int,ImportedMesh>{[1]=ModelFixture.Mesh});
            edits.Accept(await edits.PrepareAsync(batch,resolver,token)); await Assert.ThrowsAsync<IOException>(()=>edits.SaveAsync(token:token));
            Assert.Equal(ModelFixture.GameZ(),await File.ReadAllBytesAsync(path,token));
            var result = await edits.SaveAsync(output,token); Assert.Empty(result.Errors); Assert.Equal(Path.Combine(output,"gamez.zbd"),edits.TargetPath(path));
            edits.Undo(); result = await edits.SaveAsync(token:token); Assert.Empty(result.Errors); Assert.Equal(ModelFixture.GameZ(),await File.ReadAllBytesAsync(Path.Combine(output,"gamez.zbd"),token));
            Assert.Equal(ModelFixture.Texture(),await File.ReadAllBytesAsync(Path.Combine(output,"texture2.zbd"),token)); Assert.Equal(ModelFixture.GameZ(),await File.ReadAllBytesAsync(path,token));
        }
        finally { Directory.Delete(root,true); }
    }
    [Fact]
    public async Task SaveAsWatchesCurrentWorldAndTextureTargets()
    {
        var token = TestContext.Current.CancellationToken;
        string root = Path.Combine(Path.GetTempPath(), "zstudio-model-retarget-" + Guid.NewGuid().ToString("N"));
        string sourceFolder = Path.Combine(root, "source"), first = Path.Combine(root, "first"), second = Path.Combine(root, "second");
        Directory.CreateDirectory(sourceFolder);
        try
        {
            string world = Path.Combine(sourceFolder, "gamez.zbd"), texture = Path.Combine(sourceFolder, "texture2.zbd");
            await File.WriteAllBytesAsync(world, ModelFixture.GameZ(), token); await File.WriteAllBytesAsync(texture, ModelFixture.Texture(), token);
            using AssetResolver resolver = new(sourceFolder); var source = await resolver.OpenCachedAsync(world, token); var edits = new ModelEditSession(source);
            var batch = new ModelImportBatch(Convert.ToHexString(SHA256.HashData(source.Bytes.Span)), "new", Image, new Dictionary<int, ImportedMesh> { [1] = ModelFixture.Mesh });
            edits.Accept(await edits.PrepareAsync(batch, resolver, token));
            Assert.Empty((await edits.SaveAsync(first, token)).Errors);
            await File.AppendAllTextAsync(world, "external source change", token);
            await File.AppendAllTextAsync(texture, "external source change", token);
            Assert.False(edits.HasExternalChanges());
            File.Delete(world); File.Delete(texture);
            Assert.False(edits.HasExternalChanges());
            Assert.Empty((await edits.SaveAsync(second, token)).Errors);
            File.Delete(Path.Combine(first, "gamez.zbd")); File.Delete(Path.Combine(first, "texture2.zbd"));
            Assert.False(edits.HasExternalChanges());
            edits.Undo(); Assert.Empty((await edits.SaveAsync(token: token)).Errors);
            Assert.Equal(ModelFixture.GameZ(), await File.ReadAllBytesAsync(Path.Combine(second, "gamez.zbd"), token));
            Assert.Equal(ModelFixture.Texture(), await File.ReadAllBytesAsync(Path.Combine(second, "texture2.zbd"), token));
            Assert.False(edits.HasExternalChanges());
            await File.AppendAllTextAsync(Path.Combine(second, "texture2.zbd"), "external target change", token);
            Assert.True(edits.HasExternalChanges());
            await Assert.ThrowsAsync<IOException>(() => edits.SaveAsync(token: token));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void ObjCoordinatesAndTextureVSurviveNativeRoundTrip()
    {
        var source = FormatRegistry.Default.OpenBytes("gamez.zbd",ModelFixture.GameZ(), token: TestContext.Current.CancellationToken); Assert.Empty(source.Diagnostics);
        var mesh = ModelImport.ReadObj(Obj, token: TestContext.Current.CancellationToken); Assert.Equal(ModelFixture.Mesh.Positions,mesh.Positions); Assert.Equal(ModelFixture.Mesh.Uvs,mesh.Uvs);
        byte[] output = ModelReplacementWriter.Replace(source,new Dictionary<int,ImportedMesh> { [1] = mesh },"shell_new", token: TestContext.Current.CancellationToken);
        var next = FormatRegistry.Default.OpenBytes("gamez.zbd",output, token: TestContext.Current.CancellationToken); Assert.Empty(next.Diagnostics);
        var m = next.Scene!.Models[1]; Assert.Equal(0,m.Metadata.Int("model_type")); Assert.Equal(mesh.Positions,m.Vertices); Assert.Equal(mesh.Normals,m.Normals); Assert.Equal(mesh.Uvs,m.Polygons[0].Uvs);
        Assert.Equal(1,m.Polygons[0].MaterialIndex); Assert.Equal("shell_new",next.Scene.Textures[0].Text("name"));
        Assert.Equal(source.Scene!.Nodes[1].Children,next.Scene.Nodes[1].Children); Assert.Equal(source.Scene.Nodes[1].Data.ToJsonString(),next.Scene.Nodes[1].Data.ToJsonString());
        var old = source.Assets.Single(a=>a.Kind==AssetKind.Model && a.Index==0); var unchanged = next.Assets.Single(a=>a.Kind==AssetKind.Model && a.Index==0);
        Assert.Equal(source.Slice(old.Offset,old.Length).ToArray(),next.Slice(unchanged.Offset,unchanged.Length).ToArray());
        Assert.Equal(source.Bytes.ToArray(),ModelReplacementWriter.Replace(source,new Dictionary<int,ImportedMesh>(),"unused", token: TestContext.Current.CancellationToken));
        Assert.Equal(-1,next.Scene.Materials[1].Int("prev_index")); Assert.Equal(0,next.Scene.Materials[1].Int("next_index")); Assert.Equal(1,next.Scene.Materials[0].Int("prev_index"));
    }
    [Theory]
    [InlineData("v NaN 0 0")]
    [InlineData("f 1/1/1 2/2/1 999/3/1")]
    [InlineData("f 1/1/1 2/2/1 3/3/1 1/1/1")]
    [InlineData("f 1/1/1 1/1/1 3/3/1")]
    [InlineData("f 1//1 2//1 3//1")]
    public void InvalidObjFailsBeforeMutation(string line) => Assert.Throws<InvalidDataException>(() => ModelImport.ReadObj(Obj + line, token: TestContext.Current.CancellationToken));
    [Fact]
    public void PngChecksumsBoundsAndTextureBytesAreVerified()
    {
        byte[] png = PngEncoder.Encode(Image, token: TestContext.Current.CancellationToken); Assert.Equal(Image.Rgba,ModelImport.ReadPng(png, token: TestContext.Current.CancellationToken).Rgba);
        png[^7] ^= 1; Assert.Throws<InvalidDataException>(()=>ModelImport.ReadPng(png, token: TestContext.Current.CancellationToken));
        Assert.Throws<InvalidDataException>(()=>ModelImport.ReadPng(PngEncoder.Encode(new(1024,1,new byte[1024*4]), token: TestContext.Current.CancellationToken), token: TestContext.Current.CancellationToken));
        // 512 is the documented maximum (retail loader has no fixed limit; device caps gate larger textures).
        var wide = new DecodedImage(512,2,Enumerable.Repeat(new byte[] { 200,40,30,255 },1024).SelectMany(x=>x).ToArray());
        Assert.Equal(wide.Rgba,ModelImport.ReadPng(PngEncoder.Encode(wide, token: TestContext.Current.CancellationToken), token: TestContext.Current.CancellationToken).Rgba);
        var source = FormatRegistry.Default.OpenBytes("texture2.zbd",ModelFixture.Texture(), token: TestContext.Current.CancellationToken);
        byte[] first = TexturePackWriter.Append(source,"first",Image, token: TestContext.Current.CancellationToken); var one = FormatRegistry.Default.OpenBytes(source.Path,first, token: TestContext.Current.CancellationToken); Assert.Empty(one.Diagnostics);
        var two = FormatRegistry.Default.OpenBytes(source.Path,TexturePackWriter.Append(one,"second",Image, token: TestContext.Current.CancellationToken), token: TestContext.Current.CancellationToken); Assert.Empty(two.Diagnostics);
        Assert.Equal(one.Slice(one.Assets[0].Offset,one.Assets[0].Length).ToArray(),two.Slice(two.Assets[0].Offset,two.Assets[0].Length).ToArray());
        var image = TextureDecoder.Decode(two,two.Assets[1], token: TestContext.Current.CancellationToken); Assert.Equal(2,image.Width); Assert.All(Enumerable.Range(0,4),i=>Assert.Equal(255,image.Rgba[i*4+3]));
        Assert.Throws<InvalidDataException>(()=>TexturePackWriter.Append(two,"first",Image, token: TestContext.Current.CancellationToken));
        var large = FormatRegistry.Default.OpenBytes(source.Path,TexturePackWriter.Append(two,"large",wide, token: TestContext.Current.CancellationToken), token: TestContext.Current.CancellationToken); Assert.Empty(large.Diagnostics);
        var decodedLarge = TextureDecoder.Decode(large,large.Assets[2], token: TestContext.Current.CancellationToken); Assert.Equal(512,decodedLarge.Width); Assert.Equal(2,decodedLarge.Height);
        Assert.Throws<InvalidDataException>(()=>TexturePackWriter.Append(two,"huge",new(1024,1,Enumerable.Repeat((byte)255,1024*4).ToArray()), token: TestContext.Current.CancellationToken));
        var transparent = Image; transparent.Rgba[3] = 0; Assert.Throws<InvalidDataException>(()=>TexturePackWriter.Append(two,"third",transparent, token: TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task BatchUndoSaveAndExternalChangeAreAtomic()
    {
        string root = Path.Combine(Path.GetTempPath(),"zstudio-model-test-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string worldPath = Path.Combine(root,"gamez.zbd"), texturePath = Path.Combine(root,"texture2.zbd"); await File.WriteAllBytesAsync(worldPath,ModelFixture.GameZ(), cancellationToken: TestContext.Current.CancellationToken); await File.WriteAllBytesAsync(texturePath,ModelFixture.Texture(), cancellationToken: TestContext.Current.CancellationToken);
            using AssetResolver resolver = new(root); var doc = await resolver.OpenCachedAsync(worldPath,TestContext.Current.CancellationToken); var edits = new ModelEditSession(doc);
            var batch = new ModelImportBatch(Convert.ToHexString(SHA256.HashData(doc.Bytes.Span)),"shell_new",Image,new Dictionary<int,ImportedMesh> { [1] = ModelFixture.Mesh });
            var prepared = await edits.PrepareAsync(batch,resolver, token: TestContext.Current.CancellationToken); Assert.False(edits.IsDirty); edits.Accept(prepared); Assert.True(edits.IsDirty);
            edits.Undo(); Assert.False(edits.IsDirty); edits.Redo(); Assert.True(edits.IsDirty);
            var saved = await edits.SaveAsync(token: TestContext.Current.CancellationToken); Assert.Empty(saved.Errors); Assert.Equal(2,saved.SavedPaths.Count); Assert.EndsWith("gamez.zbd",saved.SavedPaths[^1]); Assert.False(edits.IsDirty);
            edits.Undo(); Assert.True(edits.IsDirty); await edits.SaveAsync(token: TestContext.Current.CancellationToken); Assert.False(edits.IsDirty);
            Assert.Equal(ModelFixture.GameZ(),await File.ReadAllBytesAsync(worldPath, cancellationToken: TestContext.Current.CancellationToken)); Assert.Equal(ModelFixture.Texture(),await File.ReadAllBytesAsync(texturePath, cancellationToken: TestContext.Current.CancellationToken));
            edits.Redo(); await File.AppendAllTextAsync(texturePath,"changed", cancellationToken: TestContext.Current.CancellationToken); byte[] before = await File.ReadAllBytesAsync(worldPath, cancellationToken: TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<IOException>(()=>edits.SaveAsync(token: TestContext.Current.CancellationToken)); Assert.Equal(before,await File.ReadAllBytesAsync(worldPath, cancellationToken: TestContext.Current.CancellationToken)); Assert.True(edits.IsDirty);
            using var canceled = new CancellationTokenSource(); canceled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>edits.PrepareAsync(batch,resolver,canceled.Token));
        }
        finally { Directory.Delete(root,true); }
    }
    [Fact]
    public async Task ImportManifestRejectsTraversalAndWrongFingerprint()
    {
        string root = Path.Combine(Path.GetTempPath(),"zstudio-manifest-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string manifest = Path.Combine(root,"replacement.json");
            await File.WriteAllTextAsync(manifest,JsonSerializer.Serialize(new ModelImportManifest(1,new string('0',64),"shell_new","../escape.png",[new(1,"mesh.obj")])), cancellationToken: TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<IOException>(()=>ModelImport.ReadAsync(manifest, token: TestContext.Current.CancellationToken));
            var source = FormatRegistry.Default.OpenBytes(Path.Combine(root,"gamez.zbd"),ModelFixture.GameZ(), token: TestContext.Current.CancellationToken); using AssetResolver resolver = new(root); var edits = new ModelEditSession(source);
            await Assert.ThrowsAsync<InvalidDataException>(()=>edits.PrepareAsync(new(new string('0',64),"new",Image,new Dictionary<int,ImportedMesh> { [1]=ModelFixture.Mesh }),resolver, token: TestContext.Current.CancellationToken)); Assert.False(edits.IsDirty);
        }
        finally { Directory.Delete(root,true); }
    }
}
