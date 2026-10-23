namespace zStudio.Desktop;

/// <summary>Desktop presentation keyed by catalog type/offset. Unknown fields have an explicit fallback.</summary>
internal static class AnimationFieldPresentation
{
    public static (uint Bit, string Label)[] Flags(byte type, int offset) => (type, offset) switch
    {
        (10, 12) => [(1,"Gravity / collision"),(2,"Inherited velocity (unavailable)"),(4,"Velocity mode"),(8,"Random launch"),(0x20,"Spin"),(0x100,"Scale"),(0x200,"Morph"),(0x400,"Timed completion"),(0x800,"Release on collision"),(0x1000,"Impact sound"),(0x2000,"Transposed world gravity")],
        (11, 12) => [(1,"Position"),(2,"Rotation"),(4,"Scale"),(8,"Morph")],
        (7, 12) => [(1,"Add to current position")], (17, 12) => [(1,"Reset cycle")],
        (30, 12) => [(1,"Iteration count"),(2,"Elapsed time")],
        (31 or 33, 12) => [(1,"Random"),(2,"Squared distance"),(4,"Effects level"),(8,"Collision bit 8"),(16,"Collision bit 16")],
        _ => []
    };
}
