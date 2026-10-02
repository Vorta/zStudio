using System.IO;
using System.Text;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Export;
using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Tests;

/// <summary>
/// A source project with two missions: m1 loads its ground, m2 its ground and an unplaced tank from its vehicle folder,
/// whose texture only m2's folders hold and whose destruction animation only m2's animation list names.
/// </summary>
internal sealed class SourceWorldFixture : IDisposable
{
    public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zstudio-world-" + Guid.NewGuid().ToString("N"));
    public string Project => System.IO.Path.Combine(Root, "project");
    public string Tank => "data/m2/models/bft/tank.gltf";
    public const string TankDefinitions = "data/common/zrdr/enemies/tank.zad";

    private static string WorldScript(string mission, string loads) => string.Join("\r\n",
        "set worldName world",
        $"SetModelDirectory ..\\data\\{mission}\\models",
        $"SetTextureDirectory ..\\data\\{mission}\\textures",
        "NewWorld %worldName%", "FindNode %worldName%", "GameGenSetWorld %worldName%", "FindNode %worldName%",
        "WorldOrigin 0.0 512.0", "WorldExtents 512.0 -512.0", "WorldPartition 256 -256",
        $"LoadGameGen {mission}.flt {mission}.flt", $"DeleteTree {mission}.flt",
        loads,
        $"GameZWriteZBDFile ..\\{mission}\\gamez.zbd", "Quit", "");

    public SourceWorldFixture()
    {
        Write("gamegen/m1.gs", WorldScript("m1", "# no vehicles"));
        Write("gamegen/m2.gs", WorldScript("m2", "SetModelDirectory ..\\data\\m2\\models\\bft\r\nLoadGameGen tank.flt tank"));
        WorldTexture rock = new("rock"), camo = new("camo");
        Model("data/m1/models", "m1", "../textures", Node("ground", Quad(new() { Texture = rock, Flags = 0x1FF }, 64, 0)));
        Model("data/m2/models", "m2", "../textures", Node("ground", Quad(new() { Texture = rock, Flags = 0x1FF }, 64, 0)));
        var hull = Node("hull", Quad(new() { Texture = camo, Flags = 0x1FF }, 4, 1));
        Model("data/m2/models/bft", "tank", "../../textures/bft", hull);
        Png("data/m1/textures/rock.png", 255, 0, 0); Png("data/m2/textures/rock.png", 255, 0, 0); Png("data/m2/textures/bft/camo.png", 0, 128, 0);
        Write("data/m1/zrdr/anim.zad", """
            (
              ANIMATION_DEFINITIONS (
                GRAVITY ( -9.8 )
                ANIMATION_LIST (
                  ANIMATION_DEFINITION_FILE ( "..\\data\\m1\\zrdr\\gates.zad" )
                )
              )
            )
            """);
        Write("data/m1/zrdr/gates.zad", """
            (
              ANIMATION_DEFINITIONS (
                ANIMATION_LIST (
                  ANIMATION_DEFINITION (
                    NAME ( ground )
                    ANIMATION_NAME ( sink )
                    ACTIVATION ( ON_CALL )
                    SEQUENCE_DEFINITION ( NAME ( down ) OBJECT_ACTIVE_STATE ( NAME ( ground ) STATE ( INACTIVE ) ) )
                  )
                )
              )
            )
            """);
        Write("data/m2/zrdr/anim.zad", """
            (
              ANIMATION_DEFINITIONS (
                GRAVITY ( -9.8 )
                ANIMATION_LIST (
                  ANIMATION_DEFINITION_FILE ( "..\\data\\common\\zrdr\\enemies\\tank.zad" )
                )
              )
            )
            """);
        Write(TankDefinitions, """
            (
              ANIMATION_DEFINITIONS (
                ANIMATION_LIST (
                  ANIMATION_DEFINITION (
                    NAME ( tank )
                    ANIMATION_NAME ( tank_die )
                    ACTIVATION ( ON_CALL )
                    SEQUENCE_DEFINITION ( NAME ( boom ) OBJECT_ACTIVE_STATE ( NAME ( hull ) STATE ( INACTIVE ) ) )
                  )
                )
              )
            )
            """);
    }

    public string Path(string relative) => System.IO.Path.Combine(Project, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
    public void Write(string relative, string text) => Write(relative, Encoding.ASCII.GetBytes(text));
    public void Write(string relative, byte[] bytes) { string path = Path(relative); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes); }
    private void Png(string relative, byte r, byte g, byte b)
    {
        byte[] rgba = new byte[4 * 4 * 4];
        for (int i = 0; i < 16; i++) { rgba[i * 4] = r; rgba[i * 4 + 1] = g; rgba[i * 4 + 2] = b; rgba[i * 4 + 3] = 255; }
        Write(relative, PngEncoder.Encode(new DecodedImage(4, 4, rgba)));
    }
    private static WorldNode Node(string name, WorldModel? model = null)
    {
        WorldNode node = new(name, WorldNodeClass.Object3D) { Model = model, Flags = WorldGltf.DefaultCarried };
        node.SetPayloadInt(0, 0x28);
        return node;
    }
    private static WorldModel Quad(WorldMaterial material, float size, float y)
    {
        ModelBuilder builder = new();
        builder.Add(new([new(0, y, 0), new(size, y, 0), new(size, y, -size), new(0, y, -size)], [new(0, 0), new(1, 0), new(1, 1), new(0, 1)], [], [], material));
        return builder.Finish();
    }
    private void Model(string folder, string stem, string textures, WorldNode root)
    {
        var (json, bin) = WorldGltf.Export([root], 0xFF, new() { Texture = t => ($"{textures}/{t.Name}.png", 0) }).Write(stem + ".bin");
        Write($"{folder}/{stem}.bin", bin); Write($"{folder}/{stem}.gltf", json);
    }
    /// <summary>
    /// Replaces m1's database with: ground (named by an animation), two adjacent flat pieces in zone 3, a piece over
    /// one of them with the same attributes, and a landmark sky.
    /// </summary>
    public void WriteTerrainDatabase()
    {
        WorldTexture rock = new("rock");
        WorldNode Piece(string name, float x0, float z0, float size, float y, uint flags = WorldGltf.DefaultCarried, uint zone = 3)
        {
            ModelBuilder builder = new();
            builder.Add(new([new(x0, y, z0 + size), new(x0 + size, y, z0 + size), new(x0 + size, y, z0), new(x0, y, z0)], [new(0, 0), new(1, 0), new(1, 1), new(0, 1)], [], [],
                new() { Texture = rock, Flags = 0x1FF }, Zone: 0xFFFF0001 | zone << 8));
            WorldNode node = new(name, WorldNodeClass.Object3D) { Model = builder.Finish(), Flags = flags, Zone = zone };
            node.SetPayloadInt(0, 0x28);
            return node;
        }
        List<WorldNode> roots =
        [
            Piece("ground", 0, 0, 64, 0),
            Piece("flat_a", 200, 300, 50, 0), Piece("flat_b", 250, 300, 50, 0), Piece("flat_over", 210, 310, 20, 10),
            Piece("sky", 0, 0, 512, 400, WorldGltf.DefaultCarried | 0x80, 0xFF),
        ];
        var (json, bin) = WorldGltf.Export(roots, 0xFF, new() { Texture = t => ($"../textures/{t.Name}.png", 0) }).Write("m1.bin");
        Write("data/m1/models/m1.bin", bin); Write("data/m1/models/m1.gltf", json);
    }

    public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
}
