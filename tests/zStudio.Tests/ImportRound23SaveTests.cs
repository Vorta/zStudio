using System.Security.Cryptography;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Export;
using Recoil.Zbd.Core.Formats;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ImportRound23SaveTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModelCopyRetainsEveryDestinationAfterPartialPublication(bool cancel)
    {
        using ModelCopy fixture = await ModelCopy.Create();
        var edits = fixture.Edits;
        var publish = edits.PublishFile;
        using CancellationTokenSource interruption = CancellationTokenSource.CreateLinkedTokenSource(Token);
        edits.PublishFile = (file, target, replace) =>
        {
            if (!cancel && target == fixture.WorldCopy) throw new IOException("Second publication unavailable.");
            publish(file, target, replace);
            if (cancel) interruption.Cancel();
        };
        var result = await edits.SaveAsync(fixture.Output, interruption.Token);
        Assert.Single(result.SavedPaths); Assert.Single(result.Errors); Assert.True(edits.IsDirty);
        Assert.Equal(fixture.WorldCopy, edits.TargetPath(fixture.World));
        Assert.Equal(fixture.TextureCopy, edits.TargetPath(fixture.Texture));
        Assert.False(File.Exists(fixture.WorldCopy));
        await fixture.AssertSourcesUnchanged();
        edits.PublishFile = publish;
        Assert.Empty((await edits.SaveAsync(token: Token)).Errors);
        Assert.False(edits.IsDirty);
        Assert.Equal(edits.Current.World.Bytes.ToArray(), await File.ReadAllBytesAsync(fixture.WorldCopy, Token));
        Assert.Equal(edits.Current.Textures[fixture.Texture].Bytes.ToArray(), await File.ReadAllBytesAsync(fixture.TextureCopy, Token));
        await fixture.AssertSourcesUnchanged();
        edits.Undo();
        Assert.Empty((await edits.SaveAsync(token: Token)).Errors);
        Assert.Equal(ModelFixture.GameZ(), await File.ReadAllBytesAsync(fixture.WorldCopy, Token));
        Assert.Equal(ModelFixture.Texture(), await File.ReadAllBytesAsync(fixture.TextureCopy, Token));
        await fixture.AssertSourcesUnchanged();
    }

    [Fact]
    public async Task PendingUnchangedModelCopyRemainsDirtyAndRefusesAnInterveningFile()
    {
        using ModelCopy fixture = await ModelCopy.Create();
        var edits = fixture.Edits; edits.Undo(); Assert.False(edits.IsDirty);
        var publish = edits.PublishFile;
        edits.PublishFile = (_, _, _) => throw new IOException("No publication available.");
        Assert.Single((await edits.SaveAsync(fixture.Output, Token)).Errors);
        Assert.True(edits.IsDirty);
        Assert.Equal(fixture.WorldCopy, edits.TargetPath(fixture.World));
        edits.PublishFile = publish;
        byte[] external = [9, 8, 7]; await File.WriteAllBytesAsync(fixture.WorldCopy, external, Token);
        await Assert.ThrowsAsync<IOException>(() => edits.SaveAsync(token: Token));
        Assert.Equal(external, await File.ReadAllBytesAsync(fixture.WorldCopy, Token));
        Assert.False(File.Exists(fixture.TextureCopy));
        await fixture.AssertSourcesUnchanged();
        File.Delete(fixture.WorldCopy);
        Assert.Empty((await edits.SaveAsync(token: Token)).Errors);
        Assert.False(edits.IsDirty);
        Assert.Equal(ModelFixture.GameZ(), await File.ReadAllBytesAsync(fixture.WorldCopy, Token));
        Assert.Equal(ModelFixture.Texture(), await File.ReadAllBytesAsync(fixture.TextureCopy, Token));
    }

    [Fact]
    public async Task ModelSaveKeepsTheOutputDirectoryUntilTemporaryCleanupCompletes()
    {
        if (!OperatingSystem.IsWindows()) return;
        using ModelCopy fixture = await ModelCopy.Create();
        string moved = fixture.Output + "-moved";
        int attempts = 0;
        var publish = fixture.Edits.PublishFile;
        fixture.Edits.PublishFile = (file, target, replace) =>
        {
            attempts++;
            Assert.ThrowsAny<IOException>(() => Directory.Move(fixture.Output, moved));
            publish(file, target, replace);
        };
        Assert.Empty((await fixture.Edits.SaveAsync(fixture.Output, Token)).Errors);
        Assert.Equal(2, attempts);
        Assert.Empty(Directory.GetFiles(fixture.Output, "*.tmp"));
        Directory.Move(fixture.Output, moved);
        Assert.True(File.Exists(Path.Combine(moved, "gamez.zbd")));
        await fixture.AssertSourcesUnchanged();
    }

    private sealed class ModelCopy : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "zstudio-model-copy-r23-" + Guid.NewGuid().ToString("N"));
        public string World => Path.Combine(root, "input", "gamez.zbd");
        public string Texture => Path.Combine(root, "input", "texture2.zbd");
        public string Output => Path.Combine(root, "copies");
        public string WorldCopy => Path.Combine(Output, "gamez.zbd");
        public string TextureCopy => Path.Combine(Output, "texture2.zbd");
        public ModelEditSession Edits { get; private set; } = null!;
        public static async Task<ModelCopy> Create()
        {
            ModelCopy fixture = new();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fixture.World)!);
                await File.WriteAllBytesAsync(fixture.World, ModelFixture.GameZ(), Token);
                await File.WriteAllBytesAsync(fixture.Texture, ModelFixture.Texture(), Token);
                using AssetResolver resolver = new(Path.GetDirectoryName(fixture.World)!);
                var world = await resolver.OpenCachedAsync(fixture.World, Token);
                fixture.Edits = new(world);
                DecodedImage image = new(2, 2, Enumerable.Repeat(new byte[] { 24, 80, 128, 255 }, 4).SelectMany(x => x).ToArray());
                var batch = new ModelImportBatch(Convert.ToHexString(SHA256.HashData(world.Bytes.Span)), "new", image,
                    new Dictionary<int, ImportedMesh> { [1] = ModelFixture.Mesh });
                fixture.Edits.Accept(await fixture.Edits.PrepareAsync(batch, resolver, Token));
                return fixture;
            }
            catch { fixture.Dispose(); throw; }
        }
        public async Task AssertSourcesUnchanged()
        {
            Assert.Equal(ModelFixture.GameZ(), await File.ReadAllBytesAsync(World, Token));
            Assert.Equal(ModelFixture.Texture(), await File.ReadAllBytesAsync(Texture, Token));
        }
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}

public sealed partial class AnimationTests
{
    [Theory]
    [InlineData("zbd_1999")]
    [InlineData("ordinary-source")]
    public async Task AnimationSaveAsRefusesAliasesIntoItsProtectedSourceRoot(string directory)
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = Path.Combine(Path.GetTempPath(), "zstudio-animation-alias-r23-" + Guid.NewGuid().ToString("N"));
        string protectedRoot = Path.Combine(root, directory); Directory.CreateDirectory(protectedRoot);
        try
        {
            using var alias = WindowsTestPathAlias.Create(protectedRoot, shortName: false);
            string path = Path.Combine(alias.Path, "new", "copy.zbd");
            await Assert.ThrowsAsync<IOException>(() => AnimationWriter.SaveAsAsync(Fixture(), path,
                Path.Combine(protectedRoot, "anim.zbd"), protectedRoot, TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFileSystemEntries(protectedRoot));
        }
        finally { Directory.Delete(root, true); }
    }
}
