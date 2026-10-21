# zStudio agent rules

Rules for coding agents (Codex reads this file; Claude Code imports it through `CLAUDE.md`). Each rule is stated once; detail lives in the linked `docs/agents/` files.

## Scope

- Windows 11 x64 WPF editor for RECOIL source projects (`data/` + `gamegen/`), reconstructed once from the original game files and exported back to ZBD files. C# 14, .NET 10, WPF Fluent, HelixToolkit/Direct3D 11; the format library (`zStudio.Core`) stays independent of UI and rendering.
- Compiled game folders open only through MCP `open_root`, read-only: inspection, preview and export. Never add compiled-asset editing in code, GUI or MCP (owner decision). The July and August 1998 demos (version-13 worlds) and MechWarrior 3 are inspection only (the September 1998 demo reconstructs like a release); MechWarrior 3 work is deferred.
- Python exists only in the Blender extension (`blender/zstudio_recoil`), the Blender round-trip driver (`tools/blender-roundtrip.py`) and the optional export checker (`tools/check_exports.py`). The app, builds and the plain test suite never need Python or Blender.

## Never

- Write into `zbd_1998/`, `zbd_1999/` or the external reconstruction/decompilation projects; they are read-only evidence, never build dependencies, never committed.
- Commit game data, extracted assets, build output, corpus reports, credentials, `.agent/` or `design/`.
- Discard or overwrite pre-existing changes.
- Enable MCP access yourself, change the user's MCP opt-in, or capture or move the user's physical mouse (only the pointer-owning PreviewCheck modes do, in their own window: [testing](docs/agents/testing.md#tools)).
- Close, signal or kill a zStudio this session did not start: a plain `zStudio.exe` (the user's GUI) and `zStudio.exe --mcp-host` (the visible MCP workspace) may hold documents, drafts and unsaved work; wait or tell the user. Without asking you may stop: for a test run, the stdio connectors the MCP client launched (`zStudio.exe --mcp`, optionally `--instance <id>`; match the argument, `--mcp-host` must not match; the client may need to reconnect); before publishing a build, any instance (owner decision).
- Let an export overwrite its inputs or escape its destination, or replace game files without explicit consent.
- Put zStudio validation results, test counts or history in tracked docs (they go to the PR description and `.agent/`); `CHANGELOG.md` is the only history. Measurements of the shipped data ([D] in engine evidence) are evidence, not results.
- Import previous-version settings, keep runtime aliases or migrate old projects.

## Workflow

- Public repository Vorta/zStudio, MIT licence and notices kept. Branch → PR linked to its issue → passing CI → expected-head squash merge; never bypass or weaken the main and tag rules ([releasing](docs/agents/releasing.md)).
- New features stay local commits until the owner has tested the build and approved; then push and open the PR. Commit messages and PR descriptions carry no AI co-author or "generated with" lines. A big PR is split by squashing it and regrouping by subject, never at commit boundaries (owner decisions).
- Codex starts its review of a push on its own: never post "@codex review"; for a merge the owner asked for, do not wait for it more than about 30 minutes after green CI (an armed PR watch's approval rules still apply). Resolve the threads a push fixed right after pushing, without waiting for CI (owner decisions).
- Review cycle (owner decision): adversarial subagent rounds with fresh reviewers while new findings appear; round 1 covers the whole change, later rounds only the delta ([code-review](docs/agents/code-review.md#review-cycle)).
- Review bar (owner decisions): only defects retail data or realistic editing reaches block a merge; still fix gaps where ordinary editing produces content the game mishandles. Deferred (recorded in the ledger, neither fixed nor blocking): contrived input (sizes far beyond retail, synthetic hierarchies, deliberately malformed files), MechWarrior 3-only findings, and split artifacts (docs, CHANGELOG, tooltips or PR descriptions describing what a later stacked PR adds). Reviewers never read the engine reference projects; they judge code against its docs ([engine-evidence](docs/agents/engine-evidence.md) as the recorded spec), tests and behaviour on retail data, and divergences from the original engine are not findings. Mixed retail/rebuilt play is not a goal: skip interop-only fixes.
- PR watch handling: for an explicitly armed watch follow [pr-watch](docs/agents/pr-watch.md); a notice continues the authorized task and never overrides newer stop, pause or scope instructions.
- `.agent/HANDOFF.md` (git-ignored): read at the start (branch, pending work, machine data locations, owner preferences); update before stopping. Review ledgers: `.agent/REVIEW-<topic>.md`.
- Record user-visible changes in `CHANGELOG.md` under `## X.Y.Z — Unreleased`; lasting user-facing limits in that feature's guide; engine facts in [engine-evidence](docs/agents/engine-evidence.md).
- Names and settings: [architecture](docs/agents/architecture.md#identity-and-settings); the project's `.zstudio/` folder: [sources](docs/agents/sources.md#source-format).

## Build, test, package

- Build `dotnet build zStudio.slnx -c Release`; test `dotnet test --solution zStudio.slnx -c Release`. CI restores with `--locked-mode`: keep lock files in sync with package changes.
- After changing MCP commands, build Release, regenerate the catalog, then rebuild: `dotnet run --project tools/zStudio.PreviewCheck -c Release --no-build -- --mcp-catalog src/zStudio.Mcp/CommandCatalog.json`. Full catalog parity is tested.
- Tests are minimal and must-have only. Test environment, corpus-gated tests, tools and in-game checks: [testing](docs/agents/testing.md). Delete scratch corpus diagnostics before committing.
- Before a release, merge the open Dependabot PRs, then update and revalidate the release branch (owner decision; steps: [releasing](docs/agents/releasing.md#cutting-a-release)).
- `artifacts/zStudio-win-x64` and its ZIP are the latest validated build, used for the user and all live GUI/MCP work; republish after application changes ([local artifacts](docs/agents/releasing.md#local-artifacts)).

## Engineering invariants

- ZBD is five format families: probe binary structure and version, never names alone. Preserve record identities, order, unknown data and source ranges. Names are not unique; stored pointer values are metadata.
- Nullable types, explicit bounded little-endian reads, cancellation, deterministic exports, actionable diagnostics. A successful parse never establishes engine compatibility.
- Bound data before hex/JSON conversion, interpolation, cloning or aggregation, not only at the MCP response boundary. Classify heterogeneous records by validated structure and version before counting duplicates or resolving links ([code-review](docs/agents/code-review.md#required-risk-passes)).
- Before changing engine-dependent code read [engine-evidence](docs/agents/engine-evidence.md) and keep it current. Emulate mechanisms that reproduce the shipped data, never per-mission exceptions. Settle disputed semantics with evidence ([architecture](docs/agents/architecture.md#formats-and-evidence)).
- MCP parity: every user-facing capability works in GUI and MCP through the shared command registry and editing, preview and export services, with the same identities, revisions, drafts, undo and verified saves; never a second parser or a bypass of source protections. Every Properties, card or panel field needs its own MCP path (`source_world_object_edit` for source objects, `pickup_move`/`scene_card` for placements, `source_terrain_edit` for terrain recipes, `source_ai_network` and `source_zone_label` for section cards). With each feature update typed schemas, `docs/mcp-capabilities.json`, `docs/mcp.md`, the catalog and a protocol check that exercises the new path ([minimal tests](docs/agents/testing.md#minimal-must-have-tests)). The parity test rejects unmapped XAML handlers and mappings to missing tools; reviewers also check bindings, dynamically created controls and renderer interactions. Transport rules: [architecture](docs/agents/architecture.md#mcp).
- Source projects are authoritative and game files change only on explicit export: [sources](docs/agents/sources.md).
- Source glTF format 4 through the shared `WorldGltf` codec for every reader, writer, edit and comparison; earlier formats and old project layouts are refused with a message to reconstruct the project ([source format](docs/agents/sources.md#source-format)).
- The SI keyframe compiler reproduces every shipped keyframe bit for bit: change its arithmetic only with new reference vectors, and write a script only when it compiles back exactly ([keyframe scripts](docs/agents/sources.md#keyframe-scripts)).
- GUI contract: [workbench](docs/agents/workbench.md). Preview semantics: [preview](docs/agents/preview.md).

## Doc map

| Area | File |
| --- | --- |
| Layers, MCP transport, saves, rendering pipeline, evidence sources | [docs/agents/architecture.md](docs/agents/architecture.md) |
| Source format, reconstruction, owner decisions, builds, workspace | [docs/agents/sources.md](docs/agents/sources.md) |
| Engine facts with evidence | [docs/agents/engine-evidence.md](docs/agents/engine-evidence.md) |
| How the developers built the shipped files | [docs/agents/recoil-original-worktree.md](docs/agents/recoil-original-worktree.md) |
| GUI contract | [docs/agents/workbench.md](docs/agents/workbench.md) |
| Preview semantics | [docs/agents/preview.md](docs/agents/preview.md) |
| Tests, tools, in-game acceptance | [docs/agents/testing.md](docs/agents/testing.md) |
| Review procedure | [docs/agents/code-review.md](docs/agents/code-review.md) |
| PR watch | [docs/agents/pr-watch.md](docs/agents/pr-watch.md) |
| Releases and packaging | [docs/agents/releasing.md](docs/agents/releasing.md) |
| Open roadmap | [docs/agents/roadmap.md](docs/agents/roadmap.md) |
| User guides (shipped) | `README.md`, `docs/*.md`, `blender/zstudio_recoil/README.md` |
