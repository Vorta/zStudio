# Testing

Build and test commands are in [AGENTS.md](../../AGENTS.md#build-test-package). A green suite proves only the cases it exercises; review coverage follows [code-review](code-review.md).

## Environment

- Rendered Desktop and live-preview checks need an interactive Windows desktop session with working Direct3D 9Ex/D3DImage interop; a disconnected session returns `D3DERR_NOTAVAILABLE`. Restore the session; never turn that failure into a skipped or passing preview assertion.
- Window-size fixtures fit the current monitor work area while still checking exact saved and restored sizes.
- Run UI checks serially, with the owner's zStudio closed, and restore the user's settings afterward. Never move or capture the physical mouse except in the explicitly pointer-owning modes below, which restore cursor, foreground window and settings.
- CI runs test modules one at a time (`--max-parallel-test-modules 1`) so Core allocation checks do not compete with the Desktop dispatcher; the Desktop chain keeps its six-minute hang guard and reports its active phase.
- The Desktop suite is one xUnit test (`WindowClosingTests.DiscardAndCancelUseTheRealWindowCloseLifecycle`) that runs every `*Checks.Run()` on a real WPF STA dispatcher; a removed check also needs its call line removed.
- Put transient reports and game-derived exports in the OS temporary folder; `artifacts/` holds only the current portable folder and ZIP.

## Minimal must-have tests

- Keep the smallest suite that protects essential supported behaviour. A test is must-have when it covers a core workflow, data integrity, a required GUI/MCP contract, or a consequential reproduced regression existing tests do not catch; state that reason briefly when adding it. Test counts, hypothetical permutations and coverage percentages are not reasons.
- Prefer extending one existing test and fixture over adding overlapping ones; add a boundary, control or failure case only when it distinguishes a real failure mode.
- Avoid redundant tests, assertions that mirror the implementation, exhaustive combinations and elaborate infrastructure for low-impact behaviour. Documentation, trivial edits and reversible presentation changes get direct verification, not new tests.
- A defective test found in review: decide necessity first. Remove it if it is not must-have (record the removal briefly; do not replace it with another optional test). Repair a must-have test, and track any production defect it revealed separately.
- Never weaken required assertions or delete essential coverage to make CI pass. Missing optional coverage never blocks a review. Apply this to new work and tests met in review, not as a blanket purge.

## Corpus-gated tests

Tests that need data return early unless their variable is set, so the plain suite needs no game data. The retail folders are never written: tests work in temporary copies, and `zbd_1998`/`zbd_1999` are refused as project or export destinations.

| Variable | Value | Enables |
| --- | --- | --- |
| `ZSTUDIO_CORPUS` | absolute path of a RECOIL data folder (holding `interp.zbd`, `zrdr.zbd`, `m1\`); a relative path resolves against the test folder | archive, world, animation, keyframe, AI, resource and source-project corpus tests. `SourceProjectCorpusTests` reconstructs the release, checks every script is an SI script with its DKit messages and no `.zrd` holds animation definitions, exports, compares the archives with the shipped ones without their definitions, rebuilds every shipped animation entry and checks that unpacking the export is refused |
| `ZSTUDIO_MW3_CORPUS` | a MechWarrior 3 `zbd` folder | MechWarrior 3 and AI valve inspection tests |
| `ZSTUDIO_DEMO_CORPUS` | a RECOIL demo's `zbd` folder (July or August 1998, version-13 worlds) | every version-13 world reads without diagnostics and every rebuilt node box gives back the stored corners |
| `ZSTUDIO_SOURCE_PROJECT` | a reconstructed project (read only; `data` and `gamegen` are copied to a temporary folder) | the terrain conversion corpus test without reconstructing first |
| `ZSTUDIO_BLENDER` | Blender 5.2.x executable | `BlenderGraphTests`: installs the packaged extension in a factory-startup background Blender with a fresh user-resource folder, round-trips shared references, flags, zones and geometry, saves and reopens a `.blend` with its baseline cache missing; never touches the user's Blender preferences or windows |
| `ZSTUDIO_BLENDER_HELPER` | the portable `zStudio.exe` (required with `ZSTUDIO_BLENDER`) | the helper the extension calls; run after packaging |
| `ZSTUDIO_BLENDER_SOURCE` | any value | `tools/blender-roundtrip.py` loads the extension from `blender/` instead of the packaged ZIP |
| `ZSTUDIO_INSPECTION_CAPTURE` | an output folder | screenshots from the Desktop inspection checks |

Full local gate (CI downloads Blender 5.2.2 x64 with a pinned SHA-256 for the optional Blender step):

```powershell
$env:ZSTUDIO_CORPUS = 'D:\...\zbd_1999'; $env:ZSTUDIO_MW3_CORPUS = 'D:\...\MechWarrior 3\zbd'
dotnet test --solution zStudio.slnx -c Release
$env:ZSTUDIO_CORPUS = 'D:\...\zbd_1998'
dotnet test --project tests/zStudio.Tests -c Release --no-build --filter "FullyQualifiedName~SourceProjectCorpusTests"
dotnet test --project tests/zStudio.Tests -c Release --no-build --filter "FullyQualifiedName~BlenderGraphTests"   # with ZSTUDIO_BLENDER and ZSTUDIO_BLENDER_HELPER
```

## Tools

`dotnet run --project <tool> -c Release -- <mode> <dataset-root>`. Each mode owns its test window, verifies unchanged source hashes, writes outputs under `%TEMP%`, restores settings and never captures the physical mouse unless stated.

| Tool / mode | Checks |
| --- | --- |
| `tools/zStudio.Verify <roots…>` | decodes every texture, validates every archive ZRD and WAV, hashes every source before and after (JSON to stdout) |
| `tools/zStudio.ExportCheck <out> <roots…>` | export samples for the independent Python checker |
| `tools/zStudio.AnimationCheck <roots…>` | byte-identical no-op serialization and reparse of every animation pack; 2 s previews of representative animations with backward seek and replay, durations without moving playback; `--all` starts every entry for 0.1 s; `--ground-enabled` opts into ground collision; `--camera`, `--ground`, `--mission`, `--difficulty` focused corpus modes |
| `PreviewCheck <out> <roots…>` | application startup, indexing, all world previews, representative texture/audio/script/animation/ZRD layouts; `--overview <out>` summarizes |
| `PreviewCheck --mcp <root>` | its own visible workspace over real MCP: previews, unfinished height drafts, texture capture/export, audio, world camera |
| `PreviewCheck --mcp-stdio <exe> [root]` | packaged connector: windowless handshake and discovery, one GUI for concurrent first use, reconnect, simultaneous clients, shutdown; with a root, the real workspace across all five families. Requires access the user already enabled; refuses an already registered instance |
| `PreviewCheck --mcp-catalog <path>` | regenerates the embedded discovery catalog |
| `--blender-navigation`, `--scene-controls`, `--scene-depth`, `--render-stability`, `--viewport-size`, `--face-orientation`, `--inspection-fixture` | navigation gestures and both projections, framing, depth precision, presented-buffer idle stability, ±89° pitch, view cube |
| `--freecam` | Fly capture, cursor clipping, injected movement, release paths; briefly owns foreground input in its window |
| `--world-highlights`, `--ai-networks`, `--placements`, `--difficulty`, `--mission`, `--lod`, `--alpha` | Whole world overlays, AI networks, starting layouts, difficulty refreshes, LOD bands, alpha passes |
| `--animation`, `--animation-layout`, `--animation-tree`, `--toolbars`, `--properties`, `--camera-follow`, `--ground` | animation preview, Inspector tabs and layout at three sizes and both themes, toolbar order and overflow, Properties window, Follow camera, grid/collision/Height; `--animation` measures WASAPI output of its own process only |
| `--gui-review`, `--header-layout`, `--responsive-files`, `--recent-folders <project>`, `--scene-tree` | whole-application captures through its own HWND, title/menu layout in 18 theme/width/state cases, DWM corners, Files breakpoints, Recent folders |
| `--lifecycle`, `--texture-dpi <image.zbd>`, `--workspace-performance`, `--audio <file.wav>` | cancellation on replacement, physical 1:1 texture sizing at the current DPI, CPU/allocation samples, silent WASAPI initialization |

Independent export consumers (optional Python, never imported by the app):

```powershell
python -m venv $env:TEMP\check-venv
& $env:TEMP\check-venv\Scripts\python.exe -m pip install -r tools/requirements-check.txt
& $env:TEMP\check-venv\Scripts\python.exe tools/check_exports.py <ExportCheck output>
```

## In-game acceptance

- Run on a disposable game installation (for example `D:\RecoilTest`), exporting only into it, through the normal runtime scripts (`mN_zbd.gs`), never the build script that writes `gamez.zbd`. Preview, parsing and reconstruction fixed points are separate acceptance labels from passing in the game. Tools: Process Monitor, VMMap, `typeperf`, PresentMon, optionally the dgVoodoo debug build with DebugView.
- T1 pack choice: dgVoodoo VRAM 8, 16, 64, 256, 1024 MB, 2 and 4 GB; Process Monitor shows the first `rtexture<N>` probed and the one opened. Pass: N follows VRAM; no setting ends on `texturemax` or `texture.zbd`.
- T2 maximum texture size: labelled grids 512² to 4096², plus 1024×128 and 1024×64, at the M1 start with a profile of `maximumDimension` 4096 and no budget. Pass per size: the grid renders; 1024×64 shows the default texture.
- T3 memory tiers: packs of upscaled textures at 32–512 MiB selected through VRAM; log virtual bytes (`typeperf`) and the largest free block (VMMap) over load, play, three restarts, save, load and a mission change. Pass: peak ≤ 1.5 GiB, largest free block ≥ 256 MiB, restarts within 32 MiB, no default textures.
- T4 runtime growth: 50, 100 and 200 craters, kills and effects on the chosen tier. Pass: peak ≤ 1.5 GiB over 30 minutes.
- T5 frame time: PresentMon 60 s at three repeatable views with original, `modern` and top-tier packs. Pass: modern p99 within 10 % of original and ≤ 16.7 ms.
- T6 UV precision: a 1024 grid on two quads with UVs off the 1/256 grid matches the preview within 0.5 texel; cratered, jumps of up to 2 texels at the patch edge are predicted.
- T7 mipmaps: T5 with dgVoodoo mipmapping `appdriven` and `autogen_bilinear`, judging shimmer and frame time.
- T8: 17 separated craters in one M1 cell; the 17th is refused. T9: fire at M2 `g502` (x ≈ 2900–3070, z ≈ 885–1010); no crater. T10: a crater at M4 x ≈ 2206, z ≈ 2800 over stacked layers; check rim heights. T11: crater a road, save, restart, load; the crater may return with the sand crater material.
- T12: two `amphi` copies, kill the second with `STOP_ANIMATION radar_spin` in its death sequence; the first copy's radar is predicted to stop; then save and load with one copy dead. T13: a three-turn spin in one segment and 600 iterations of a 1 s loop against a stopwatch; about half a frame of drift per iteration.
- T14: multiplayer m14 without a patch: copy `zbd\m7` to `zbd\m14`, add `m14_zbd.gs`, `support\initm14.gw`, `commonm14.gw`, `tex_fxm14.gw` and a `dialog.zrd` world entry; host world 8 and join with a second client; every weapon predicted available. T15: an m2 single-player save retargeted to m14 with m2's files copied as m14 loads and round-trips.
- T16: remove `activate_rad` from one M1 network; its vehicle predicted not to move before being shot. T17: complete M3 objective 5; the objectives panel predicted to read "INACTIVE". T18: delete `READ_SOUND` from M1 objective 3, complete it before objective 2, review; a crash predicted.
- T19: walk M1 across zone transitions with the camera low and high; drawn geometry follows the camera's zones, not the vehicle's.
- Vehicle upgrade acceptance: startup; two live copies where one dying leaves the other unaffected; later spawns; movement, reversing and turning; slopes and soils; aiming and firing; LOD changes; death and repeated spawns; save and reload; the player vehicle's mode changes.

## Flaky tests

- `MissionCoordinateGrowthTests.FullValveNameSearchDoesNotConcatenateTheAuthoredString` and `JsonPreviewColdBudgetTests.CustomizedScalarGraphsAreOmittedWithoutSerialization` measure thread allocations and fail intermittently on the CI runner. If the change does not touch AI valve search or JSON preview, rerun the failed job (`gh run rerun <id> --failed`) before investigating.
- `SoundAliasLookupRetainsNamesAndLoopFlagsWithoutCopyingIgnoredPayload` can fail when test assemblies run in parallel; rerun it alone before treating it as a regression.
