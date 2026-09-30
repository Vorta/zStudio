namespace Recoil.Zbd.Core.Animation;

public static partial class AnimationCatalog
{
    private static readonly Lazy<IReadOnlyList<AnimationEventSpec>> mw3Events = new(() =>
    {
        var events = Events.Where(e => e.Type is not (3 or 4 or 10 or 12 or 37 or 38 or 39 or 40)).Select(e =>
            e.Support.StartsWith("Engine-based", StringComparison.Ordinal) ? e with { Support = "Approximate: shared transform and timing preview; MW3 gameplay is not simulated" } : e).ToList();
        events.Add(new(4, "Light", 132, [S("Light name",12),N("Light reference",44,2),B("Fields",48),I("Active",52),I("Mode",56),
            I("Directional",60),I("Saturated",64),I("Subdivide",68),I("Static",72),N("Basis node",76),V("Position / basis offset",80),
            V("Rotation",92),F("Inner range",104),F("Outer range",108),V("Color RGB",112),F("Ambient",124),F("Diffuse",128)], "Approximate: preview lighting"));
        var motion = Events.Single(e => e.Type == 10);
        events.Add(motion with { Size = 332, DurationOffset = 328,
            Fields = [..motion.Fields.Where(f => f.Offset != 248), S("Water collision sequence",248),new("Water sequence cache",280,AnimationFieldKind.Short,2,ReadOnly:true),
                N("Water collision sample",282,4,true),F("Water volume scale",284),S("Lava collision sequence",288),new("Lava sequence cache",320,AnimationFieldKind.Short,2,ReadOnly:true),
                N("Lava collision sample",322,4,true),F("Lava volume scale",324),F("Duration (s)",328)],
            Support = "Approximate: procedural motion with flat-ground contact; water, lava and gameplay forces are not simulated" });
        events.Add(new(12, "Transform keyframes", 36, [N("Target node",12),I("Frame count",16,true),F("Reserved",20,true),F("Runtime local time",24,true),I("Runtime cursor",28,true),I("Runtime lookahead",32,true)],
            "Approximate: authored base/rate channels; stored spline coefficients are retained"));
        events.Add(new(41, "Detonate weapon", 36, [S("Weapon",12,10),N("Position node",22,1,true),V("Offset",24)], "Not executed: weapon gameplay; shown in event trace"));
        events.Add(new(42, "Puffer state", 592, [S("Puffer name",12),I("Puffer reference",44),B("Fields",48)], "Not executed: particle simulation; remaining operands preserved"));
        return events.OrderBy(e => e.Type).ToArray();
    });
    public static IReadOnlyList<AnimationEventSpec> ForVersion(uint version) => version switch
    { 28 => Events, 39 => mw3Events.Value, _ => throw new InvalidDataException("Unsupported animation version.") };
}
