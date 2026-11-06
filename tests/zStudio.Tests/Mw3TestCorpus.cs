using System.Buffers.Binary;
using System.IO;
using zStudio.Core.Formats;
using zStudio.Core.Sources;

namespace zStudio.Tests;

/// <summary>Copyright-free input for the source pipeline and real GUI/MCP protocol checks.</summary>
internal static class Mw3TestCorpus
{
    internal static void Create(string corpus)
    {
        Directory.CreateDirectory(Path.Combine(corpus, "c1")); byte[] world = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(world, 0x02971222); BinaryPrimitives.WriteUInt32LittleEndian(world.AsSpan(4), 27);
        foreach (int at in new[] { 12, 16 }) BinaryPrimitives.WriteInt32LittleEndian(world.AsSpan(at), 36);
        BinaryPrimitives.WriteInt32LittleEndian(world.AsSpan(20), 52); BinaryPrimitives.WriteInt32LittleEndian(world.AsSpan(28), -1); BinaryPrimitives.WriteInt32LittleEndian(world.AsSpan(32), 64);
        File.WriteAllBytes(Path.Combine(corpus, "c1", "gamez.zbd"), world);
        var root = ZrdText.Parse("( VALUE 1 )");
        File.WriteAllBytes(Path.Combine(corpus, "reader.zbd"), ArchiveSources.Write([new("fixture.zrd", "", ZrdWriter.Write(root))]));
    }
}
