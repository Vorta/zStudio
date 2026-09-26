using Recoil.Zbd.Automation;

namespace Recoil.Zbd.Desktop;

public partial class MainWindow
{
    private static StudioParameter AnimationChanges => new("changes", "object", "Animation options; only supplied values change.", true, Properties:
    [
        .. new[] { "map", "grid", "collision", "horizon", "followCamera", "effects", "replay", "mute", "followLog", "autoRange", "fitTrace" }.Select(n => P(n,"boolean",n)),
        P("height","number","World Y offset from -999 to 999."), P("lod","integer","Authored LOD rank."), P("difficulty","string","Mission layout.",false,"Easy","Medium","Hard"),
        P("speed","number","0.25, 0.5, 1, 2 or 4."), P("volume","number","0–1."), P("phase","string","Playback phase.",false,"runtime","cleanup"),
        P("seed","integer","32-bit random seed."), P("condition","integer","0: stored/game-dependent, 1: true, 2: false."),
        P("range","number","Preview range in seconds, 1/60–3600."), P("traceRange","number","Visible dispatch range, 1/60–3600 seconds."),
        P("problemFilter","integer","0 All, 1 File/operation, 2 Preview, 3 Resource, 4 Support."),
        P("worldPath","string","GameZ archive path."), P("root","integer","Scene root node index."),
        P("activationOrigin","string","Comma-separated XYZ, or blank for default."), P("activationTarget","string","Comma-separated XYZ, or blank for default.")
    ]);
    private static StudioParameter SceneChanges => new("changes", "object", "Static model/world options.", true, Properties:
    [
        .. new[] { "textures", "wireframe", "bounds", "horizon" }.Select(n => P(n,"boolean",n)),
        P("lod","integer","Authored LOD rank."), P("difficulty","string","Mission layout.",false,"Easy","Medium","Hard"), P("texturePack","string","Available texture pack path, or empty for automatic.")
    ]);
    private static StudioParameter WorkspaceChanges => new("changes", "object", "Optional presentation preferences.", Properties:
    [
        .. new[] { "resetLayout", "navigator", "inspector", "tools", "toolsMaximized", "backupOnSave" }.Select(n=>P(n,"boolean",n)),
        P("theme","string","Theme.",false,"System","Light","Dark"), P("density","string","Density.",false,"Compact","Comfortable"),
        P("preset","string","Workspace preset.",false,"Inspect","Edit","Debug","Focus preview"),
        .. new[] { "navigatorWidth", "inspectorWidth", "toolsHeight" }.Select(n=>P(n,"number","Preferred dimension in DIP; clamped to supported layout limits.")),
        .. new[] { "navigatorTab", "inspectorTab", "toolsTab" }.Select(n=>P(n,"integer","Zero-based visible tab index."))
    ]);
}
