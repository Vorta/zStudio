using zStudio.Automation;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private static StudioParameter AnimationChanges => new("changes", "object", "Animation options; only supplied values change. A compiled animation takes the view's presentation (lod, difficulty, faceOrientation, map, grid, height, collision, horizon, effects, followCamera) and the transport (speed, replay, mute, volume); what the run is made of is animation_scenario's (the keys marked Moved are refused with a pointer to it).", true, Properties:
    [
        .. new[] { "map", "faceOrientation", "grid", "horizon", "followCamera", "effects", "replay", "mute" }.Select(n => P(n,"boolean",n)),
        P("collision", "boolean", "Flat-plane collision in isolated preview; refused in source-map preview, which always uses standable terrain."),
        P("height","number","World Y offset from -999 to 999 (a compiled animation's: the scenario's Height)."), P("lod","integer","Authored LOD rank."), P("difficulty","string","Mission layout.",false,"Easy","Medium","Hard"),
        P("speed","number","0.25, 0.5, 1 or 2."), P("volume","number","0–1."),
        new("playbackRange","array","Animation mode in a source map: the playback range [start, end] in seconds (the transport's range flyout). Moved for a compiled animation: animation_scenario inputs {range}.", Items: new("", "number", "Seconds.", NumberMinimum: 0, NumberMaximum: 3600), MinItems: 2, MaxItems: 2),
        .. new[] { "seed", "condition", "root" }.Select(n => P(n, "integer", "Moved: refused with a pointer to animation_scenario.")),
        P("range", "number", "Moved: refused with a pointer to animation_scenario (a compiled animation) or to playbackRange (a source map)."),
        .. new[] { "phase", "worldPath", "activationOrigin", "activationTarget" }.Select(n => P(n, "string", "Moved: refused with a pointer to animation_scenario.")),
        .. new[] { "autoRange", "fitTrace", "followLog" }.Select(n => P(n, "boolean", "Moved: refused with a pointer to animation_scenario or animation_timing.")),
        P("problemFilter","integer","Moved: refused with a pointer to animation_runtime notices."), P("traceRange","number","Moved: refused with a pointer to animation_timing options."),
    ]);
    private static StudioParameter SceneChanges => new("changes", "object", "Static model/world options. Validate the entire batch before applying changes; publish highlights and AI options only after requested refreshes succeed.", true, Properties:
    [
        .. new[] { "textures", "faceOrientation", "wireframe", "bounds", "horizon" }.Select(n => P(n,"boolean",n)),
        P("aiNodes", "boolean", "Whole world: show authored AI nodes and directed links."),
        P("pickupsByType", "boolean", "Source maps: Group by type, a row per catalog type inside the Pickups section's Weapons, Ammo and Other (for the session; off at first)."),
        P("animationsByStart", "boolean", "Source maps: Group by start, the Animations section's definitions in one row per start kind (Mission start, On startup, On damage, Called, Started by the game, No caller found; scene_tree category definitionGroup), the map's and then Common's, instead of by file (for the session; off at first)."),
        P("bftType", "string", "Source maps: the player tank's master type as the view draws it (preview only, for the open map; tracked when a map opens, as the mission starts). A type this map's data has no transition to is refused.", false, "tracked", "amphibious", "hover", "sub"),
        P("bftPrimary", "string", "Source maps: the player tank's primary weapon (preview only; when a map opens, the second listed weapon the mission allows, RFPG in the shipped maps).", false, [.. zStudio.Core.PlayerTankWeapon.Primaries.Select(w => w.Id)]),
        P("bftSecondary", "string", "Source maps: the player tank's secondary weapon, by its pickup catalog name without _WEAPON (preview only; when a map opens, the first listed weapon the mission allows, HEMORTAR in the shipped maps). Beam and fire weapons are refused in sub; choosing sub switches away from one as the game does.", false, [.. zStudio.Core.PlayerTankWeapon.Secondaries.Select(w => w.Id)]),
        P("aiThroughGeometry", "boolean", "Whole world: show AI through scene geometry; false enables depth occlusion."),
        P("aiNetwork", "string", "Whole world: all, or an AI network ID. A specific ID requires aiSnapshot."),
        P("aiSnapshot", "string", "Expected AI graph snapshot from preview_state.ai; required with a specific aiNetwork ID."),
        P("highlight", "string", "Whole world only: 50% colour overlays for yellow non-default soils, green CanModify, red ClipTo, combined CanModify/ClipTo (red wins overlap), object (node) zones (grey for any), polygon zones (each polygon's zone set, striped where it has several), or magenta landmarks (never culled, with everything below a landmark object); none restores normal appearance.", false, "none", "nonDefaultSoils", "canModify", "clipTo", "canModifyAndClipTo", "zones", "polygonZones", "landmarks"),
        new("lod","integer","Authored LOD rank within the active viewer's available range.", Minimum: 0, Maximum: int.MaxValue), P("difficulty","string","Mission layout.",false,"Easy","Medium","Hard"), P("texturePack","string","Available texture pack path, or empty for automatic.")
    ]);
    private static StudioParameter WorkspaceChanges => new("changes", "object", "Optional presentation preferences.", Properties:
    [
        .. new[] { "resetLayout", "navigator", "inspector", "tools", "toolsMaximized" }.Select(n=>P(n,"boolean",n)),
        P("theme","string","Theme.",false,"System","Light","Dark"),
        P("preset","string","Workspace preset.",false,"Inspect","Edit","Debug","Focus preview"),

        P("filesWidth","number","Preferred detached Files width in DIP, clamped to 200–650; automatic layout does not overwrite it."),
        P("inspectionPanelHeight","number","Preferred total expanded inspection-panel height in DIP; default 432, minimum 216. Effective height is limited to 80% of the viewport without overwriting this preference; shorter viewports lower the effective minimum. Does not commit drafts."),
        .. new[] { "navigatorWidth", "inspectorWidth", "toolsHeight" }.Select(n=>P(n,"number","Preferred dimension in DIP; clamped to supported layout limits. Navigator width is the content tabs width in split mode, or the combined tab group width otherwise.")),
        P("navigatorTab","integer","Stable section index: 0 Files (also when detached), 1 Assets, 2 Search, 3 Document scene. Unavailable document sections are rejected."),
        P("toolsTab","integer","Zero-based visible tab index of the bottom tools (Problems, Related, Bytes).")
    ]);
}
