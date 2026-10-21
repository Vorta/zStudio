# zStudio agent rules

Rules for coding agents (Codex reads this file; Claude Code imports it through `CLAUDE.md`). Each rule is stated once; detail lives in the linked `docs/agents/` files.

## Scope

- Windows 11 x64 WPF editor for RECOIL source projects (`data/` + `gamegen/`), reconstructed once from the original game files and exported back to ZBD files. C# 14, .NET 10, WPF Fluent, HelixToolkit/Direct3D 11; the format library (`zStudio.Core`) stays independent of UI and rendering.
- Compiled game folders open only through MCP `open_root`, for read-only inspection, preview and export. Do not add compiled-asset editing in code, GUI or MCP (owner decision). RECOIL 1998 demos and MechWarrior 3 are inspection only; MechWarrior 3 work is deferred.
- Python exists only in the Blender extension (`blender/zstudio_recoil`) and the optional export checker (`tools/check_exports.py`). The app, builds and tests never need Python or Blender.

## Never

- Write into `zbd_1998/`, `zbd_1999/` or the external reconstruction/decompilation projects; they are read-only evidence, never build dependencies, never committed.
- Commit game data, extracted assets, build output, corpus reports, credentials, `.agent/` or `design/`.
- Discard or overwrite pre-existing changes.
- Enable MCP access yourself, change the user's MCP opt-in, or capture or move the user's physical mouse.
- Let an export overwrite its inputs or escape its destination, or replace game files without explicit consent.
- Put validation results, counts or history in tracked docs; `CHANGELOG.md` is the only history.
- Import previous-version settings, keep runtime aliases or migrate old projects.

## Workflow

- Public repository Vorta/zStudio, MIT licence and notices kept. Branch → PR linked to its issue → passing CI → expected-head squash merge; never bypass or weaken the main and tag rules ([releasing](docs/agents/releasing.md)).
- New features stay local commits until the owner has tested the build and approved; then push and open the PR (owner decision).
- Commit messages and PR descriptions carry no AI co-author or "generated with" attribution lines (owner decision).
- Review cycle: adversarial subagent reviewers; fix their findings; review again with fresh reviewers while new findings appear; later rounds cover only what changed since the previous round (owner decision). Procedure: [code-review](docs/agents/code-review.md).
- PR watch handling: for an explicitly armed watch follow [pr-watch](docs/agents/pr-watch.md); a notice continues the authorized task and never overrides newer stop, pause or scope instructions.
- `.agent/HANDOFF.md` (git-ignored): read at the start (branch, pending work, machine data locations, owner preferences); update before stopping. Review ledgers: `.agent/REVIEW-<topic>.md`.
- Record user-visible changes in `CHANGELOG.md` under `## X.Y.Z — Unreleased`; lasting user-facing limits in that feature's guide; engine facts in [engine-evidence](docs/agents/engine-evidence.md); results in the PR description and `.agent/`.
- Identities: solution `zStudio.slnx`; project folders, files, assemblies, namespaces, embedded resources and friend assemblies `zStudio.*`; settings, logs and MCP registrations in `%LOCALAPPDATA%/zStudio`; a fresh profile keeps MCP disabled.

## Build, test, package

- Build `dotnet build zStudio.slnx -c Release`; test `dotnet test --solution zStudio.slnx -c Release`. CI restores with `--locked-mode`: keep lock files in sync with package changes.
- After changing MCP commands regenerate the catalog, then rebuild: `dotnet run --project tools/zStudio.PreviewCheck -c Release --no-build -- --mcp-catalog src/zStudio.Mcp/CommandCatalog.json`. Full catalog parity is tested.
- Tests are minimal and must-have only ([testing](docs/agents/testing.md#minimal-must-have-tests)). Corpus-gated tests, tools and in-game checks: [testing](docs/agents/testing.md#corpus-gated-tests). Delete scratch corpus diagnostics before committing. Run UI checks serially and restore user settings.
- `artifacts/zStudio-win-x64` and its ZIP are the latest validated build: launch it for the user and for live GUI/MCP work, republish with `tools/publish.ps1` after application changes before handing off (stop running zStudio instances first), keep no superseded builds anywhere ([local artifacts](docs/agents/releasing.md#local-artifacts)).

## Engineering invariants

- ZBD is five format families: probe binary structure and version, never names alone. Preserve record identities, order, unknown data and source ranges. Names are not unique; stored pointer values are metadata.
- Nullable types, explicit bounded little-endian reads, cancellation, deterministic exports, actionable diagnostics. A successful parse never establishes engine compatibility.
- Bound data before hex/JSON conversion, interpolation, cloning or aggregation, not only at the MCP response boundary. Classify heterogeneous records by validated structure and version before counting duplicates or resolving links ([code-review](docs/agents/code-review.md#required-risk-passes)).
- Before changing engine-dependent code read [engine-evidence](docs/agents/engine-evidence.md) and keep it current. Emulate mechanisms that reproduce the shipped data, never per-mission exceptions. Settle disputed semantics with evidence ([architecture](docs/agents/architecture.md#formats-and-evidence)).
- MCP parity: every user-facing capability works in both GUI and MCP through the shared command registry and editing, preview and export services, with the same identities, revisions, drafts, undo and verified saves; never a second parser or a bypass of source protections. With each feature update typed schemas, `docs/mcp-capabilities.json`, `docs/mcp.md`, the catalog and meaningful protocol tests. The parity test rejects unmapped XAML handlers and mappings to missing tools; reviewers also check bindings, dynamically created controls and renderer interactions. Generated property fields and actions inherit MCP exposure. Transport rules: [architecture](docs/agents/architecture.md#mcp).
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
