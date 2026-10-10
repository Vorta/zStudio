using System.Buffers.Binary;
using System.IO;
using System.Text;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class SourceModelAdmissionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Small = "{\"asset\":{\"version\":\"2.0\"},\"nodes\":[{\"name\":\"hull\"}]}";

    [Fact]
    public void TypedAdmissionReadsOnlyTheHeaderBeforeRefusingAndDoesNotHashThePayload()
    {
        foreach (byte[] bytes in new[] { Encoding.UTF8.GetBytes(Small.PadRight(256)), Glb(Small.PadRight(256), 64) })
        {
            using CountingStream stream = new(bytes);
            bool verified = false;
            var error = Assert.Throws<InvalidDataException>(() => SourceRead.AllAdmitted(stream, 1024, "any.extension",
                (prefix, length) => GltfDocument.CheckContainerAdmission(prefix, length, 128), Token, _ => verified = true));
            Assert.Contains("Model JSON", error.Message);
            Assert.Equal(20, stream.BytesRead);
            Assert.False(verified);
        }
        byte[] binary = Glb(Small, 512);
        using CountingStream allowed = new(binary);
        Assert.Equal(binary, SourceRead.AllAdmitted(allowed, binary.Length, "binary.gltf",
            (prefix, length) => GltfDocument.CheckContainerAdmission(prefix, length, 128), Token));
        Assert.Equal(binary.Length + 20, allowed.BytesRead);
    }

    [Fact]
    public void MalformedContainerHeadersRefuseBeforePayloadAllocationOrVerification()
    {
        byte[] good = Glb(Small, 64);
        byte[] truncated = good[..16];
        BinaryPrimitives.WriteUInt32LittleEndian(truncated.AsSpan(8), (uint)truncated.Length);
        byte[] overflow = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(overflow.AsSpan(12), uint.MaxValue);
        byte[] wrongKind = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(wrongKind.AsSpan(16), 0x004E4942);
        byte[] wrongTotal = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(wrongTotal.AsSpan(8), (uint)good.Length + 4);
        foreach (byte[] bytes in new[] { truncated, overflow, wrongKind, wrongTotal })
        {
            using CountingStream stream = new(bytes);
            bool verified = false;
            Assert.Throws<InvalidDataException>(() => SourceRead.AllAdmitted(stream, 1024, "model",
                (prefix, length) => GltfDocument.CheckContainerAdmission(prefix, length, 128), Token, _ => verified = true));
            Assert.InRange(stream.BytesRead, 0, 20);
            Assert.False(verified);
        }
    }

    [Theory]
    [InlineData("large.gltf", false)]
    [InlineData("large.glb", false)]
    [InlineData("large.gltf", true)]
    public void AnUnopenedMissionCannotAdmitOversizedJsonThroughTransformInspection(string name, bool binary)
    {
        using SourceWorldFixture fixture = new();
        const string edited = "data/check/edited.gltf";
        fixture.Write(edited, Small);
        byte[] content = binary ? Glb(Small.PadRight(256), 64) : Encoding.UTF8.GetBytes(Small.PadRight(256));
        File.WriteAllBytes(fixture.Path("data/check/" + name), content);
        fixture.Write("gamegen/m2.gs", $"SetModelDirectory ../data/check\nLoadGameGen {name} root\nFindSubNode hull\nObject3DRotate 0 1 0\n");
        SourceWorkspace workspace = new(fixture.Project);
        SourceScriptInspection inspection = new(workspace, Token, maximumModelJsonBytes: 128);
        var error = Assert.Throws<IOException>(() => SourceObjectEdits.TransformElsewhere(workspace, "m1", new WorldNodeProvenance { ModelFile = edited }, "hull", Token, inspection: inspection));
        Assert.Contains("Model JSON", Assert.IsType<InvalidDataException>(error.InnerException).Message);
        Assert.Equal(Encoding.UTF8.GetByteCount(Small), inspection.RetainedModelBytes);
        Assert.Equal(content, File.ReadAllBytes(fixture.Path("data/check/" + name)));
        Assert.False(workspace.CanUndo);
    }

    [Fact]
    public void CleanChangedDiskPendingAndPreparedBytesKeepTheirRespectiveIdentities()
    {
        using SourceWorldFixture fixture = new();
        const string path = "data/check/owned.gltf";
        fixture.Write(path, Small.PadRight(256));
        SourceWorkspace workspace = new(fixture.Project);
        workspace.Apply("Smaller", [(path, Encoding.UTF8.GetBytes(Small))], Token);
        workspace.Undo();
        fixture.Write(path, Small);
        Assert.Equal(Encoding.UTF8.GetBytes(Small), Assert.IsType<byte[]>(workspace.ReadModel(path, Token, 1024, 128)));

        SourceWorkspace dirty = new(fixture.Project);
        byte[] draft = Encoding.UTF8.GetBytes(Small.PadRight(256));
        dirty.Apply("Large draft", [(path, draft)], Token);
        Assert.Contains("Model JSON", Assert.Throws<InvalidDataException>(() => dirty.ReadModel(path, Token, 1024, 128)).Message);
        Assert.Equal("Large draft", dirty.UndoLabel);
        Assert.Equal(draft, Assert.IsType<byte[]>(dirty.Read(path, Token)));

        var prepared = workspace.BeginPreparedEdit();
        Assert.Equal(Encoding.UTF8.GetBytes(Small), Assert.IsType<byte[]>(prepared.Workspace.ReadModel(path, Token, 1024, 128)));
        DateTime stamp = File.GetLastWriteTimeUtc(fixture.Path(path));
        fixture.Write(path, Small.Replace("hull", "gate", StringComparison.Ordinal));
        File.SetLastWriteTimeUtc(fixture.Path(path), stamp); // Same length/stamp: the held digest must establish identity.
        Assert.Throws<SourceFileChangedException>(() => prepared.Workspace.ReadModel(path, Token, 1024, 128));
        Assert.Equal(0, workspace.UndoCount);
    }

    [Fact]
    public void APreparedGenericReadCannotForceHashingBeforeTheLaterTypedAdmission()
    {
        using SourceWorldFixture fixture = new();
        const string path = "data/check/owned.glb";
        fixture.Write(path, Small.PadRight(256));
        SourceWorkspace workspace = new(fixture.Project);
        var prepared = workspace.BeginPreparedEdit();
        Assert.NotNull(prepared.Workspace.Read(path, Token));
        Assert.Contains("Model JSON", Assert.Throws<InvalidDataException>(() => prepared.Workspace.ReadModel(path, Token, 1024, 128)).Message);
        Assert.False(workspace.IsDirty);
    }

    [Fact]
    public void AdmissionCancellationStopsBeforeTheDigestAndFullRead()
    {
        using CancellationTokenSource canceled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using CountingStream stream = new(Glb(Small, 512));
        bool verified = false;
        var error = Assert.Throws<OperationCanceledException>(() => SourceRead.AllAdmitted(stream, 1024, "model", (_, _) => canceled.Cancel(), canceled.Token, _ => verified = true));
        Assert.Equal(canceled.Token, error.CancellationToken);
        Assert.False(verified);
        Assert.Equal(20, stream.BytesRead);
    }

    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes)
    {
        internal int BytesRead { get; private set; }
        public override int Read(Span<byte> buffer) { int count = base.Read(buffer); BytesRead += count; return count; }
    }

    private static byte[] Glb(string json, int binaryLength)
    {
        byte[] text = Encoding.UTF8.GetBytes(json);
        int jsonLength = (text.Length + 3) & ~3;
        byte[] bytes = new byte[28 + jsonLength + binaryLength];
        "glTF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)bytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)jsonLength);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 0x4E4F534A);
        bytes.AsSpan(20, jsonLength).Fill((byte)' '); text.CopyTo(bytes, 20);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20 + jsonLength), (uint)binaryLength);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24 + jsonLength), 0x004E4942);
        return bytes;
    }
}
