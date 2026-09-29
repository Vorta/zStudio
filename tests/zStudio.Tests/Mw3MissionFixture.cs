using System.Buffers.Binary;
using System.IO;
using System.Text;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Tests;

internal sealed class Mw3MissionFixture : IDisposable
{
    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "zstudio-mw3-layout-" + Guid.NewGuid().ToString("N"));
    public string ReaderPath => Path.Combine(Folder, "readerm1.zbd");
    public ZbdDocument World { get; }
    public AssetResolver Resolver { get; }
    public byte[] ReaderBytes { get; }
    public Mw3MissionFixture(params string[] names) : this(0, names) { }
    public Mw3MissionFixture(float heading, params string[] names)
    {
        Directory.CreateDirectory(Folder);
        var rows = names.SelectMany((name, i) => new[] { S(name), A(I(0), A(F(i + 10), F(2), F(3)), F(heading)) }).ToArray();
        byte[] aiv = ZrdWriter.Write(A(rows));
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.Latin1, true))
        {
            writer.Write(aiv); writer.Write(0); writer.Write(aiv.Length);
            byte[] entry = new byte[140]; Encoding.Latin1.GetBytes("aiv.zrd").CopyTo(entry, 0); writer.Write(entry);
            writer.Write(1); writer.Write(1);
        }
        ReaderBytes = stream.ToArray(); File.WriteAllBytes(ReaderPath, ReaderBytes);
        byte[] header = new byte[36]; BinaryPrimitives.WriteUInt32LittleEndian(header, 0x02971222); BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 27);
        string worldPath = Path.Combine(Folder, "gamez.zbd"); File.WriteAllBytes(worldPath, header);
        World = new(worldPath, new(header.Length, DateTime.MinValue), new(FormatFamily.GameZ, 27, Recognition.Supported, "fixture"), header) { Scene = new() };
        World.Scene.Nodes.Add(new(0, "world", "world", null, [], [], new(), new()));
        World.Scene.Nodes.Add(new(1, "actor", "object3d", null, [], [], new() { ["flags"] = 4 }, new() { ["flags"] = 8 }));
        Resolver = new(Folder);
    }
    public void Dispose() { Resolver.Dispose(); Directory.Delete(Folder, true); }
    private static ZrdNode A(params ZrdNode[] children) => ZrdNode.Create(ZrdKind.Array) with { Children = children };
    private static ZrdNode S(string value) => ZrdNode.Create(ZrdKind.String) with { Text = value };
    private static ZrdNode I(int value) => ZrdNode.Create(ZrdKind.Int) with { Bits = unchecked((uint)value) };
    private static ZrdNode F(float value) => ZrdNode.Create(ZrdKind.Float) with { Bits = BitConverter.SingleToUInt32Bits(value) };
}
