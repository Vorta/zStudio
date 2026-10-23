# Workbench GUI contract

Rules for the WPF workbench. User-facing descriptions: [map editor](../map-editor.md), [compiled files](../compiled-files.md), [3D navigation](../3d-navigation.md).

## Welcome screen

- Before a root opens show the welcome screen; hide workspace panes, actions and search without changing saved layout preferences.
- One card, Work with source project. Initialize: dialog with retail ZBD folder and project folder fields, Browse buttons, Initialize and open enabled only when both are given, progress and refusals shown in the dialog, Cancel stops the work. Open: only a folder holding `data` and `gamegen`.
- File → Open workspace… (Ctrl+O), Recent folders, a dropped folder and a command-line folder open only a source project and refuse any other folder with what is wrong with it; files are refused.
- Recent-folder paths render literally: underscores are never menu access keys.

## Workbench layout

- Fluent workbench, always compact; no density setting (owner decision).
- Compiled explorer (MCP-opened compiled roots; in a source project the Navigator and Inspector are collapsed, Reset layout is hidden and the [source map editor](#source-map-editor) applies): one Navigator (Files, Assets, Search, Document scene), the central preview with persistent transport, one Inspector (animation tabs), bottom tools (Dispatch, Event log, Problems, Runtime, Related, Bytes), and a separate [Properties window](#properties-window).
- Keep both side panes visible when the viewport has room. Hide the whole right pane for non-animation viewers.
- Preferred pane dimensions, groups and tabs persist; automatic narrow collapse never overwrites them.
- Resizing, fullscreen and theme changes never replace or dispose the editor or commit drafts.
- Grouped native ToolBar overflow.
- Problems keeps the emitter's severity and source context and separates file/operation issues from preview, resource and support notices. Event log holds observed dispatches with distinct occurrence identities and discloses its caps. Runtime holds the current simulation state.
- Bytes shows original-source bytes, bounded to 4096, labelled as distinct from properties and runtime memory.
- No bottom Cancel button and no manual indexing cancellation (owner decision). Automatic cancellation on workspace or document replacement and on shutdown stays.

## Update check

- Owner request 2026-10-23: the status bar shows **Update available** (with the version) directly left of Preview notices when GitHub's latest published release (`releases/latest`: no drafts or prereleases) is newer than the build: SemVer of the tag against the informational version before `+`, so a prerelease build sees its release as newer. The item is docked right beside the source map's status group, so it also shows without a source map (welcome screen, compiled explorer). A click opens the release page, only one under `https://github.com/Vorta/zStudio/releases/` (anything else opens `…/releases/latest`).
- Help → **Check for updates automatically** (saved, default on): one check after the window shows (application startup, never tests), at most once a day: every check that asks GitHub saves its time (`UpdateAttemptedUtc`), so a failed, refused or rate-limited one waits a day too; the last successful check and its release are saved and shown until the next. The request runs on the thread pool (the first one can block in proxy discovery, which its timeout cannot interrupt). Silent: checking, an up-to-date build and any failure (offline, proxy, TLS, 4xx/5xx, rate limit, bad JSON, 10 s timeout) show nothing. Turned off, nothing shows until an explicit check, whose result (including a failure: `unknown` with its reason) then shows. Running 0.9.6 or older on the same profile drops the setting (older versions drop unknown keys), so automatic checks turn back on.
- Help → **Check for updates** is an explicit request, also with automatic checks off: the item shows "Checking for updates…", then Update available, or for 8 s "zStudio is up to date" or "Couldn't check for updates" (the reason in its tooltip; a click dismisses it). Closing cancels a running check.
- A private test profile never contacts GitHub; tests give the window a checker over a fake handler. MCP: `update_check` (state, check, automatic) and `state`'s `update`; MCP returns the release page URL and opens nothing.

## Navigator

- Files owns open-document navigation: marks open, active and dirty files; row close buttons keep the save/discard guards. Opening a file from Files switches to Assets. No document tabs.
- The search input belongs to Search.
- Hide Assets when no file is open; hide Document scene unless the active document has scene roots. Before hiding the selected tab move to Assets if a document remains, otherwise to Files. Document changes keep an existing Files or Search selection.
- Source selection uses stable entry, sequence and event identities. Runtime markers and asset export selection stay separate from it.
- Document scene (compiled explorer): the active 3D preview's hierarchy (an animation's stable bound scene; else the selected file's stored nodes). Shared references appear under each parent with a shared label; **Unlinked nodes** keeps disconnected roots and cyclic components; invalid references, cycles and the 256-level display limit are explicit leaves. Selecting a row inspects it and sets the Frame-selected target; it never moves the camera or opens or retargets Properties. Expansion and selection persist through presentation changes and compatible refreshes for the document's lifetime, matching mission objects by provenance, never by clone index. Presentation-only expansion, collapse and reveal never commit pending input.

## Properties window

- One reusable, owned, modeless window for every viewer, opened through a row context menu or Alt+Enter (in a source map Alt+Enter focuses the docked card — a source object's first control, or a placement's first editable coordinate while it is shown, else the player tank's chosen master type (see below) or the keyboard stays — also for a pickup, vehicle or animation-placed object chosen in the World tree; this window opens instead for a terrain piece (its recipe), for a pickup, for a vehicle or animation-placed object (read-only), and for an AI node or AI path selected in the view, and with nothing selected for the selected asset, else the archive). Closed at startup; size and position saved; closes when its document is disposed; the window X closes it. Never a docked pane and never takes workspace space; no bottom button row.
- Its document and record identity stay pinned across selection and preview replacement; ordinary selection never retargets it. Never bind editable fields to the active row; never keep a hidden renderer alive for it.
- Fields are editable for source documents (source objects, source pickups); compiled assets show a read-only view.
- Its commands target its owning document; native text undo stays. Icon-only Undo/Redo at the top right of the breadcrumb row; trimmed single-line heading with a full-context tooltip; document-scoped Ctrl+S.

## Drafts

- Scalar and compound drafts commit on Enter or an intentional field boundary, with inline validation; Escape restores. Exception: the source object card (`SourceObjectCardEditor`, `ManualDraftMode`) keeps its draft through Enter and focus loss; only Apply accepts it. Resolve drafts explicitly before navigation, retarget, save or close, including clean documents with pending input; popup drafts first. MCP draft rules: [architecture](architecture.md#mcp).
- Numbers display in ordinary decimal notation with the exact stored float precision; scientific notation is accepted as input; viewing a value creates no edit.
- Preserve unknown bits, inactive stored fields, authored rates and record order.
- Realizing a retained tree selection when its tab opens is presentation work, not navigation or a draft commit.

## Source map editor

- An open source map uses the source map editor: Save, Undo and Redo sit in its grouped command bar; the title shows the map name and an Edited pill; the World tree is the left pane; the selected-object card is always docked as the right Properties pane and never floats. These session panes never overwrite the compiled explorer's pane preferences; the [Properties window](#properties-window) rules still apply.
- World editing applies to objects throughout the hierarchy; how views update after an edit: [sources](sources.md#workspace-save-and-recovery).
- In source maps placement and object editing is always available. Compiled worlds allow read-only selection, cards and bounds, without handles or drags.
- Panes: World tree (`SourceOutlinerPane`), view, docked Properties pane; the gaps resize them for the session. Hiding a pane gives its width to the view; hiding Properties hides the selected object's form (it never floats over the view). Properties pane sections fill its width and an overflow scroll bar takes its own column.
- Diagnostics is the bottom pane: file and preview problems only, without the compiled explorer's tabs (no Related or Bytes) or its maximize and hide buttons; shown by default, the status bar toggle shows and hides it, and the project remembers that and its height. The status bar shows no messages: a failure in the map (a brush stroke, a stale conversion, Properties refused during a drag or Fly, the 3D view failing to render until it renders again) goes to Problems and opens Diagnostics.
- Status bar: the selection ("name · node #N" or "No selection"), mission start and difficulty, the player tank (below), instance and mesh batch counts; on the right the [update item](#update-check) when shown, Preview notices (with count) and the World tree, Properties and Diagnostics toggles.
- No Scene or Selection menu in a source map: the World tree's eyes (middle-click isolates) and the header's Show all eye replace Selection.
- The player tank's card puts Master type and Weapons before Details, which starts collapsed there and remembers its own expansion for the session (other objects' Details keep theirs; owner request 2026-10-23); with the tank chosen in the World tree, Alt+Enter focuses the chosen master type unless the open Details has an editable placement field (selected in the view, Alt+Enter opens the read-only Properties window, as for any vehicle). Master type and Weapons are each tagged Preview: radio groups that are one tab stop each, entered at the checked radio, arrows (Left/Right in a row, Up/Down in a column, Home/End) moving and selecting past disabled radios, tooltips on keyboard focus; disabled radios give their reason. A choice rebuilds the view like a difficulty change (pending drafts are resolved first) with "Posing…" in the changed section's header, keeps the radios' keyboard focus through the rebuild, and creates no draft, edit or undo step. With nothing selected the pane's Player tank row shows the choice and selects the tank; the status bar names a choice that differs from the defaults. Rules: [preview](preview.md#player-tank).

## Inspection card

- Selecting an object expands its details below the permanent hover readout; the panel stays at the top right as the camera or object moves, labelled offscreen or inactive when appropriate. Width and details height are bounded by the viewport, keeping the navigation cube clear (beside the panel in short viewports, a compact slot at constrained widths, its usual corner when space permits).
- Floating it is translucent (blurred scene under an 80 % tint of the theme background); docked in a source map it is opaque. The probe strip shows, on one line, the name under the cursor and the surface point with X/Y/Z coloured like the handles; outside source worlds a second row gives the object's origin.
- Header: icon, name and type (a source object's class and node number, the model in its tooltip), Isolate (not in source maps) and Close; trimmed title with a full-name tooltip. Collapsible sections: a source object's Object, Transform (Move/Rotate toggles in its header), Zone, Flags (check boxes), Surface; a placement's Details (toggles in its header). Footer: draft status with Revert and Apply, enabled once something changes. Showing a placement's handles starts its draft; hiding them ends it while it changes nothing. The card keeps its size and field positions when a draft starts or ends.
- Readouts are subdued selectable fields with stacked X/Y/Z for vectors; copy buttons keep round-trip precision and coordinate-space labels; the body has its own scrollbar gutter clear of the copy buttons. The wheel scrolls the card over its contents, never zooming the scene at a scroll limit; camera gestures start outside the card.
- Animation cards distinguish runtime instance IDs from source nodes; expired instances never silently bind to another copy. Inspection never changes events or playback.
- Height: the bottom grip resizes it (Up/Down 10 DIP, Home/End to the limits); minimum 216 DIP, default 432 DIP, maximum 80 % of the 3D viewer's height (the maximum wins in short viewers). The probe strip and action header stay fixed while the rest scrolls. One preferred height is shared by the three 3D viewers and saved; automatic shrinking and closing keep it; Reset layout restores 432. Resizing never moves the camera or commits drafts. MCP: `workspace_view` `changes.inspectionPanelHeight`.
- Closing the card clears its tree selection and Frame-selected target and collapses only the details. Compatible refreshes restore a retained source row's framing and inspection without choosing an arbitrary runtime copy. Isolating a pickup child keeps the whole placed pickup. Geometry shared by several mission actors has no uniquely editable placement and stays read-only.

## World tree

- Fixed row order: World, Zones, AI Vehicles, AI Networks, Pickups, Animations. Animations holds Objects placed by animations, then the mission and Common definition folders. Section, folder, file, pass and note rows show counts, open or close on click and are never the selection. Selecting a zone, network or definition ends the object selection and opens its card in the Properties pane; Escape, Clear selection or a click on empty view space clears it.
- Keyboard: Up/Down, Home/End and Page Up/Down visit every shown row without stopping on buttons; a selectable row becomes the selection, other rows only take focus (outline) while the selection keeps its highlight and card. Left/Right step through the row's buttons (Zones switch, Pickups Group by type, Frame, eye; Left from the first returns to the row) and show their tooltips; Space or Enter opens or closes a row or presses the button; Escape returns from a button to its row.
- After a rebuild the tree that had the keyboard gives it to the selected row. After Group by type: the Pickups row or group row that had the keyboard keeps it (a type row gives it to its group when the types go; pressing the toggle from the keyboard keeps it on Pickups); otherwise the selected row takes it, or Pickups when nothing is selected or the selection is inside a closed row.

## Title row

- One title/menu row with full-height caption controls and a Windows maximize/Snap hit region; menu and caption hit regions reserved equally on both sides.
- Outside the source map editor the current document name is centered, trimmed, with a full-path tooltip; hidden without a document. Undo, Redo and Save exist only for source documents; compiled documents are read-only and never dirty.
- No separate folder/document command row and no duplicate Export or Validate buttons; exports live in the Export menu.
- Help: **Controls and formats**, then the user guides and the release notes, which open in the browser on GitHub at the installed version's tag (`v<version>`, never `main`: guides change between versions), then **Check for updates** and **Check for updates automatically** ([update check](#update-check)), then **About zStudio**: version, licence and third-party notices from the executable, so it works without the files beside it. MCP: `help` (the update items: `update_check`).

## Escape

- Order: exit captured Fly; else leave the polygon zone tool; else cancel the VTOL drop-window draft (or its loading analysis); else cancel the AI path box, draft or Edit mode; else cancel the active pickup/placement drag or terrain stroke (only that gesture); else stop an active export, validation, reconstruction or terrain-conversion planning. Escape never cancels loaded preview lifetimes.
- In a source map, an Escape nothing else uses (no field, drag, Fly or operation) clears the selection.

## Presentation modes

- In every 3D preview F10 toggles Hide UI and F11 fullscreen, independently (owner decision).
- Hide UI leaves the view alone: no menus, command bar, panes, status bar, banners, Properties window or editor guides; clicks select and edit nothing while navigation and Fly go on. It is refused during a zone or terrain stroke; it leaves the polygon zone tool (which cannot be engaged until the UI shows again) and turns off the terrain brush, keeps layout preferences and drafts, ends when 3D content is left and is never saved. F10's release is consumed so Windows does not enter menu mode (Alt opens menus); the title strip stops moving the window while hidden; keyboard commands such as Ctrl+Z still work. MCP: `presentation` `uiHidden`.

## Fly camera

- Captures input only on explicit activation in model and Whole world previews, never through a scene-option refresh; never in animation previews.
- WASD moves along the camera basis, Space/C along world Y, mouse movement looks around, the wheel changes speed without translating. Combined movement is normalized and integrated over elapsed time without inertia; speed never depends on surface hit distance.
- Desktop owns foreground raw mouse and cursor capture with a recentered fallback for absolute or remote input; Rendering owns the motion math.
- Release all held input, cursor confinement and capture on Escape, deactivation, focus or capture loss, an unavailable or replaced preview, and shutdown. Keep the final pose; keep the icon and speed hint in sync with the actual state; support overflow activation; never persist active capture.

## Source animation card

- Source maps keep the existing world viewport, toolbar, LOD and Difficulty controls. Animation selection opens docked Properties and structural timeline rows; it never constructs an `AnimationEditor`, prepares a player, evaluates paths, measures duration, decodes audio or moves the camera. Object cards link to exact definitions/bindings, never names as identities.
- The center lower dock has Timeline and Diagnostics views. A pinned session keeps its one compact transport visible when the body is collapsed or Diagnostics is selected; no duplicate seeker or animation toolbar. Opaque Fluent surfaces cover controls, menus, margins and wrapped rows. The dock starts at 300 DIP, has a 180 DIP minimum where space permits and expands to 60% of the center. Rows are virtualized; labels start at 220 DIP and use at most 42% of center width. Narrow layouts retain Play/Pause, Stop, time and Exit while secondary actions overflow.
- Source shows concurrent sequences in source order, events, child-call references and read-only keyframe metadata. Child references begin collapsed and excluded. Expansion, Include in preview and Show guides are independent. Root events remain visible. Run shows observed instances/occurrences and incomplete or blocked coverage, never an invented global schedule. Unknown clock placement stays an authored threshold badge; no clip duration is inferred from the following event.
- First Play pins the exact candidate binding. Later selection only inspects; Use selection explicitly stops and retargets without autoplay. Stop detaches to the initialized static map at time zero while retaining the target; Exit also releases the pin. Source edits detach before creating the shared draft and leave previous evidence stale. Apply/Revert never revives a run.
- Properties contains Source, Scenario and Observed controls. Existing source event operands, activation and prerequisites retain their Apply/Revert transaction. Timing uses a numeric field and a ruler in the event's native clock; F2 opens it. Ruler movement proposes 1/60-second increments without changing clock mode/order; viewing preserves raw negative and off-grid thresholds. Global event marks cannot be dragged to edit source. Whole-sequence START_TIME is labeled ignored. Keyframes, synthetic/protected records and event insertion/deletion/reordering remain read-only.
- Scenario holds Runtime/Cleanup (Runtime by default), preview-only Height (0 by default), seed/reference/conditions and the existing environment settings. Selecting cleanup only inspects it. Scenario/inclusion changes stop and reset without automatic replay. The fixed playback range starts at 0–30 s; its ruler's visible interval is independent. Ruler drag seeks, Ctrl+wheel zooms at the pointer, Shift+wheel pans and wheel scrolls rows. Manual pan/zoom disables Follow playhead. Canvas arrows step one tick (Shift: ten), Home/End seek range bounds, Space plays/pauses, and native controls retain native keys.
- Show guides only toggles a compatible cached result; Evaluate paths explicitly samples the pinned root or candidate and its included paths, silently in separate state. Neither action changes the dock view. Camera guides are orange and object guides teal; emphasis changes alpha only. Drop windows require explicit dedicated analysis; their flight-clock move strip and route controls use the guarded adapter, never a generic endpoint drag. See [source session semantics](preview.md#source-world-animation-session).

## Compiled animation inspector

- Right tabs: Sequences, Settings, References, in that order. Entering animation preview selects Sequences; consecutive animation selections keep the selected tab; ordinary source selection never changes it.
- Compiled animations are read-only. Describe stored event operands without mutating records or resolving names as unique identities; reference indices stay visible. Timing rows are clock thresholds, not durations or observed dispatch times. Descriptions keep child-wait flags and condition/loop integer-versus-float semantics.
- Selecting cleanup never switches the playback phase.
- Transport icons stay beside the seeker at narrow widths; transport keys: [preview](preview.md#animation-runtime-and-duration).
- Settings holds binding and mission information, execution, activation points, preview range and support. One authoritative control per option: no toolbar Settings button and no duplicate presentation or audio controls in Settings.

## Viewport toolbars

- Animation and Whole world toolbars begin with LOD, an unlabeled difficulty picker, then Frame. Difficulty: [preview](preview.md#difficulty).
- Viewport toggles are individually named Fluent icon buttons with native checked state and tooltips; hit targets are 32 DIP. Grid, Height and Ground collision move together in native overflow. Whole world and model previews use the same icons and styles.
- Height applies valid values on TextChanged without Enter or blur. The Fluent clear button is hidden, reapplied after template or theme replacement, without changing single-line behaviour. Empty, incomplete or invalid text keeps the last valid height without errors or caret changes; it normalizes on an intentional keyboard-focus boundary or Enter/Escape. Overflow reparenting, layout, menus and window deactivation keep partial text and caret. A superseded asynchronous change never overwrites the newest requested height or loses the prior playback intent.

## Placement edits

- Source map placements: pickups take Euler XYZ radians shown in degrees; AIV vehicles take a Y heading in degrees; AI nodes take translation only.
- Card fields and the Move/Rotate handles share one position/rotation draft that previews until Apply (or Enter in a field) accepts one undoable source transaction. Escape during a drag cancels only that gesture; Revert (or Escape in a field) discards the draft.
- Drafts preview through `PickupPlacementEditSession.PreviewTransform` and renderer instance transforms (`SceneViewport` placement deltas and transform overrides); never mutate the cached mission baseline or shared mesh transforms.
- Difficulty counterparts, stored precision and where placement edits land: [sources](sources.md#edit-semantics).
