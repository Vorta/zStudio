using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Recoil.Zbd.Core.Worlds;

/// <summary>Node classes the engine serializes (CZZbd::WriteSingleNodeClassData, retail 0x4544B0).</summary>
public enum WorldNodeClass { Plain = 0, Camera = 1, World = 2, Window = 3, Display = 4, Object3D = 5, Lod = 6, Light = 9, Sound = 10 }

/// <summary>An axis-aligned box as the engine stores it: min then max.</summary>
public readonly record struct WorldBox(Vector3 Min, Vector3 Max)
{
    public static readonly WorldBox Empty = new(Vector3.Zero, Vector3.Zero);
    public WorldBox Union(WorldBox other) => new(Vector3.Min(Min, other.Min), Vector3.Max(Max, other.Max));
    public static WorldBox Of(IEnumerable<Vector3> points)
    {
        Vector3 min = new(float.MaxValue), max = new(float.MinValue); bool any = false;
        foreach (var p in points) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); any = true; }
        return any ? new(min, max) : Empty;
    }
    public IEnumerable<Vector3> Corners()
    {
        for (int i = 0; i < 8; i++) yield return new((i & 1) != 0 ? Max.X : Min.X, (i & 2) != 0 ? Max.Y : Min.Y, (i & 4) != 0 ? Max.Z : Min.Z);
    }
}

/// <summary>
/// An entry of the world's texture directory: a pack texture name the materials use (≤19 characters, looked up in the
/// mission pack and image.zbd when the mission loads).
/// </summary>
public sealed class WorldTexture(string name)
{
    public string Name { get; set; } = name;
    /// <summary>The 20-byte name field; retail entries keep residue after the terminator, which the engine ignores.</summary>
    public byte[]? NameField { get; set; }
    /// <summary>Load state: 2 (pending) in every retail file; TexDirLoadPendingEntries loads pending entries.</summary>
    public uint State { get; set; } = 2;
    public WorldTexture? NextVariant { get; set; }
}

/// <summary>A material slot (zModel material, 0x2C bytes).</summary>
public sealed class WorldMaterial
{
    /// <summary>Low byte: probably opacity (0xFF in almost all retail slots). 0x100 textured is set from <see cref="Texture"/>; 0x200 pinned surface; 0x400 texture cycle.</summary>
    public ushort Flags { get; set; } = 0x00FF;
    public ushort PackedColor { get; set; } = 0x7FFF;
    /// <summary>Colour in 0–255 per channel.</summary>
    public Vector3 Color { get; set; } = new(255);
    public WorldTexture? Texture { get; set; }
    public float Field14 { get; set; }
    public float Field18 { get; set; } = 0.5f;
    public float Field1C { get; set; } = 0.5f;
    /// <summary>Surface type (userTag): 0 default, 1 water, 2 seafloor, 3 quicksand, 4 lava, 5 fire, 6+ from LoadSoils.</summary>
    public uint Soil { get; set; }
}

/// <summary>A polygon entry: corner indices, optional per-corner normals and UVs, its material and draw attributes.</summary>
public sealed class WorldPolygon
{
    /// <summary>Entry flags above the corner count: 0x100 shows the back face, 0x200 marks normal indices (set from <see cref="Normals"/>).</summary>
    public uint Flags { get; set; }
    /// <summary>Draw priority (Object3DSetPriority): 0, 1, 2, 4, 5 or 10 in retail.</summary>
    public int Priority { get; set; }
    public WorldMaterial? Material { get; set; }
    /// <summary>Zone tag: count byte then up to three signed zone ids, 0xFF padding (0xFFFFFF00 = none).</summary>
    public uint Zone { get; set; } = 0xFFFFFF00;
    public int[] Vertices { get; set; } = [];
    public int[] Normals { get; set; } = [];
    public Vector2[] Uvs { get; set; } = [];
}

/// <summary>A point entry of a model (lens-flare points): a 76-byte record plus its points.</summary>
public sealed class WorldPoint
{
    public byte[] Record { get; set; } = new byte[76];
    public Vector3[] Vertices { get; set; } = [];
}

/// <summary>A display instance (DI): one model's geometry, shared by every node that uses it.</summary>
public sealed class WorldModel
{
    /// <summary>0 normal, 1 facade (billboard; bounds symmetric about the origin), 2 points.</summary>
    public uint Mode { get; set; }
    /// <summary>0x01 lighting on (NodeSetLighting), 0x02 set by the allocator, 0x04 textures registered to the world.</summary>
    public uint Flags { get; set; } = 0x3;
    public List<Vector3> Vertices { get; } = [];
    public List<Vector3> Normals { get; } = [];
    /// <summary>Blend (morph) vertices and their scale.</summary>
    public List<Vector3> Morphs { get; } = [];
    public float MorphFactor { get; set; }
    public float ScrollU { get; set; }
    public float ScrollV { get; set; }
    public uint ScrollFrame { get; set; }
    public List<WorldPoint> Points { get; } = [];
    public List<WorldPolygon> Polygons { get; } = [];
    /// <summary>Bounding sphere centre and approximate radius (zDi::RebuildBounds); recomputed when a world is compiled.</summary>
    public Vector3 BoundsCentre { get; set; }
    public float BoundsRadius { get; set; }
}

/// <summary>A cell of the world's area partition grid and the nodes it holds.</summary>
public sealed class WorldArea
{
    /// <summary>The 64-byte area record (flags, index, cell corner, box, centre, radius); counts and pointers are written from <see cref="Nodes"/>.</summary>
    public byte[] Record { get; set; } = new byte[64];
    public List<WorldNode> Nodes { get; } = [];
}

/// <summary>A node slot: base fields, class data and the references the writer turns into slot indices.</summary>
public sealed class WorldNode
{
    public static int PayloadSize(WorldNodeClass kind) => kind switch
    {
        WorldNodeClass.Plain => 0, WorldNodeClass.Camera => 0x1E8, WorldNodeClass.World => 0xAC, WorldNodeClass.Window => 0xF8,
        WorldNodeClass.Display => 0x1C, WorldNodeClass.Object3D => 0x90, WorldNodeClass.Lod => 0x50, WorldNodeClass.Light => 0xE4,
        WorldNodeClass.Sound => 0x94, _ => throw new InvalidDataException($"Node class {kind} cannot be stored.")
    };

    public WorldNode(string name, WorldNodeClass kind)
    {
        Class = kind; Payload = new byte[PayloadSize(kind)];
        // gwNodeNew (retail Class.c) names a node "Default_node_name" and a rename writes over it, so the field keeps the
        // rest as residue ("world1\0_node_name"); an empty name is therefore never an all-zero field, which marks free slots.
        NameField = new byte[36]; DefaultName.CopyTo(NameField); Name = name;
    }
    private static ReadOnlySpan<byte> DefaultName => "Default_node_name"u8;
    /// <summary>The 36-byte name field (retail names keep residue after the terminator, e.g. "world1\0_node_name").</summary>
    public byte[] NameField { get; set; }
    public string Name
    {
        get { int end = Array.IndexOf(NameField, (byte)0); return Encoding.Latin1.GetString(NameField, 0, end < 0 ? 36 : end); }
        set
        {
            // gwNodeSetName: a name that fits is copied with its terminator; a longer one keeps 34 characters and the
            // field is terminated at its last byte.
            var bytes = Encoding.Latin1.GetBytes(value);
            if (bytes.Length >= NameField.Length) { bytes.AsSpan(0, 34).CopyTo(NameField); NameField[35] = 0; }
            else { bytes.CopyTo(NameField, 0); NameField[bytes.Length] = 0; }
        }
    }
    public WorldNodeClass Class { get; }
    /// <summary>Node flags; gwNodeNew's default is 0x0108001C (active, cached bounds and the variant gate).</summary>
    public uint Flags { get; set; } = 0x0108001C;
    public uint AuxFlags { get; set; }
    /// <summary>1 DI bounds dirty, 2 child bounds dirty, 4 view sphere stale; retail geometry nodes store 4.</summary>
    public uint BoundsFlags { get; set; }
    /// <summary>The u32 at +0x30: its low byte is the zone/variant id (default 0xFF in memory; 0–32 or 255 in retail files).</summary>
    public uint Zone { get; set; }
    public WorldModel? Model { get; set; }
    public uint Priority { get; set; } = 1;
    public int GridColumn { get; set; } = -1;
    public int GridRow { get; set; } = -1;
    public List<WorldNode> Parents { get; } = [];
    public List<WorldNode> Children { get; } = [];
    /// <summary>The render sphere cache at +0x64 (zero in files).</summary>
    public byte[] SphereCache { get; set; } = new byte[16];
    public WorldBox CachedBounds { get; set; } = WorldBox.Empty;
    public WorldBox PrimaryBounds { get; set; } = WorldBox.Empty;
    public WorldBox SecondaryBounds { get; set; } = WorldBox.Empty;
    /// <summary>Class data; the writer patches its node references (camera nodes) and counts.</summary>
    public byte[] Payload { get; set; }

    // Class-specific references.
    public WorldNode? CameraWorld { get; set; }
    public WorldNode? CameraWindow { get; set; }
    public WorldNode? CameraHorizon { get; set; }
    public WorldNode? CameraHorizonXZ { get; set; }
    public List<WorldNode> WorldLights { get; } = [];
    public List<WorldNode> WorldSounds { get; } = [];
    /// <summary>Area grid, row-major (rows × columns as the world record stores them).</summary>
    public List<WorldArea> Areas { get; } = [];
    public List<WorldNode> AttachedWorlds { get; } = [];

    // Typed views of class data used by the builder.
    public float PayloadFloat(int offset) => BinaryPrimitives.ReadSingleLittleEndian(Payload.AsSpan(offset));
    public void SetPayloadFloat(int offset, float value) => BinaryPrimitives.WriteSingleLittleEndian(Payload.AsSpan(offset), value);
    public int PayloadInt(int offset) => BinaryPrimitives.ReadInt32LittleEndian(Payload.AsSpan(offset));
    public void SetPayloadInt(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(Payload.AsSpan(offset), value);
    public override string ToString() => $"{Name} ({Class})";
}

/// <summary>
/// A whole GameZ v15 world in memory: the texture directory, material and model pools and the node table, in slot order.
/// <see cref="GameZWriter"/> serializes it the way CZZbd::WriteZBDFile (retail 0x454A50) does.
/// </summary>
public sealed class GameZWorld
{
    public const int NodeCapacityDefault = 16000, ModelCapacityDefault = 6000, MaterialCapacityDefault = 5000;
    /// <summary>The largest node and model tables a build script can ask for (SetGameZNodeArraySize, SetModel3DArraySize).</summary>
    public const int MaximumNodeCapacity = 65536;
    public List<WorldTexture> Textures { get; } = [];
    public List<WorldMaterial> Materials { get; } = [];
    public List<WorldModel> Models { get; } = [];
    /// <summary>Live nodes in slot order. Freed slots (retail m6) appear as <see cref="FreedSlots"/> at their positions.</summary>
    public List<WorldNode> Nodes { get; } = [];
    /// <summary>Slots freed during the original build, kept verbatim at their slot index (196 bytes each) for faithful rewrites.</summary>
    public SortedDictionary<int, byte[]> FreedSlots { get; } = [];
    /// <summary>Head of the node free list; null writes the first slot after the last used one.</summary>
    public int? FreeHead { get; set; }
    public int NodeCapacity { get; set; } = NodeCapacityDefault;
    public int ModelCapacity { get; set; } = ModelCapacityDefault;
    public int MaterialCapacity { get; set; } = MaterialCapacityDefault;
}
