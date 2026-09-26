# Changelog

Version numbers follow `MAJOR.MINOR.PATCH`; the 0.x series is under active development. Release tags and downloadable archives use the same version.

## 0.5.0 — Unreleased

- Correct active-view MCP state after viewer changes, apply list queries before paging, reject invalid layout batches before changing preferences, report stale structural targets explicitly, and preserve 3D capture proportions. Add packaged stdio corpus regressions.

- Add opt-in local MCP access to the visible zStudio workspace through a stdio connector and same-user named pipe, including on-demand visible startup (handshake and discovery stay windowless), connection controls and activity history.
- Expose browsing/inspection, animation properties and structure, preview and camera controls, texture/audio inspection, pickup editing, undo/redo, verified saves, exports, diagnostics and captures. Retain document revisions, explicit draft/unsaved decisions and asynchronous operation tracking.
- Require MCP parity for future GUI features; add an independent action inventory, schema/protocol tests and real-file/portable-connector verification tools.

## 0.4.8 — 2026-09-25

- Replace model/Whole world Fly camera with captured freecam: WASD movement, Space/C vertical movement, mouse-look, wheel speed adjustment and Escape to release controls. Movement is time-based and independent of surface distance; diagonal input is normalized and the camera stays upright.
- Release freecam input on focus/capture loss, hidden or replaced previews and shutdown. Show current speed and controls in the viewer, support toolbar overflow, and retain normal orbit/pickup controls after exit. Animation navigation is unchanged.

## 0.4.7 — 2026-09-24

- Redesign the Windows 11 Fluent workspace with resizable navigation, preview, Inspector and bottom tool panes, saved layouts, workspace presets, and Compact/Comfortable density.
- Keep a single title/menu row with full-height window controls and centered document name plus Undo/Redo/Save icons. Preserve native caption dragging, resizing and the maximize/Snap target.
- Hide empty workspace controls before opening a folder. Use Files for open-document navigation, dirty markers and per-file close buttons; place search in Search and show Assets/Document scene only when applicable. Render recent-folder paths literally, including underscores.
- Organize animation tools as Sequences, Settings and References. Open Sequences initially and retain the selected tab when switching animations. Describe event operands, referenced animations and scheduling thresholds in the sequence tree.
- Open Properties in one reusable, resizable window pinned to its owning document and record. Keep icon Undo/Redo beside its breadcrumb, preserve unfinished drafts with inline validation and Escape restoration, and retain document-scoped save/history commands.
- Display numeric property values in plain decimal notation without losing float precision; continue accepting exponent input.
- Consolidate animation and 3D preview options into accessible icon toggles with accent-filled active states and tooltips. Put LOD, difficulty and Frame first; remove duplicate Settings controls and group Grid, Height and independent ground collision together in overflow.
- Support live signed preview Height from −999 to 999 in a compact field, with preserved playhead/playback and partial input during layout changes. Explain collision relative to authored coordinates as Y=−Height.
- Separate observed Dispatch/Event log, structured Problems, current Runtime, Related references and original-source Bytes tools. Retain source identity and distinguish stored data from preview state.
- Fit restored window bounds to desktops smaller than the preferred minimum, avoiding a startup exception on small or scaled displays.
- Keep pinned Properties drafts untouched while browsing the world; resolve them only for actual pickup-handle clicks, and consume rejected handle clicks without starting a drag.
- Navigate Problems to the supplied asset identity even when the error offset is inside its record; use source-range containment for offset-only diagnostics and reveal the selected asset through filters.
- Refresh Whole world preview notices, counts and summary after successful difficulty changes while retaining unrelated operation diagnostics.
- Refresh title-bar and menu Undo/Redo directly from the active document history, including pinned Properties edits while a non-animation asset is being viewed.
- Apply Reset layout to live tab selections and persist the defaults without committing Properties drafts or restarting the preview.
- Fix the reentrant window-close error when discarding changes, and retain save/discard guards for pending property drafts and edited documents.

## 0.3.0 — 2026-09-24

- Introduce the first 3D editing workflow: move mission pickups in Whole world; select an instance, see its bounds, unlock world-axis arrows, or enter exact XYZ coordinates in Properties.
- Enlarge pickup XYZ handles and use generous, DPI-aware screen-space click targets for their shafts and tips. Prioritize handles over scene geometry and show a hand cursor over a draggable target.
- Keep maps locked by default; add one-action drag undo/redo, cancellation, and preserved edits/selection across difficulty and LOD changes.
- Cancel pickup drags when the pointer leaves the viewport, including captured movement or release outside its bounds, restoring the starting position without an undo entry.
- Apply moves to uniquely matching difficulty records while retaining amounts, rotations and respawn metadata. Report ambiguous or unmatched counterparts without changing them.
- Save surgical coordinate patches to the owning ZBD archive, with verified Save As, external-change checks, atomic file replacement and optional backups (off by default). Preserve protected reference datasets.
- Resolve Windows destination aliases before checking protected folders, including substituted drives and new Save As directories; retain edits and report an error if the destination cannot be verified.
- Reject the current save target during pickup Save As; only ordinary Save may replace it.
- Include pickup edits in document dirty-state, close/reload prompts, and the shared Save/Undo/Redo commands.

## 0.2.18 — 2026-09-23

- Publish the open-source repository with contribution guidance, protected main/release tags, and confidential vulnerability reporting.
- Refresh repository documentation and bundled guides for public use.
- Rename the solution to `zStudio.slnx` and all project folders/files to `zStudio.*`; update build, CI, packaging and documentation references while preserving assembly names, namespaces and existing settings.

## 0.2.17 — 2026-09-23

First zStudio repository release. Earlier builds were developed locally as Recoil ZBD Studio; their unavailable binaries are not reconstructed as releases.

- Rebrand the desktop application and portable executable to zStudio, use the updated Studio icon, and retain existing user settings.
- Start a fresh desktop-project history and archive the previous Python CLI separately. Keep the optional independent export checker self-contained.
- Add contributor documentation, bug/feature/compatibility issue forms, PR guidance, dependency updates, Windows CI and version-tagged ZIP releases with SHA-256 checksums.
- Include the complete existing Recoil viewer/editor: five ZBD format families; texture, audio, script and data inspection; model/whole-world previews and exports; version-28 animation editing with undo and verified Save As.
- Retain animation playback/audio, texture cycles, alpha rendering, per-object LOD choices, mission difficulty, initialized turrets and pickups, camera following, ground grid/height, and resizable sidebar sections with the dispatch graphic below the player.

Other Zipper Interactive games and non-animation content editing remain future work. See the README and animation guide for preview approximations and compatibility limits.
