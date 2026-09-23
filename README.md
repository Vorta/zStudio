# zStudio

**Advanced viewer and editor for Zipper Interactive's ZBD files.**

zStudio is a native Windows desktop application built with C# 14, .NET 10, WPF Fluent and Direct3D 11. Browse game archives, inspect and export assets, preview assembled worlds, and edit supported animation programs. The current version is **0.2.17**.

[Download releases](https://github.com/Vorta/zStudio/releases) · [Report a bug or request a feature](https://github.com/Vorta/zStudio/issues/new/choose) · [Contribute](CONTRIBUTING.md) · [Changelog](CHANGELOG.md)

The repository is initially private for owner review. Repository and release links require access until Vorta makes the repository public.

## Compatible games

| Game | Status |
| --- | --- |
| **Recoil** | Supported for the format versions tested in the 1998 and 1999 datasets. See the capabilities and limits below. |
| Other Zipper Interactive titles | Planned; compatibility has not yet been established. |

Game files are supplied by the user and are not included. Recognizing an archive does not imply that every editing operation is supported or that edited files have been verified in the original game.

The version-28 animation editor supports event and sequence editing, deterministic playback and seeking, preloaded audio, undo/redo and verified Save As. Its preview includes mission starting layouts, authored camera following, texture cycles, transparency, LOD selection and an adjustable-height ground grid for falling debris. Other formats provide browsing, inspection, previews and standard exports. See [the animation editor guide](docs/animation-editor.md) for controls and preview limitations.

## Run

Download `zStudio-0.2.17-win-x64.zip` from [GitHub Releases](https://github.com/Vorta/zStudio/releases), extract it, and run **zStudio.exe**. Keep the adjacent **dependencies** folder with it. Releases include a SHA-256 checksum. The self-contained Windows 11 x64 build does not require Python, Visual Studio, or a separate .NET installation. A Direct3D 11-capable graphics device is needed for 3D previews.

```text
zStudio.exe
dependencies/
```

The dependencies folder contains the application libraries, bundled .NET runtime, documentation, and license notices. The executable and app window use the supplied Studio icon.

Choose **Open ZBD folder** and select the folder containing `image.zbd` and the mission directories, such as `zbd_1999`. Double-click a file to open a tab. Filter its assets on the left, or use the top-right search across the selected root. Folder scanning includes unknown files, which can be inspected as raw bytes.

## Available tools

| Content | Browse and inspect | Export |
| --- | --- | --- |
| Texture packs | Thumbnails, enlarged preview, zoom/pan, channel views, pixel values, palettes and headers | PNG with alpha, JSON |
| Sound archives | Member list, PCM waveform, play/pause/stop, seeking, cue points | Original WAV/member bytes, JSON |
| ZRD data | Typed nested trees, values and raw float bits | Original bytes and JSON |
| Prepared scripts | Reconstructed script text and instruction data | Text and JSON |
| Animation/effects | Edit events, sequences, references and keyframes; scrub motion/effects/audio previews in isolation or mission context | Verified new ZBD with Save As; edited JSON |
| GameZ | Individual models, assembled static worlds, scene tree, materials, texture references and node properties | OBJ/MTL with PNG textures, JSON |

The workspace uses resizable file/asset/preview/property panes and document tabs. Related references navigate to matching assets. Files changed externally get a reload banner. Settings retain theme, pane widths, window size, and recent roots.

Textures open at 1:1 (100%) with the scroll position reset. At 1:1, each texture pixel occupies one physical screen pixel, regardless of Windows display scaling. Texture controls: wheel to zoom, left- or middle-button drag to pan, Fit and 1:1, RGBA/RGB/individual channels. Drag the texture or the surrounding preview background when the image extends beyond the viewport; scrollbars remain available.

3D textures display their original colors without added scene lighting. Right-button drag orbits, middle-button drag pans (Shift + right-button drag also works), and the wheel zooms toward the surface under the pointer. Fly mode looks around from the camera position and moves forward/back with the wheel, using smaller steps near surfaces. Frame all resets the camera. Select a node in the view or Scene tree to inspect or isolate it. Wireframe, textures, bounds, fly mode, LOD choices, and texture variants are available. **LOD 0 · Highest detail** is the default. Higher numbers select lower-detail distance bands separately for each object; objects with fewer variants keep their last available level. A model belonging to an LOD group previews that group's selected variant. The animation toolbar has the same picker, also applied to its optional mission level. The camera-following horizon is optional in the preview; OBJ world exports include it.

All 3D cameras stay upright relative to world +Y, with free horizontal rotation and vertical tilt limited to ±89°. Orbit preserves its target and Fly preserves its position when rotating. View-cube transitions, inertia and authored-camera previews also stay upright; saved animation camera data is unchanged. This does not add terrain collision or lock the camera's height. Depth clipping follows the geometry in view, independently of the small navigation clearance, so entering a map's bounds does not destroy depth precision or prevent close inspection.

Animation **Grid** sits beside LOD and starts checked. **Height** beside Grid raises the selected animation by the entered number of game units; it defaults to 0. Valid values apply as you type, preserving the playhead and prior playback state while recalculating contact and duration. Empty, unfinished or invalid input keeps the last valid height without errors; leaving the field restores that number. Height is preview-only and does not move the map or save into the animation. It shows a world Y=0 line grid and enables flat-ground debris collision; uncheck to disable both. Landed meshes stay above the plane, impact triggers stop trails, and collision-driven durations recalculate. This is a preview plane, not mission-terrain or camera collision.

Animation controls: select an asset and press **Space** to play/pause. Play/pause, stop and frame-step icons sit beside the seek bar, with a current/total frame readout. Finite preview duration is calculated automatically; looping and open-ended programs use a labeled, adjustable preview range. Speed, Replay preview, Mute and volume sit below the transport. **Preview options** contains scene bindings, seed, conditions and custom range/Auto. Edit typed event fields on the right; add, duplicate, delete and reorder events or sequences. Runtime and reset/stop phases preview separately. Audio starts unmuted at 50% volume. **Ctrl+S** saves a new animation pack; **Ctrl+Z / Ctrl+Y** undo/redo edits. See [the animation editor guide and engine evidence](docs/animation-editor.md).

Shortcuts: **Ctrl+O** opens a folder, **Ctrl+E** exports selected records, **Ctrl+W** closes a tab, and **F5** reloads. **Escape** or **Tools → Cancel export or validation** cancels only an active export or validation; the menu item is disabled otherwise. Indexing runs to completion unless the root changes or the application closes. Export creates a new folder outside the source tree; existing exports and game files are not overwritten. A canceled export may leave completed files in that new folder.

The **Dispatched events** graphic sits below the animation player. The right-side **Details** sections are collapsible and individually resizable: drag the bottom grip to change a section's height, or focus the grip and use Up/Down (Shift for larger steps). Section heights and expansion choices are remembered across animations and launches. **Status** and **Event trace** hold scrollable text; the **Events** body identifies its selected sequence. **View → Reset layout** restores the defaults.

**Diagnostics** is a session list of file-reading warnings, indexing/open errors, export failures, validation findings and reported operation errors. **Tools → Validate current file** adds any findings there. It is empty when no issues have been reported, and opening another root clears it. Animation-specific resource warnings, preview limitations and event dispatches are shown separately in **Status** and **Event trace**.

## Build and verify

Open `zStudio.slnx` in Visual Studio 2026 with the .NET desktop workload, and select `zStudio.Desktop` as the startup project. SDK and package versions are pinned.

```powershell
dotnet build zStudio.slnx -c Release
dotnet test --solution zStudio.slnx -c Release
./tools/publish.ps1
```

The solution separates binary readers/exporters (`zStudio.Core`), Direct3D preview (`zStudio.Rendering`), WPF/audio (`zStudio.Desktop`), tests, and verification runners. Project folders and files use `zStudio.*`; existing `Recoil.Zbd.*` assembly names and namespaces remain stable for runtime and resource compatibility. Normal builds and unit tests require no game files or Python. Optional corpus and GPU/audio checks use your own game data. See [architecture and format evidence](docs/architecture.md), [verification commands](docs/testing.md), and [current status](docs/status.md).

## Current limits

Animation version 28 supports editing existing entries and Save As to a new file. Other formats remain read-only; texture import/replace and creation of whole animation entries are not implemented. Animation preview approximates physics, beams, lighting, fog, camera parameters, screen effects and audio; game callbacks are trace-only. LOD selection is manual; camera-distance fades are not simulated. Edited animation packs have not yet been tested in the original game. Missing textures are reported; unresolved animation texture cards stay hidden. Six 1999 missions reference some textures absent from their own packs.

Unknown versions remain available for raw inspection. The snapshot reader limits individual files to 512 MiB. JSON exports are intended for inspection and external tools; they are not the Python CLI repack schema. The portable build is unsigned and has not yet been checked on an independent clean Windows machine.

## Contributing and future development

Bug reports, feature requests and pull requests are welcome. Use the [issue forms](https://github.com/Vorta/zStudio/issues/new/choose), and read [CONTRIBUTING.md](CONTRIBUTING.md) before proposing code changes. Include the game/data version, zStudio version and asset record identity in reports. Please do not upload game archives or extracted game assets.

Future work includes additional Zipper Interactive games and broader content editing. A compatibility request should identify the game and format evidence; support remains unconfirmed until tested. [Release and maintenance instructions](docs/releasing.md) describe versioning, CI and the owner's public-launch checklist.

The former Python unpacker/repacker is archived separately from this desktop repository. An optional Python utility remains for independent export checks; it is not needed to build or run zStudio.

## License

zStudio is licensed under the [MIT License](LICENSE), copyright © 2026 Vorta. Game content retains its original ownership. This project is independent of Zipper Interactive and is not an official game tool.

Game data is not included in releases. Dependency notices are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
