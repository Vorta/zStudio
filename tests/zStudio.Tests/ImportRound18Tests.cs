using System.Buffers.Binary;
using System.Text;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ImportRound18Tests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("")][InlineData(",\"byteLength\":null")]
    [InlineData(",\"byteLength\":0")][InlineData(",\"byteLength\":-1")]
    public void BuffersRequirePositiveDeclaredLengthBeforeResolving(string length)
    {
        bool resolved = false;
        byte[] json = Encoding.UTF8.GetBytes("{\"asset\":{\"version\":\"2.0\"},\"buffers\":[{\"uri\":\"data.bin\"" + length + "}]}");
        Assert.Throws<InvalidDataException>(() => GltfDocument.Read(json, _ => { resolved = true; return new byte[16]; }, Token));
        Assert.False(resolved);
    }

    [Theory]
    [InlineData("")][InlineData(",\"byteLength\":null")][InlineData(",\"byteLength\":0")]
    public void InlineBuffersCannotBypassRequiredLength(string length)
    {
        byte[] json = Encoding.UTF8.GetBytes("{\"asset\":{\"version\":\"2.0\"},\"buffers\":[{\"uri\":\"data:application/octet-stream;base64,AQ==\"" + length + "}]}");
        Assert.Throws<InvalidDataException>(() => GltfDocument.Read(json, _ => throw new InvalidOperationException(), Token));
    }

    [Fact]
    public void PositiveBufferAndBufferlessDocumentRemainSupported()
    {
        GltfDocument.Read("""{"asset":{"version":"2.0"},"buffers":[{"uri":"data:application/octet-stream;base64,AQ==","byteLength":1}]}"""u8, _ => [], Token);
        var (json, bin) = new GltfDocument().Write("empty.bin", TestContext.Current.CancellationToken);
        Assert.Empty(bin);
        Assert.DoesNotContain("buffers", Encoding.UTF8.GetString(json));
        GltfDocument.Read(json, _ => throw new InvalidOperationException(), Token);
    }

    [Theory]
    [InlineData("")][InlineData("\"byteLength\":null")][InlineData("\"byteLength\":0")]
    public void GlbBuffersCannotBorrowTheChunkLength(string declaration)
    {
        byte[] Glb(string field)
        {
            string json = "{\"asset\":{\"version\":\"2.0\"},\"buffers\":[{" + field + "}]}";
            byte[] text = Encoding.UTF8.GetBytes(json.PadRight((json.Length + 3) / 4 * 4));
            byte[] file = new byte[32 + text.Length];
            "glTF"u8.CopyTo(file); BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 2);
            BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(8), file.Length);
            BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(12), text.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(16), 0x4E4F534A); text.CopyTo(file, 20);
            BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(20 + text.Length), 4);
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(24 + text.Length), 0x004E4942);
            return file;
        }
        Assert.Throws<InvalidDataException>(() => GltfDocument.Read(Glb(declaration), _ => [], Token));
        // One payload byte and three alignment bytes is valid.
        GltfDocument.Read(Glb("\"byteLength\":1"), _ => [], Token);
    }

    [Fact]
    public async Task CreatedNodesAndSharedAbsentRootsAreNotLoadFailures()
    {
        using var fixture = new SourceWorldFixture();
        fixture.Write("data/m1/zrdr/gates.zad", """
            ANIMATION_DEFINITIONS ( ANIMATION_LIST (
              ANIMATION_DEFINITION ( NAME ( ground ) SEQUENCE_DEFINITION ( NAME ( s )
                SOUND_NODE ( NAME ( new_sound ) ) LIGHT_STATE ( NAME ( new_light ) )
                OBJECT_ACTIVE_STATE ( NAME ( new_sound ) STATE ( ACTIVE ) )
                OBJECT_ACTIVE_STATE ( NAME ( new_light ) STATE ( ACTIVE ) ) ) )
              ANIMATION_DEFINITION ( NAME ( other_mission ) ANIMATION_ROOT_NAME ( absent ) )
            ) )
            """);
        var output = Assert.Single((await SourceBuilder.CheckAsync(fixture.Project, ["m1/anim.zbd"], token: Token)).Outputs);
        Assert.Equal("built", output.Status); Assert.Equal(1, output.Items);
    }

    [Theory]
    [InlineData("ANIMATION_ROOT_NAME ( absent )")]
    [InlineData("SEQUENCE_DEFINITION ( NAME ( s ) OBJECT_ACTIVE_STATE ( NAME ( absent ) STATE ( ACTIVE ) ) )")]
    [InlineData("SEQUENCE_DEFINITION ( NAME ( s ) EFFECT ( NAME ( absent ) ) )")]
    public async Task RejectedAnimationsCannotReplaceAnyDestinationOutput(string settings)
    {
        using var fixture = new SourceWorldFixture();
        string destination = Path.Combine(fixture.Root, "export");
        string[] outputs = ["m1/gamez.zbd", "m1/anim.zbd"];
        await SourceBuilder.ExportAsync(fixture.Project, destination, outputs, token: Token);
        var before = outputs.ToDictionary(p => p, p => File.ReadAllBytes(Path.Combine(destination, p)));
        fixture.Write("data/m1/zrdr/gates.zad", "ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION ( NAME ( ground ) " + settings + " ) ) )");
        fixture.Write("data/m1/zrdr/effects.zrd", "( EFFECTS ( ) )");
        var failed = Assert.Single((await SourceBuilder.CheckAsync(fixture.Project, outputs, token: Token)).Outputs, o => o.Status == "failed");
        Assert.Equal("m1/anim.zbd", failed.Path);
        Assert.Contains("absent", failed.Error);
        Assert.Contains("rejects", failed.Error);
        await Assert.ThrowsAsync<InvalidDataException>(() => SourceBuilder.ExportAsync(fixture.Project, destination, outputs, overwrite: true, token: Token));
        foreach (string output in outputs) Assert.Equal(before[output], File.ReadAllBytes(Path.Combine(destination, output)));
        Assert.Equal(outputs.Length, Directory.GetFiles(destination, "*", SearchOption.AllDirectories).Length);
        Assert.Empty(Directory.GetDirectories(destination, ".zstudio-*"));
    }

    [Fact]
    public async Task WarningCapCannotHideAnEngineRejection()
    {
        using var fixture = new SourceWorldFixture();
        string warnings = string.Concat(Enumerable.Range(0, 2001).Select(i => $"UNKNOWN_{i} ( ) "));
        fixture.Write("data/m1/zrdr/gates.zad", "ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION ( NAME ( ground ) " + warnings + " SEQUENCE_DEFINITION ( NAME ( s ) OBJECT_ACTIVE_STATE ( NAME ( absent ) STATE ( ACTIVE ) ) ) ) ) )");
        var output = Assert.Single((await SourceBuilder.CheckAsync(fixture.Project, ["m1/anim.zbd"], token: Token)).Outputs);
        Assert.Equal("failed", output.Status);
        Assert.Contains("absent", output.Error);
        Assert.InRange(output.Error!.Length, 1, 1024);
    }

    [Theory]
    [InlineData("WriteTextureSetMap fire1 rock.png")]
    [InlineData("WriteTextureSetMap rock.png")]
    [InlineData("WriteTextureSetMapExtra fire1 \"sub/rock.png\"")]
    [InlineData("WriteTextureSetMap fire1 %mask%")]
    public async Task DamageMaskUsesTheFinalArgumentAndStaysUnpaletted(string command)
    {
        using var fixture = new SourceWorldFixture();
        fixture.Write("data/m1/textures/fire1.png", File.ReadAllBytes(fixture.Path("data/m1/textures/rock.png")));
        fixture.Write("data/m1/textures/runtime.png", File.ReadAllBytes(fixture.Path("data/m1/textures/rock.png")));
        fixture.Write("gamegen/support/masks.gw", "ifdef enabled\n" + command + "\nendif\nifdef disabled\nWriteTextureSetMap fire1.png\nendif\n");
        fixture.Write("gamegen/support/unused.gw", "WriteTextureSetMap fire1.png\n");
        string world = File.ReadAllText(fixture.Path("gamegen/m1.gs"));
        // Execute the source after writing the world; Quit still stops the following registration.
        fixture.Write("gamegen/m1.gs", world.Replace("Quit", "set enabled TRUE\nset leak TRUE\nset mask sub/rock.png\nset maskScript support/masks.gw\nsource %maskScript%\nQuit\nWriteTextureSetMap fire1.png", StringComparison.Ordinal));
        fixture.Write("gamegen/m1_zbd.gs", "ifdef leak\nWriteTextureSetMap fire1.png\nendif\nset runtimeMask sub/runtime.png\nsource support/runtime-masks.gw\n");
        fixture.Write("gamegen/support/runtime-masks.gw", "WriteTextureSetMap fire1 %runtimeMask%\n");
        string destination = Path.Combine(fixture.Root, "export");
        await SourceBuilder.ExportAsync(fixture.Project, destination, ["m1/texture2.zbd", "m2/texture2.zbd"], token: Token);
        var pack = FormatRegistry.Default.OpenBytes("texture2.zbd", File.ReadAllBytes(Path.Combine(destination, "m1/texture2.zbd")), token: Token);
        var mask = Assert.Single(pack.Assets, a => a.Name == "rock");
        Assert.Contains("RGB565", mask.Summary);
        Assert.Contains("RGB565", Assert.Single(pack.Assets, a => a.Name == "runtime").Summary);
        Assert.Contains("Paletted", Assert.Single(pack.Assets, a => a.Name == "fire1").Summary);
        var other = FormatRegistry.Default.OpenBytes("texture2.zbd", File.ReadAllBytes(Path.Combine(destination, "m2/texture2.zbd")), token: Token);
        Assert.Contains("Paletted", Assert.Single(other.Assets, a => a.Name == "rock").Summary);
    }
}
