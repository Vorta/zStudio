using System.Security.Cryptography;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Export;
using Recoil.Zbd.Core.Formats;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class EditBufferAdmissionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static DecodedImage Image(byte red = 24) => new(2, 2, Enumerable.Repeat(new byte[] { red, 80, 128, 255 }, 4).SelectMany(p => p).ToArray());

    [Fact]
    public async Task ModelPreparationChargesOriginalsAndEveryReplacementAtExactBoundary()
    {
        using Folder folder = new();
        for (int i = 0; i < 3; i++) await File.WriteAllBytesAsync(folder.PathOf($"texture{i}.zbd"), ModelFixture.Texture(), Token);
        var world = FormatRegistry.Default.OpenBytes(folder.PathOf("gamez.zbd"), ModelFixture.GameZ(), token: Token);
        using AssetResolver resolver = new(folder.Path);
        ModelEditSession session = new(world);
        var before = session.Current;
        ModelImportBatch batch = new(Convert.ToHexString(SHA256.HashData(world.Bytes.Span)), "new", Image(), new Dictionary<int, ImportedMesh> { [1] = ModelFixture.Mesh });
        // Three 24-byte originals and three outputs: original + one 40-byte row + 16-byte payload header + 8 RGB565 bytes.
        const long exact = 3 * (24 + 24 + 40 + 16 + 8);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => session.PrepareAsync(batch, resolver, exact - 1, Token));
        Assert.Contains("remaining edit buffer budget", error.Message);
        Assert.Same(before, session.Current); Assert.False(session.CanUndo); Assert.False(session.IsDirty);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.PrepareAsync(batch, resolver, exact, canceled.Token));
        Assert.Same(before, session.Current);
        var prepared = await session.PrepareAsync(batch, resolver, exact, Token);
        Assert.Equal(3, prepared.Textures.Count); Assert.Equal(3, prepared.Baselines!.Count);
        Assert.Equal(exact, prepared.Textures.Values.Sum(d => (long)d.Bytes.Length) + prepared.Baselines!.Values.Sum(d => (long)d.Bytes.Length));
        foreach (var document in prepared.Textures.Values) Assert.Equal("new", Assert.Single(document.Assets).Name);
        session.Accept(prepared); session.Undo(); Assert.Same(before, session.Current); session.Redo(); Assert.Same(prepared, session.Current);
    }

    [Theory]
    [InlineData("cold")]
    [InlineData("cache")]
    [InlineData("workspace")]
    public async Task ResolverAppliesRemainingAllowanceToEveryProvider(string provider)
    {
        using Folder folder = new(); string path = folder.PathOf("texture.zbd");
        await File.WriteAllBytesAsync(path, ModelFixture.Texture(), Token);
        using AssetResolver resolver = new(folder.Path);
        if (provider == "cache") await resolver.OpenCachedAsync(path, Token);
        if (provider == "workspace") resolver.SetWorkspaceSnapshots(Guid.NewGuid(), [FormatRegistry.Default.OpenBytes(path, ModelFixture.Texture(), token: Token)]);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => resolver.OpenCachedAsync(path, 23, Token));
        Assert.Contains(provider == "cold" ? "exceeds 23 bytes" : "remaining edit buffer budget", error.Message);
        var accepted = await resolver.OpenCachedAsync(path, 24, Token); Assert.Equal(24, accepted.Bytes.Length);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.OpenCachedAsync(path, 24, canceled.Token));
    }

    [Fact]
    public async Task TextureVariantColdReadUsesRemainingSourceAllowanceAndRefusalLeavesNoEdit()
    {
        using Folder folder = new(); string first = folder.PathOf("texture1.zbd"), second = folder.PathOf("texture2.zbd"), png = folder.PathOf("change.png");
        var empty = FormatRegistry.Default.OpenBytes(first, ModelFixture.Texture(), token: Token);
        byte[] pack = TexturePackWriter.Append(empty, "item", Image(), Token);
        await File.WriteAllBytesAsync(first, pack, Token); await File.WriteAllBytesAsync(second, pack, Token);
        await File.WriteAllBytesAsync(png, PngEncoder.Encode(Image(200), Token), Token);
        using AssetResolver resolver = new(folder.Path);
        TextureEditSession session = new(await resolver.OpenCachedAsync(first, Token)); var before = session.Current;
        TextureTarget[] targets = [new(first, 0), new(second, 0)];
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => session.PrepareAsync(png, 0, "item", targets, resolver, pack.Length * 2L - 1, Token));
        Assert.Contains($"exceeds {pack.Length - 1:N0} bytes", error.Message);
        Assert.Same(before, session.Current); Assert.False(session.CanUndo);
        var prepared = await session.PrepareAsync(png, 0, "item", targets, resolver, pack.Length * 2L, Token);
        Assert.Equal(2, prepared.After.Documents.Count);
        Assert.Equal(pack, await File.ReadAllBytesAsync(second, Token));
    }

    [Fact]
    public void FailedReservationDoesNotConsumeBudgetAndRepeatedDocumentIsBorrowedOnce()
    {
        EditBufferBudget budget = new(24);
        var document = FormatRegistry.Default.OpenBytes("texture.zbd", ModelFixture.Texture(), token: Token);
        Assert.Throws<InvalidDataException>(() => budget.Reserve(25)); Assert.Equal(24, budget.Remaining);
        budget.Document(document); budget.Document(document); Assert.Equal(0, budget.Remaining);
    }

    [Fact]
    public void AppendRefusesCompleteOutputBeforeTouchingOriginalPayload()
    {
        using var memory = new UnreadableMemory();
        var source = new ZbdDocument("texture.zbd", new(24, DateTime.MinValue), new(FormatFamily.TexturePack, 1, Recognition.Supported, "test"), memory.Bytes);
        var error = Assert.Throws<InvalidDataException>(() => TexturePackWriter.Append(source, "new", Image(), 87, Token));
        Assert.Contains("remaining edit buffer budget", error.Message);
    }
    private sealed class UnreadableMemory : System.Buffers.MemoryManager<byte>
    {
        public ReadOnlyMemory<byte> Bytes => CreateMemory(24);
        public override Span<byte> GetSpan() => throw new InvalidOperationException("Admission must precede reading or copying the source payload.");
        public override System.Buffers.MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }

    private sealed class Folder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zstudio-edit-admission-" + Guid.NewGuid().ToString("N"));
        public Folder() => Directory.CreateDirectory(Path);
        public string PathOf(string name) => System.IO.Path.Combine(Path, name);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
