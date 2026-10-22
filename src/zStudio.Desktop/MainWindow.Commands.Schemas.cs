using zStudio.Automation;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private static StudioParameter AnimationChanges => new("changes", "object", "Animation options; only supplied values change.", true, Properties:
    [
        .. new[] { "map", "faceOrientation", "grid", "collision", "horizon", "followCamera", "effects", "replay", "mute", "followLog", "autoRange", "fitTrace" }.Select(n => P(n,"boolean",n)),
        P("height","number","World Y offset from -999 to 999."), P("lod","integer","Authored LOD rank."), P("difficulty","string","Mission layout.",false,"Easy","Medium","Hard"),
        P("speed","number","0.25, 0.5, 1, 2 or 4."), P("volume","number","0–1."), P("phase","string","Playback phase.",false,"runtime","cleanup"),
        P("seed","integer","32-bit random seed."), P("condition","integer","0: stored/game-dependent, 1: true, 2: false."),
        P("range","number","Preview range in seconds, 1/60–3600."), P("traceRange","number","Visible dispatch range, 1/60–3600 seconds."),
        P("problemFilter","integer","0 All, 1 File/operation, 2 Preview, 3 Resource, 4 Support."),
        P("worldPath","string","GameZ archive path."), P("root","integer","Scene root node index."),
        P("activationOrigin","string","Comma-separated XYZ, or blank for default."), P("activationTarget","string","Comma-separated XYZ, or blank for default.")
    ]);
    private static StudioParameter SceneChanges => new("changes", "object", "Static model/world options. Validate the entire batch before applying changes; publish highlights and AI options only after requested refreshes succeed.", true, Properties:
    [
        .. new[] { "textures", "faceOrientation", "wireframe", "bounds", "horizon" }.Select(n => P(n,"boolean",n)),
        P("aiNodes", "boolean", "Whole world: show authored AI nodes and directed links."),
        P("pickupsByType", "boolean", "Source maps: Group by type, a row per catalog type inside the Pickups section's Weapons, Ammo and Other (for the session; off at first)."),
        P("bftType", "string", "Source maps: the player tank's master type as the view draws it (preview only, for the open map; tracked when a map opens). A type this map's data has no transition to is refused.", false, "tracked", "amphibious", "hover", "sub"),
        P("bftPrimary", "string", "Source maps: the player tank's primary weapon (preview only; RFPG when a map opens).", false, [.. zStudio.Core.PlayerTankWeapon.Primaries.Select(w => w.Id)]),
        P("bftSecondary", "string", "Source maps: the player tank's secondary weapon, by its pickup catalog name without _WEAPON (preview only; HEMORTAR when a map opens). Beam and fire weapons are refused in sub; choosing sub switches away from one as the game does.", false, [.. zStudio.Core.PlayerTankWeapon.Secondaries.Select(w => w.Id)]),
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
        .. new[] { "inspectorTab", "toolsTab" }.Select(n=>P(n,"integer","Zero-based visible tab index."))
    ]);
}
