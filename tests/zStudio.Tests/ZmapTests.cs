using System.Buffers.Binary;
using System.Numerics;
using zStudio.Core.Formats;
using Xunit;

namespace zStudio.Tests;

public sealed class ZmapTests
{
    [Fact]
    public void VersionFiveKeepsOpaqueFieldsAndOnePointOutlinesByteForByte()
    {
        // Literal layout from the engine format, including the opaque header, signed index and minus zero.
        byte[] original = Convert.FromHexString("05000000EFBEADDE0000000000000000000000000000803F000000000000803F12345601000000000000400000008000008040F9FFFFFF");
        var token = TestContext.Current.CancellationToken;
        var map = ZmapCodec.Read(original, token);
        Assert.Equal(0xdeadbeefu, map.HeaderWord);
        var outline = Assert.Single(map.Outlines);
        Assert.Equal(new ZmapColor(0x12, 0x34, 0x56), outline.Color);
        Assert.Equal(-7, outline.ObjectiveIndex);
        Assert.Equal(new Vector3(2, 0, 4), Assert.Single(outline.Vertices).Position);
        Assert.Equal(original, ZmapCodec.Write(map, token));
        Assert.Throws<InvalidDataException>(() => ZmapCodec.Read(original.AsMemory(0, original.Length - 1), token));
        BinaryPrimitives.WriteInt32LittleEndian(original.AsSpan(35), int.MaxValue);
        Assert.Throws<InvalidDataException>(() => ZmapCodec.Read(original, token));
    }

    [Fact]
    public void WorkingCopyCanBeSavedAgainAndDetectsExternalChanges()
    {
        using SourceWorldFixture scratch = new();
        var token = TestContext.Current.CancellationToken;
        var map = new Zmap(0, Vector3.Zero, Vector3.One, []);
        var original = ZmapCodec.Write(map, token);
        var replacement = ZmapCodec.Write(map with { HeaderWord = 4 }, token);
        string destination = Path.Combine(scratch.Root, "m1.zmap");
        Assert.Null(ZmapFile.Save(destination, original, null, token));
        var opened = ZmapFile.OpenSnapshot(destination, token);
        Assert.Equal(destination, opened.Path);
        Assert.Equal(original, opened.Bytes);
        Assert.Throws<IOException>(() => ZmapFile.Save(destination, replacement, null, token));
        if (OperatingSystem.IsWindows())
        {
            var attributes = File.GetAttributes(destination);
            File.SetAttributes(destination, attributes | FileAttributes.ReadOnly);
            try { Assert.Throws<UnauthorizedAccessException>(() => ZmapFile.Save(destination, replacement, opened.Bytes, token)); }
            finally { File.SetAttributes(destination, attributes); }
        }
        Assert.Null(ZmapFile.Save(destination, replacement, opened.Bytes, token));
        Assert.Equal(4u, ZmapFile.Open(destination, token).HeaderWord);
        Assert.Throws<IOException>(() => ZmapFile.Save(destination, original, original, token));
        Assert.Equal(replacement, File.ReadAllBytes(destination));
        File.Delete(destination);
        Assert.ThrowsAny<IOException>(() => ZmapFile.Save(destination, original, replacement, token));
        Assert.False(File.Exists(destination));
        string protectedFolder = Directory.CreateDirectory(Path.Combine(scratch.Root, "zbd_1999")).FullName;
        Assert.Throws<IOException>(() => ZmapFile.Save(Path.Combine(protectedFolder, "m1.zmap"), original, null, token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(protectedFolder));
        Assert.Empty(Directory.EnumerateFiles(scratch.Root, ".zstudio-*"));
    }

    [Fact]
    public void SuppliedLooseMapsRoundTripWhenAvailable()
    {
        if (Environment.GetEnvironmentVariable("ZSTUDIO_ZMAP_CORPUS") is not { Length: > 0 } corpus) return;
        var token = TestContext.Current.CancellationToken;
        var paths = Directory.GetFiles(corpus, "*.zmap");
        Assert.NotEmpty(paths);
        foreach (string path in paths)
        {
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(bytes, ZmapCodec.Write(ZmapFile.Open(path, token), token));
        }
    }
}
