# Verification

Use the pinned .NET 10 SDK. Put transient reports and game-derived verification exports in an OS temporary directory. Historical commands below use ignored `artifacts/`; remove their generated results before delivery, when that folder must contain only the current portable folder and ZIP.

```powershell
dotnet build Recoil.Zbd.slnx -c Release
dotnet test --solution Recoil.Zbd.slnx -c Release
dotnet run --project tools/Recoil.Zbd.Verify -- zbd_1998 zbd_1999 > artifacts/corpus.json
dotnet run --project tools/Recoil.Zbd.ExportCheck -- artifacts/export-check zbd_1998 zbd_1999
dotnet run --project tools/Recoil.Zbd.PreviewCheck -- artifacts/preview-check zbd_1998 zbd_1999
dotnet run --project tools/Recoil.Zbd.PreviewCheck -- --overview artifacts/preview-check
dotnet run --project tools/Recoil.Zbd.PreviewCheck -- --lifecycle zbd_1999
dotnet run --project tools/Recoil.Zbd.PreviewCheck -- --texture-dpi zbd_1999/image.zbd
dotnet run --project tools/Recoil.Zbd.PreviewCheck -- --scene-controls zbd_1999
dotnet run --project tools/Recoil.Zbd.PreviewCheck -- --scene-depth zbd_1999
dotnet run --project tools/Recoil.Zbd.PreviewCheck -- --render-stability zbd_1999
dotnet run --project tools/Recoil.Zbd.PreviewCheck -- --render-stability zbd_1998
dotnet run --project tools/Recoil.Zbd.PreviewCheck -- --alpha zbd_1999
dotnet run --project tools/Recoil.Zbd.PreviewCheck -- --lod zbd_1999
dotnet run --project tools/Recoil.Zbd.AnimationCheck -- zbd_1998 zbd_1999 > artifacts/animation-seek.log
dotnet run --project tools/Recoil.Zbd.AnimationCheck -- --all zbd_1998 zbd_1999 > artifacts/animation-all.log
dotnet run --project tools/Recoil.Zbd.PreviewCheck -- --animation zbd_1999
dotnet run --project tools/Recoil.Zbd.PreviewCheck -- --animation zbd_1998
dotnet run --project tools/Recoil.Zbd.PreviewCheck -- --animation-layout zbd_1999
dotnet run --project tools/Recoil.Zbd.AnimationCheck -- --camera zbd_1998 zbd_1999
dotnet run --project tools/Recoil.Zbd.PreviewCheck -- --camera-follow zbd_1999
dotnet run --project tools/Recoil.Zbd.PreviewCheck -- --camera-follow zbd_1998
```

The preview runner opens and closes its own application window. It exercises application startup, indexing, tab/document binding, all 19 world previews, and representative texture/audio/script/animation/ZRD layouts. It saves Direct3D render artifacts and WPF layout artifacts. These are integration checks, not a replacement for testing pointer gestures, file dialogs, or multi-monitor DPI changes.

The camera corpus mode checks m1/start_single_player's camera1 identity, authored descent and changing direction, 60-degree horizontal FOV, deterministic rewind and unchanged animation bytes/source hashes. The camera-follow WPF mode verifies immediate Follow camera toggling while paused, free navigation when unchecked, playback/seek following, world-space eye/direction/FOV, upright viewing and Preview status identity. It captures the mission-context view at 0.1, 5, 10, 15 and 20 seconds into a temporary directory and restores user settings. Unit tests cover independent position/rotation channels, parent transforms, keyframes, cached camera matrices, radian FOV animation and invalid FOV input.

The animation-layout check opens `m1/destroy_the_gen` in an owned offscreen window and saves temporary wide, Properties-open, light/dark, expanded/trace and narrow-details captures. It checks all five vertical resize grips and keyboard steps, minimum/maximum heights, internal list/text scrolling and wheel handoff, height persistence through collapse/expand, settings reload and asset replacement, and Reset layout. The dispatched-events graphic must remain below the player, outside the text-only Event trace panel, and fit the allocated editor area even in short/narrow windows. Headers and sequence context, graphic/text playhead updates and Fit trace are checked. Expansion/resizing must retain the current frame and event selection; Space from Assets, native control keys and frame stepping are exercised without audible playback. Original settings are restored and the source animation file hash must be unchanged.

The animation corpus runner checks byte-identical no-op serialization and reparsing for every animation pack. Its normal mode compares two-second previews with a backward seek and replay for 149 representative animations and calculates their durations without altering playback time; `--all` starts every nonempty entry for 0.1 second and checks finite poses. Neither mode establishes long-running or original-game equivalence. Reports include finite/looping/open-ended duration classifications and approximation/unresolved-reference diagnostics.

Animation player checks also route Space through the focused Assets row, verify loading intent and cancellation on reselection, protect native Space behavior in editing controls, and check single-frame steps, range clamping, custom/Auto ranges, and restart at the endpoint. They measure the transport at a 250-logical-pixel center width and save dark/light layout renders. Unit fixtures include one-second/60-frame motion, concurrent sequences, relative delays, keyframes, cleanup, finite/infinite loops, external stop sequences, waiting/unsupported events, sound tails, snapshot isolation and cancellation.

The animation WPF mode exercises isolated and mission-context rendering, quarter-speed playback, scrubbing, dirty/undo/redo behavior, Save As reopening, cleanup-phase isolation, and resource disposal. It rejects invisible representative geometry, verifies that transparent smoke contributes actual pixels, and checks that all four red sparks turn downward after launch. In addition to a zero-volume device check, it measures actual WASAPI output from **its own process only** for `vtol_destruction1`: default unmuted playback, unmuting after the sample events, playback after seeking, and pause/resume. Mute and paused scrubbing must stay silent. This part plays brief sounds at 15% volume; it does not record the microphone or other applications. Close/reload/root cancellation paths use the same view-model guard with deterministic responses; they do not automate native save dialogs. Its generated ZBDs and screenshots stay under `artifacts/animation-preview/1998/` or `1999/`; it restores user settings on completion. Run UI modes sequentially to avoid shared-settings interference.

The lifecycle mode checks automatic cancellation when replacing the root, duplicate tabs, rapid selection, an individual model and its view controls, and external-change/reload behavior. Manual indexing cancellation was removed in 0.2.4. The animation check also verifies that idle Escape and the inactive cancel handler leave animation seeking usable. The lifecycle runner's modified file is a copy under `artifacts/`; original sources remain unchanged.

The scene-controls mode verifies right-drag rotation and identical middle/Shift+right pan bindings, pan translation, texture toggling, and repeated orbit/Fly zoom toward a real map surface from a stale target above it. It also renders a constant RGB (40,80,120) texture through a scene material and compares the resulting pixel channels. Artifacts: `scene-controls-close.png` and `scene-color-check.png`. Camera checks call the same viewport navigation methods as the controls, with inertia disabled for deterministic distances; they do not simulate physical mouse dragging.

The scene-depth mode renders the whole world at 1×, 2×, 4×, and 8× overview distance, then checks two closely spaced opaque instanced surfaces at the same scene scale. The front red surface must occlude the rear green surface in both draw orders at every distance. It saves current-size Direct3D frames in the OS temporary `zbd-scene-depth-preview/` folder. This check reproduced the fixed tiny-near-plane regression at 2× before adaptive clipping was added; rerun the scene-controls and lifecycle checks alongside it to cover close navigation and isolated models.

The render-stability mode owns an offscreen WPF window and reads the existing GPU back buffer without requesting a redraw or resize. A world-coordinate fixture places red/green opaque surfaces only 0.001 units apart, 200 units from a camera inside the aggregate scene bounds. It checks both draw orders, static instancing and animation transforms during zoom inertia and after 2/10 seconds idle. On 0.2.7 the rear green surface incorrectly won; geometry-aware clipping restores the red foreground. Repeated sized captures must leave viewport dimensions and camera position unchanged at the current monitor DPI. It exercises ±89° pitch limits, no roll, Orbit/Fly anchors, native rotation inertia, a view-cube-style top transition and authored-camera following. Real-data cases cover m1/start_single_player at frame zero with Mission level off/on, paused after one second, Whole world, and sandbag model 384. Idle pixels and mesh identities must remain unchanged; PNGs go to a timestamped OS temporary folder. This verifies the captured cases, not every GPU/driver or every possible camera angle. Physical pointer gesture feel still needs user review.

The alpha mode tests the application's actual material pass with alpha-zero foreground texels followed by rear geometry, then verifies the expected pixel value for two half-transparent layers. The LOD mode uses the real world/model picker and compares rendered triangle totals with the selected variant, verifies lower-detail geometry changes, and restores highest detail. Animation checks include `fire_bft`'s 12-map/10-fps runtime cycle, changed rendered pixels, exact backward-seek image reproduction, and `lock`'s LOD switching with and without mission context. Core fixtures cover per-object LOD bands, overlapping fade ranges, clamping, model variant groups, animation state preservation, color keys, scoped script includes, loop/clamp/reverse cycles and variant resets.

The texture DPI mode selects a 640 × 480 texture, measures its physical screen bounds at 50%, 100%, and 200% zoom, checks Fit and the 1:1 button, and verifies that switching textures restores 100%. It saves `artifacts/texture-dpi-preview.png` at the current display DPI. The preview runner uses the same PerMonitorV2 manifest as the desktop app. Run this check at each desired Windows scaling setting; moving the app between differently scaled monitors still requires a manual check. A fixed 96-DPI render cannot establish physical 1:1 sizing on a scaled display.

Independent export consumers require development-only Python dependencies:

```powershell
python -m venv artifacts/check-venv
artifacts/check-venv/Scripts/python.exe -m pip install -r tools/requirements-check.txt
artifacts/check-venv/Scripts/python.exe tools/check_exports.py artifacts/export-check
```

`check_exports.py` locates the original texture records independently, uses self-contained little-endian RGB565 bit expansion, compares every RGBA byte of each sampled PNG through Pillow, reads WAVs with Python's wave module, and loads OBJ/MTL through trimesh. It does not import the archived CLI. Embedded palette decoding has a synthetic unit fixture; the first/middle/last corpus samples currently cover direct color, shared palettes, and alpha.

To test audio device initialization without audible output, pass one exported WAV to `PreviewCheck --audio <file.wav>`. This plays briefly at zero volume, then stops. It does not establish audible output quality.

The corpus runner decodes all textures, validates all archive ZRDs and WAVs, and hashes every source before and after. Compare `results[].sha256` across runs for preservation over a longer session. Parse success does not imply byte-identical repacking or exact game rendering.

Release packaging: `./tools/publish.ps1`. The ZIP root must contain exactly `zStudio.exe` and `dependencies/`, with the runtime, application libraries, documentation and license notices inside dependencies. Packaging stages a fresh build, validates paths below the repository's `artifacts/`, and keeps the previous output in a `.previous-<id>` sibling before replacing it. A custom `-Output` must also be below `artifacts/`. `verify-package.ps1` checks versions, the relative apphost binding and every ZIP file by SHA-256. See [releasing.md](releasing.md) for private review, CI and tagged release publication.

To verify packaging, extract the ZIP into a different directory containing spaces, start `zStudio.exe` from an unrelated working directory, and pass a dataset file path containing spaces. Disable installed-runtime discovery in that process's environment and confirm `hostfxr.dll`, `hostpolicy.dll`, `coreclr.dll` and WPF native modules load from the extracted dependencies folder. The native executable's six RT_ICON resources must match the six source ICO frames. These checks passed for the previous application name in 0.2.2. The app remains unsigned. A separate Windows 11 machine without Visual Studio/.NET is still needed for independent distribution validation.


## Ground grid and collision

`dotnet run --project tools/Recoil.Zbd.AnimationCheck -c Release -- --ground zbd_1999 zbd_1998` verifies VTOL mesh contacts frame by frame at preview heights 0, 40 and 100, impact releases and preloaded samples, finite duration, deterministic rewind, LOD invariance, disabled collision and unchanged source bytes. Add `--ground-enabled` to the regular animation corpus command to opt every representative player into ground collision.

`dotnet run --project tools/Recoil.Zbd.PreviewCheck -c Release -- --ground zbd_1999` (and `zbd_1998`) exercises the actual editor: default line grid, paused/playing/rapid toggles, same playhead, custom range, unchanged camera and clip bounds, mission/horizon reloads including a toggle during loading, narrow layout and the presented idle back buffer. Height checks cover live typing without Enter/blur, absence of the Fluent clear button, quiet empty/incomplete/nonfinite/out-of-range text, restoring valid text on blur, superseded asynchronous values, initial pose offsets, recalculated duration, custom range and playback retention. The layout runner also checks the focused Height field across light/dark themes and narrow layouts without changing single-line editing or native Space behavior. Pixel differences against the grid-disabled frame must show grid lines in each screen quarter from above and in perspective; the top-down plane lies beyond the scene far bound. Toolbar checks require the icons, LOD and every display control in one row, accessible button names and the Map label. PNGs go to `%TEMP%/zbd-ground-*`; original app settings are restored.
