using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;

namespace Recoil.Zbd.Core.Animation;

public enum AnimationFieldKind { Integer, Short, Float, Vector, Flags, Text }
public sealed record AnimationField(string Name, int Offset, AnimationFieldKind Kind, int Size = 4, int ReferenceTable = -1, bool ReadOnly = false, string Hint = "")
{
    public JsonNode Read(AnimationRecord record) => Kind switch
    {
        AnimationFieldKind.Text => JsonValue.Create(record.Text(Offset, Size))!,
        AnimationFieldKind.Vector => JsonData.Vector(record.Vector(Offset)),
        AnimationFieldKind.Float => JsonData.Number(record.F32(Offset)),
        AnimationFieldKind.Short => JsonValue.Create((int)record.I16(Offset))!,
        AnimationFieldKind.Flags => JsonValue.Create($"0x{record.U32(Offset):X8}")!,
        _ => JsonValue.Create(record.I32(Offset))!
    };
    public string Format(AnimationRecord record) => Kind switch
    {
        AnimationFieldKind.Vector => string.Join(", ", new[] { record.F32(Offset), record.F32(Offset + 4), record.F32(Offset + 8) }.Select(f => f.ToString("R", CultureInfo.InvariantCulture))),
        AnimationFieldKind.Float => record.F32(Offset).ToString("R", CultureInfo.InvariantCulture),
        _ => Read(record).ToString()
    };
    public void Write(AnimationRecord record, string text)
    {
        if (ReadOnly) throw new InvalidOperationException("Runtime/opaque fields are read-only.");
        int Integer() => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? unchecked((int)uint.Parse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)) : int.Parse(text, CultureInfo.InvariantCulture);
        switch (Kind)
        {
            case AnimationFieldKind.Text: record.SetText(Offset, text, Size); break;
            case AnimationFieldKind.Short: record.SetShort(Offset, checked((short)Integer())); break;
            case AnimationFieldKind.Float: record.SetFloat(Offset, float.Parse(text, CultureInfo.InvariantCulture)); break;
            case AnimationFieldKind.Vector:
                float[] xyz = text.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries).Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                if (xyz.Length != 3) throw new InvalidDataException("Enter three numbers: X, Y, Z.");
                record.SetVector(Offset, new Vector3(xyz[0], xyz[1], xyz[2])); break;
            default: record.SetInt(Offset, Integer()); break;
        }
    }
}

public sealed record AnimationEventSpec(byte Type, string Name, int Size, AnimationField[] Fields, string Support = "Engine-based", int DurationOffset = -1)
{
    public override string ToString() => $"{Name} · 0x{Type:X2}";
}

/// <summary>Event IDs follow retail RunSequenceEvents 0x45CC00, not historical CLI labels.</summary>
public static class AnimationCatalog
{
    private static AnimationField I(string name, int offset, bool readOnly = false) => new(name, offset, AnimationFieldKind.Integer, ReadOnly: readOnly);
    private static AnimationField F(string name, int offset, bool readOnly = false) => new(name, offset, AnimationFieldKind.Float, ReadOnly: readOnly);
    private static AnimationField V(string name, int offset, bool readOnly = false) => new(name, offset, AnimationFieldKind.Vector, 12, ReadOnly: readOnly);
    private static AnimationField B(string name, int offset = 12, string hint = "") => new(name, offset, AnimationFieldKind.Flags, Hint: hint);
    private static AnimationField N(string name, int offset, int table = 1, bool narrow = false) => new(name, offset, narrow ? AnimationFieldKind.Short : AnimationFieldKind.Integer, narrow ? 2 : 4, table);
    private static AnimationField S(string name, int offset, int size = 32) => new(name, offset, AnimationFieldKind.Text, size);
    public static string ModeName(int mode) => mode switch { 1 => "Entry elapsed", 2 => "Sequence elapsed", 3 => "Since previous event", _ => $"Unknown ({mode})" };
    public static readonly IReadOnlyList<AnimationEventSpec> Events =
    [
        new(1, "Play sound sample", 28, [N("Sample",12,4,true),N("Position node",14,1,true),V("Offset",16)]),
        new(2, "Sound node",72,[S("Sound name",12),N("Sound reference",44,3),B("Fields",48),I("Active",52),N("Parent node",56),V("Offset",60)]),
        new(3,"Spawn effect template",28,[N("Effect template",12,5,true),N("Position node",14,1,true),V("Offset",16)],"Approximate: effect rendering"),
        new(4,"Light",124,[S("Light name",12),N("Light reference",44,2),B("Fields",48),I("Active",52),I("Mode",56),I("Directional",60),I("Parameter",64),N("Basis node",68),V("Position / basis offset",72),V("Rotation",84),F("Inner range",96),F("Outer range",100),V("Specular RGB",104),F("Intensity",116),F("Falloff",120)],"Approximate: preview lighting"),
        new(5,"Animate light",112,[S("Light name",12),N("Light reference",44,2),F("Inner range start",48),F("Outer range start",52),F("Inner range rate",56),F("Outer range rate",60),F("Current inner range",64,true),F("Current outer range",68,true),V("Color start",72),V("Color rate",84),V("Current color",96,true),F("Duration (s)",108)],"Approximate: preview lighting",108),
        new(6,"Set active state",20,[I("Active",12),N("Target node",16,1,true)]),
        new(7,"Set position",32,[B("Fields",12,"1: add to current position; basis contributes world position; activation-dependent modes are approximate"),V("Position",16),N("Target node",28,1,true),N("Basis node",30,1,true)]),
        new(8,"Set scale",28,[V("Scale",12),N("Target node",24,1,true)]),
        new(9,"Set rotation",32,[B("Fields"),V("Rotation (engine radians)",16),N("Target node",28,1,true),N("Basis node",30,1,true)]),
        new(10,"Procedural motion",252,[B("Motion flags",12,"1: gravity/collision; 2: inherited velocity; 4: velocity; 8: random launch; 32: spin; 256: scale; 512: morph; 1024: timed; 2048: release sequence on collision; 4096: impact sound; 8192: gravity through world basis. The Grid option enables mesh-based collision at Y=0; terrain and inherited velocity remain unavailable."),N("Target node",16),F("Gravity acceleration",24),F("Morph rate",28),F("Yaw minimum (degrees)",32),F("Yaw maximum (degrees)",36),F("Pitch minimum",40),F("Pitch maximum",44),F("Speed minimum",48),F("Speed maximum",52),F("Launch acceleration minimum",56),F("Launch acceleration maximum",60),V("Initial velocity",64),V("Initial acceleration",76),V("Velocity state",88,true),V("Acceleration state",100,true),V("Direction state",112,true),F("Damping base",124),F("Damping step",128),F("Damping state",132,true),V("Spin velocity",136),V("Spin acceleration",148),V("Spin state",160,true),V("Scale velocity",172),V("Scale acceleration",184),V("Scale state",196,true),S("Collision release sequence",208),new("Sequence cache",240,AnimationFieldKind.Short,2,ReadOnly:true),N("Collision sample",242,4,true),F("Lookup scale",244),F("Duration (s)",248)],"Approximate: procedural physics and collision",248),
        new(11,"Animate transform / morph",144,[B("Channels",12,"1: position; 2: rotation; 4: scale; 8: morph blend"),N("Target node",16),F("Morph start",20),F("Morph end",24),F("Morph rate",28),V("Position start",32),V("Position end",44),V("Position rate",56),V("Rotation start",68),V("Rotation end",80),V("Rotation rate",92),V("Scale start",104),V("Scale end",116),V("Scale rate",128),F("Duration (s)",140)],"Engine-based",140),
        new(12,"Transform keyframes",32,[N("Target node",12),I("Reserved",16,true),F("Runtime local time",20,true),I("Runtime cursor",24,true),I("Runtime lookahead",28,true)]),
        new(13,"Set opacity override",24,[new("Enable alpha override",12,AnimationFieldKind.Short,2),new("Set opacity",14,AnimationFieldKind.Short,2),F("Opacity",16),N("Target node",20,1,true)]),
        new(14,"Animate opacity",36,[N("Target node",12),new("Start alpha override",16,AnimationFieldKind.Short,2),new("End alpha override",18,AnimationFieldKind.Short,2),F("Start opacity",20),F("End opacity",24),F("Opacity rate",28),F("Duration (s)",32)],"Engine-based",32),
        new(15,"Add child",16,[N("Parent node",12,1,true),N("Child node",14,1,true)]),
        new(16,"Remove child",16,[N("Parent node",12,1,true),N("Child node",14,1,true)]),
        new(17,"Select texture-cycle frame",20,[B("Fields",12,"1: reset variant cycle"),N("Target node",16,1,true),new("Texture frame",18,AnimationFieldKind.Short,2)]),
        new(18,"Animate beam",88,[B("Beam flags"),N("Beam node",16,1,true),N("Point A node",18,1,true),N("Point B node",20,1,true),V("Point A",24),V("Point B",36),F("Start fraction",48),F("Final start fraction",52),F("Start fraction rate",56),F("Start state",60,true),F("End fraction",64),F("Final end fraction",68),F("End fraction rate",72),F("End state",76,true),F("Duration (s)",80),F("Length limit",84)],"Approximate: beam geometry",80),
        new(19,"Launch animation between points",80,[B("Launch flags"),S("Animation name",16),new("Entry cache",48,AnimationFieldKind.Short,2,ReadOnly:true),N("Runtime slot",50,7,true),N("Node A",52,1,true),N("Node B",54,1,true),V("Point A",56),V("Point B",68)]),
        new(20,"Camera parameters",48,[B("Channels"),N("Camera node",16),F("Near clip",20),F("Far clip",24),F("Clip distance",28),F("Horizontal FOV (radians)",32),F("Vertical FOV (radians)",36),F("Viewport X",40),F("Viewport Y",44)],"Approximate: perspective camera"),
        new(21,"Animate camera",108,[B("Channels"),N("Camera node",16),V("Near clip: start/end/rate",20),V("Far clip: start/end/rate",32),V("Clip distance: start/end/rate",44),V("FOV X (radians): start/end/rate",56),V("FOV Y (radians): start/end/rate",68),V("Viewport X: start/end/rate",80),V("Viewport Y: start/end/rate",92),F("Duration (s)",104)],"Approximate: perspective camera",104),
        new(22,"Release waiting sequence",48,[S("Sequence name",12),I("Sequence cache",44,true)]),
        new(23,"Stop sequence",48,[S("Sequence name",12),I("Sequence cache",44,true)]),
        new(24,"Launch child animation",80,[S("Animation name",12,20),I("Runtime state",32,true),N("Bound node",44,1,true),new("Launch flags",46,AnimationFieldKind.Short,2),new("Entry cache",48,AnimationFieldKind.Short,2,ReadOnly:true),N("Runtime slot",50,7,true),N("Reference node",52,1,true),V("Position",56),V("Rotation",68)]),
        new(25,"Stop named animation",48,[S("Animation name",12),I("Entry cache",44,true)]),
        new(26,"Reset / clean up named animation",48,[S("Animation name",12),I("Entry cache",44,true)]),
        new(27,"Request animation finish",48,[S("Animation name",12),I("Entry cache",44,true)]),
        new(28,"World fog",80,[B("Fields",44),I("Enabled",48),V("Fog RGB",52),F("Altitude min",64),F("Altitude max",68),F("Range start",72),F("Range end",76)],"Approximate: preview fog"),
        new(30,"Loop sequence",20,[B("Stop mode",12,"1: iteration count (65535 is infinite); 2: elapsed time (negative is infinite)"),I("Count / time bits",16)]),
        new(31,"If condition",24,[B("Condition",12,"1: random; 2: distance; 4: effects level; 8/16: collision"),N("Target node",16),F("Threshold / integer bits",20)],"Engine-based; collision uses explicit preview override"),
        new(32,"Else",12,[]),
        new(33,"Else if",24,[B("Condition"),N("Target node",16),F("Threshold / integer bits",20)]),
        new(34,"End if",12,[]),
        new(35,"Game callback",16,[I("Parameter",12)],"Not executed: game callback; shown in event trace"),
        new(36,"Screen color",64,[V("Red: start/end/rate",12),V("Green: start/end/rate",24),V("Alpha: start/end/rate",36),V("Blue: start/end/rate",48),F("Duration (s)",60)],"Approximate: viewport overlay",60),
        new(37,"Screen wave overlay",112,[B("Flags / anchor",12),V("World anchor",16),V("Center X: start/end/rate",28),V("Center Y: start/end/rate",40),F("Near radius (world)",52),F("Far radius (world)",56),V("Radius (pixels): start/end/rate",60),V("Extent: start/end/rate",72),V("Frequency: start/end/rate",84),V("Phase: start/end/rate",96),F("Duration (s)",108)],"Approximate: screen-wave visualization",108),
        new(38,"Game text message",16,[I("Text ID",12)],"Not executed: game message; shown in event trace"),
        new(39,"Marker",16,[I("Marker payload",12,true)]),
        new(40,"Marker (extended)",12,[])
    ];
    public static AnimationEventSpec? Find(int type) => Events.FirstOrDefault(e => e.Type == type);
    public static AnimationEvent Create(byte type)
    {
        var spec = Find(type) ?? throw new InvalidDataException("Unknown event type.");
        AnimationEvent ev = new(new byte[spec.Size]); ev.Bytes[0] = type; ev.StartMode = 1; ev.SetInt(4, spec.Size);
        if (spec.DurationOffset >= 0) ev.SetFloat(spec.DurationOffset, 1);
        if (type == 6) ev.SetInt(12, 1);
        if (type == 8) ev.SetVector(12, Vector3.One);
        if (type == 11) { ev.SetInt(12, 1); ev.SetVector(104, Vector3.One); ev.SetVector(116, Vector3.One); }
        if (type == 12) ev = ev.WithKeyframes([AnimationKeyframe.Create()]);
        if (type == 13) { ev.SetShort(14, 1); ev.SetFloat(16, 1); }
        if (type == 14) { ev.SetFloat(20, 1); ev.SetFloat(28, -1); }
        if (type == 30) { ev.SetInt(12, 1); ev.SetInt(16, 1); }
        if (type is 22 or 23 or 25 or 26 or 27) ev.SetInt(44, -1);
        return ev;
    }
}
