using System.Numerics;
using System.IO;
using System.Text;
using zStudio.Core;
using zStudio.Core.Animation;
using zStudio.Core.Export;
using zStudio.Core.Formats;
using zStudio.Core.Sources;
using zStudio.Core.Worlds;

namespace zStudio.Tests;

/// <summary>Copyright-free native scripts, geometry, definitions and compiled keyframes for GUI/MCP and Core checks.</summary>
internal static class Mw3TestCorpus
{
    internal const int OutputCount = 7;
    internal const string Resource = "data/common/zrdr/fixture.zrd";
    internal const string Definitions = "data/c1/zrdr/envmodels/ground.zad";
    internal const string Script = "data/c1/zrdr/envmodels/ground.zan";
    internal static byte[] AddLooseSounds(string corpus)
    {
        string reader = Path.Combine(corpus, "reader.zbd");
        var resources = ArchiveSources.Read(File.ReadAllBytes(reader)).Select(m => new ArchiveSources.Entry(m.Name, m.SourceField ?? "", m.Name == "sounds.zrd"
            ? ZrdWriter.Write(ZrdText.Parse(ZrdText.Write(ZrdDecoder.Read(m.Payload)).Replace("( absent absent.wav )",
                "( absent absent.wav ) ( mech_engine mech_engine_np.wav ) ( soilDefault footsteps.wav ) ( soilSnow footsteps.wav )", StringComparison.Ordinal))) : m.Payload.ToArray())).ToArray();
        File.WriteAllBytes(reader, ArchiveSources.Write(resources));
        string bank = Path.Combine(corpus, "soundsH.zbd");
        var members = ArchiveSources.Read(File.ReadAllBytes(bank)).Select(m => new ArchiveSources.Entry(m.Name, "", m.Payload.ToArray())).ToList();
        members.Add(new("mech_engine.wav", "", SourceFixture.Tone(new(22050, 8, 1), 32, null)));
        File.WriteAllBytes(bank, ArchiveSources.Write(members));
        byte[] engine = SourceFixture.Tone(new(22050, 16, 1), 64, null);
        File.WriteAllBytes(Path.Combine(corpus, "mech_engine_np.wav"), engine);
        File.WriteAllBytes(Path.Combine(corpus, "footsteps.wav"), SourceFixture.Tone(new(22050, 16, 1), 48, null));
        File.WriteAllBytes(Path.Combine(corpus, "soil_metal_2s.wav"), engine);
        return engine;
    }
    internal static void AddTextureTier(string corpus)
    {
        var original = FormatRegistry.Default.OpenBytes("rtexture.zbd", File.ReadAllBytes(Path.Combine(corpus, "c1/rtexture.zbd")));
        var textures = original.Assets.Select(a =>
        {
            var image = TextureDecoder.Decode(original, a);
            for (int i = 3; i < image.Rgba.Length; i += 4) image.Rgba[i] = 255;
            return new PackTexture(a.Name, a.Name, image, a.Name == "rock" ? 3 : 0);
        }).ToArray();
        File.WriteAllBytes(Path.Combine(corpus, "c1/rtexture2.zbd"), TexturePackBuilder.Build(textures,
            new("rtexture2.zbd", TexturePackKind.Interface, null, 4096) { Mw3Pixels = true }).Bytes);
    }
    internal static byte[] AddSounds(string corpus, string? declarations = null)
    {
        byte[] wave = SourceFixture.Tone(new(22050, 16, 1), 64, 12);
        var definitions = ZrdText.Parse("( HIGH_DEFAULT ( 22050 16 1 ) SETS ( first ( " + (declarations ??
            "( mech_powerup \"0001.wav\" VOICE LOOPED VOLUME 0.7 QUEUE () ) ( menu_select button.wav ) ( second_id \"0001.wav\" ) ( absent absent.wav )") + " ) ) )");
        string path = Path.Combine(corpus, "reader.zbd");
        var entries = ArchiveSources.Read(File.ReadAllBytes(path)).Select(m => new ArchiveSources.Entry(m.Name, m.SourceField ?? "", m.Payload.ToArray())).ToList();
        entries.Add(new("sounds.zrd", "D:\\Mech3dev\\data\\common\\zrdr\\sou123.TMP", ZrdWriter.Write(definitions)));
        File.WriteAllBytes(path, ArchiveSources.Write(entries));
        File.WriteAllBytes(Path.Combine(corpus, "soundsH.zbd"), ArchiveSources.Write([new("0001.wav", "", wave),
            new("button.wav", "", SourceFixture.Tone(new(22050, 16, 1), 32, null)), new("orphan.wav", "", wave)]));
        return wave;
    }
    internal static void Create(string corpus, bool staleGravity = false, bool alphaTexture = false)
    {
        Directory.CreateDirectory(Path.Combine(corpus, "c1"));
        WorldTexture texture = new("rock"); WorldMaterial material = new() { Texture = texture, Flags = 0x1FF, Mw3Flags = 0x11 };
        // Retail MW3 polygons exceed RECOIL's 57-corner compiler limit; keep this native polygon whole.
        WorldModel model = new();
        for (int i = 0; i < 64; i++) { double angle = i * Math.Tau / 64; model.Vertices.Add(new(8 + 7 * (float)Math.Cos(angle), 0, -8 + 7 * (float)Math.Sin(angle))); }
        model.Polygons.Add(new() { Material = material, Vertices = Enumerable.Range(0, 64).ToArray(), Uvs = model.Vertices.Select(v => new Vector2(v.X / 16, -v.Z / 16)).ToArray(), Colors = Enumerable.Repeat(new Vector3(255, 128, 64), 64).ToArray() });
        WorldNode ground = new("ground", WorldNodeClass.Object3D) { Model = model, Flags = 0x0100001C };
        ground.SetPayloadInt(0, 9); foreach (int at in new[] { 0x24, 0x28, 0x2C, 0x30, 0x40, 0x50 }) ground.SetPayloadFloat(at, 1);
        WorldNode reserve = new("reserve", WorldNodeClass.Object3D) { Flags = 0x0308051C };
        reserve.SetPayloadInt(0, 0x09); reserve.SetPayloadFloat(0x24, 1); reserve.SetPayloadFloat(0x28, 1); reserve.SetPayloadFloat(0x2C, 1); reserve.SetPayloadFloat(0x54, 3);
        var gltf = WorldGltf.Export([ground, reserve], WorldGltf.DefaultZone, new() { Game = GameVariant.MechWarrior3, RegisterWithWorld = n => n != reserve, Texture = _ => ("../textures/rock.png", 0) }, new("c1.flt", WorldNodeClass.Object3D) { Zone = WorldGltf.DefaultZone, Mw3Words = [0, 160, 0, 0] }).Write("c1.bin");
        var definitions = ZrdText.Parse("( ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION ( NAME ( ground ) ACTIVATION ( ON_CALL ) SEQUENCE_DEFINITION ( NAME ( move ) OBJECT_MOTION_SI_SCRIPT ( NAME ( ground ) SCRIPT_FILENAME ( ground.zan ) SCRIPT_FRAME_RATE ( 30 ) ) ) ) ) ) )");
        if (staleGravity) definitions = ZrdText.Parse(ZrdText.Write(definitions).Replace("NAME ( move )", "NAME ( move ) OBJECT_MOTION ( NAME ( ground ) GRAVITY ( LOCAL 0.0 ) TRANSLATION ( 0 45 2 0 ) RUN_TIME ( 1 ) )", StringComparison.Ordinal));
        var root = ZrdText.Parse("( ANIMATION_DEFINITIONS ( ANIMATION_PATH ( \"..\\\\data\\\\c1\\\\zrdr\\\\envmodels\" ) ANIMATION_LIST ( ANIMATION_DEFINITION_FILE ( ground.zad ) ) ) )");
        // The original SI compiler keys all channels at the first/last segment.
        const string script = "SI Animation Script\nFRAMES: 2\nOBJECTS: 1\n\nFrame: 1\nObject: ground\nScaling: 1 1 1\nRotation: 0 0 0\nTranslation: 0 0 0\n\nFrame: 3\nObject: ground\nScaling: 1 1 1\nRotation: 0 0 0\nTranslation: 0 0.06666667 0\n";
        const string worldScript = "set DB c1\nSetModelDirectory ..\\data\\c1\\models\nSetTextureDirectory ..\\data\\c1\\textures\nNewWorld world\nFindNode world\nGameGenSetWorld world\nWorldOrigin 0 0\nWorldExtents 16 -16\nWorldPartition 16 -16\nLoadGameGen %DB%.gltf %DB%.flt intersect_off altitud_off\nFindNode ground\nObject3DSetScrollAlways on 0.015 0\nifdef COMPILE\nGameZWriteZBDFile zbd\\c1\\gamez.zbd\nendif\nQuit\n";
        Dictionary<string, byte[]> sources = new(StringComparer.OrdinalIgnoreCase)
        {
            ["data/c1/models/c1.gltf"] = gltf.Json, ["data/c1/models/c1.bin"] = gltf.Binary,
            [Definitions] = ZrdText.Encode(definitions), ["data/c1/zrdr/anim.zad"] = ZrdText.Encode(root), [Script] = Encoding.ASCII.GetBytes(script),
            ["gamegen/c1.gs"] = Encoding.ASCII.GetBytes(worldScript), ["data/c1/textures/rock.png"] = PngEncoder.Encode(new(1, 1, [255, 255, 255, 255]))
        };
        MemoryFiles files = new(sources); WorldAssembler assembler = new(files) { Game = GameVariant.MechWarrior3 };
        var world = assembler.Assemble("c1.gs"); Write("c1/gamez.zbd", GameZWriter.Write(world));
        Write("c1/anim.zbd", AnimationCompiler.Compile(files, "data/c1/zrdr/anim.zad", world.Nodes.Select(n => n.Name).ToArray(), GameVariant.MechWarrior3).Bytes);
        if (staleGravity) definitions = ZrdText.Parse(ZrdText.Write(definitions).Replace("GRAVITY ( LOCAL 0.0 )", "", StringComparison.Ordinal));
        var compiledRoot = ZrdText.Parse("( ANIMATION_DEFINITIONS ( ANIMATION_PATH ( \"..\\\\data\\\\c1\\\\zrdr\\\\envmodels\" ) ANIMATION_LIST ( ANIMATION_DEFINITION_FILE ( ground.zrd ) ) ) )");
        Write("c1/reader.zbd", ArchiveSources.Write([new("anim.zrd", "D:\\Mech3dev\\data\\c1\\zrdr\\ani123.TMP", ZrdWriter.Write(compiledRoot)),
            new("ground.zrd", "D:\\Mech3dev\\data\\c1\\zrdr\\envmodels\\gro123.TMP", ZrdWriter.Write(definitions)),
            new("mission.zrd", "D:\\Mech3dev\\data\\c1\\zrdr\\mis123.TMP", ZrdWriter.Write(ZrdText.Parse("( VALUE 1 )")))]));
        Write("reader.zbd", ArchiveSources.Write([new("fixture.zrd", "D:\\Mech3dev\\data\\common\\zrdr\\fix123.TMP", ZrdWriter.Write(ZrdText.Parse("( VALUE 1 )")))]));
        var lines = GameGenScriptText.Tokenize(worldScript.Replace("%DB%.gltf", "%DB%.flt", StringComparison.Ordinal));
        var instructions = lines.Select(t => new ScriptInstruction(Guid.NewGuid(), t, ReadOnlyMemory<byte>.Empty, null)).ToArray();
        byte[] header = new byte[12]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header, 0x08971119); System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 7);
        Write("interp.zbd", PreparedScriptWriter.Write(new(header, ReadOnlyMemory<byte>.Empty, [new(Guid.NewGuid(), null, "c1.gs", 1_000_000_000, new byte[128], instructions, ReadOnlyMemory<byte>.Empty)], ReadOnlyMemory<byte>.Empty)));
        Write("c1/rtexture.zbd", TexturePackBuilder.Build([new("rock", "rock", new(1, 1, [255, 255, 255, alphaTexture ? (byte)128 : (byte)255])), new("scripttex", "scripttex", new(1, 1, [255, 0, 0, 255]), 1)], new("rtexture.zbd", TexturePackKind.Interface, null, 4096)).Bytes);
        using MemoryStream image = new(); using BinaryWriter writer = new(image);
        writer.Write(0); writer.Write(1); writer.Write(0); writer.Write(1); writer.Write(0); writer.Write(0);
        byte[] imageName = new byte[32]; Encoding.ASCII.GetBytes("button").CopyTo(imageName, 0); writer.Write(imageName); writer.Write(64); writer.Write(-1);
        writer.Write(3u); writer.Write((ushort)2); writer.Write((ushort)1); writer.Write(0); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)(43 << 5)); writer.Write((ushort)0);
        Write("rimage.zbd", image.ToArray());
        void Write(string path, byte[] bytes) => File.WriteAllBytes(Path.Combine(corpus, path), bytes);
    }
    private sealed class MemoryFiles(IReadOnlyDictionary<string, byte[]> files) : IProjectFiles
    {
        public bool Exists(string path) => files.ContainsKey(path);
        public byte[] Read(string path, CancellationToken token, ProjectReadLimits limits) { var bytes = files[path]; limits.Validate(bytes); return bytes; }
    }
}
