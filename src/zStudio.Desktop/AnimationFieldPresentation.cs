using Recoil.Zbd.Core.Animation;

namespace Recoil.Zbd.Desktop;

/// <summary>Desktop presentation keyed by catalog type/offset. Unknown fields have an explicit fallback.</summary>
internal static class AnimationFieldPresentation
{
    public static string Category(byte type) => type switch
    {
        1 or 2 or 3 or 4 or 5 => "Sound, lights and effects",
        >= 6 and <= 17 => "Transform and hierarchy",
        18 or 20 or 21 or 28 or 36 or 37 => "Camera and presentation",
        19 or >= 22 and <= 34 => "Launch and control flow",
        _ => "Game callbacks and markers"
    };
    public static string Group(byte type, AnimationField field)
    {
        int o = field.Offset;
        if (field.ReadOnly) return "Serialized state / provenance";
        if (type == 10) return o switch
        {
            12 or 16 => "Target and modes", >= 32 and <= 60 => "Launch ranges",
            24 or 64 or 76 or 124 or 128 => "Initial motion / gravity",
            136 or 148 => "Spin", 28 or 172 or 184 => "Scale and morph",
            208 or 242 or 244 => "Impact actions", 248 => "Completion", _ => "Additional stored fields"
        };
        if (type == 11) return o switch { 12 or 16 => "Target and channels", >= 20 and <= 28 => "Morph", >= 32 and <= 56 => "Position", >= 68 and <= 92 => "Rotation · engine radians", >= 104 and <= 128 => "Scale", 140 => "Duration", _ => "Additional stored fields" };
        if (field.ReferenceTable >= 0) return "Targets and references";
        if (field.Kind == AnimationFieldKind.Flags) return "Modes and fields";
        if (AnimationCatalog.Find(type)?.DurationOffset == o) return "Duration";
        return type switch
        {
            1 or 2 or 3 => "Sound / effect placement", 4 => o < 72 ? "Light configuration" : o < 96 ? "Placement and rotation" : "Ranges and color",
            5 => o < 48 ? "Light" : o < 72 ? "Ranges · start and rate" : "Color · start and rate",
            7 => "Position · game units", 8 => "Scale", 9 => "Rotation · engine radians",
            13 or 14 => "Inherited alpha override", 18 => o < 48 ? "Endpoints" : "Fractions and length",
            19 or 24 => o < 56 ? "Launch settings" : "Placement", 20 => o < 40 ? "Lens and clipping · radians" : "Viewport",
            21 => "Parameters · start / end / rate", 22 or 23 => "Sequence control", 25 or 26 or 27 => "Animation control",
            28 => o < 64 ? "Fog color and state" : "Altitude and range", 30 => "Loop termination", 31 or 33 => "Condition",
            35 => "Game callback · not executed", 36 => "Color channels · start / end / rate", 37 => "Wave parameters",
            38 => "Game message · not executed", _ => "Additional stored fields"
        };
    }
    public static (uint Bit, string Label)[] Flags(byte type, int offset) => (type, offset) switch
    {
        (10, 12) => [(1,"Gravity / collision"),(2,"Inherited velocity (unavailable)"),(4,"Velocity mode"),(8,"Random launch"),(0x20,"Spin"),(0x100,"Scale"),(0x200,"Morph"),(0x400,"Timed completion"),(0x800,"Release on collision"),(0x1000,"Impact sound"),(0x2000,"Transposed world gravity")],
        (11, 12) => [(1,"Position"),(2,"Rotation"),(4,"Scale"),(8,"Morph")],
        (7, 12) => [(1,"Add to current position")], (17, 12) => [(1,"Reset cycle")],
        (30, 12) => [(1,"Iteration count"),(2,"Elapsed time")],
        (31 or 33, 12) => [(1,"Random"),(2,"Squared distance"),(4,"Effects level"),(8,"Collision bit 8"),(16,"Collision bit 16")],
        _ => []
    };
    public static uint RelevanceBit(byte type, string group) => (uint)(type == 10 ? group switch
    {
        "Launch ranges" => 8, "Spin" => 0x20, "Scale and morph" => 0x300, "Impact actions" => 0x1800, "Completion" => 0x400, _ => 0
    } : type == 11 ? group switch { "Morph" => 8, "Position" => 1, "Rotation · engine radians" => 2, "Scale" => 4, _ => 0 } : 0);
    public static string[] Components(byte type, AnimationField field) => field.Name.Contains("start/end/rate", StringComparison.Ordinal) ? ["Start", "End", "Rate"] : type is 4 or 5 or 28 && field.Name.Contains("RGB", StringComparison.Ordinal) || type == 5 && field.Offset is 72 or 84 or 96 ? ["R", "G", "B"] : ["X", "Y", "Z"];
}
