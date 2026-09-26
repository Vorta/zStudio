# Contributing to zStudio

Bug reports, feature requests and pull requests are welcome. Use the issue forms to report bugs, propose features or request support for another game.

## Reports and proposals

Use the [issue forms](https://github.com/Vorta/zStudio/issues/new/choose). For bugs, include the zStudio version from Help > About, Windows version, game/data version, archive path, asset name **and record index**, reproduction steps, and expected/actual behavior. Include relevant Diagnostics or animation Status/Event trace text, removing personal information. Do not attach game archives, extracted assets or decompilation databases. Synthetic reproductions and descriptions of format structure are preferred.

For a feature or another game's compatibility, explain the intended workflow and available evidence. Discuss substantial format or architecture changes in an issue before implementation. Current compatibility is Recoil; other Zipper Interactive titles are planned, not presumed compatible.

## Build and test

Use Windows 11 x64 and the .NET SDK specified in `global.json`. Visual Studio 2026 with the .NET desktop workload is optional.

```powershell
dotnet restore zStudio.slnx --locked-mode
dotnet build zStudio.slnx -c Release --no-restore
dotnet test --solution zStudio.slnx -c Release --no-build
```

These checks use synthetic fixtures and require no game data, Python, Binary Ninja or external reconstruction project. Optional corpus, graphics and audio checks are described in [docs/testing.md](docs/testing.md); run UI checks sequentially and keep their outputs outside the source tree. Mark checks you cannot run honestly in your PR.

## Code and pull requests

1. Fork the repository when public, or use a feature branch if you have access. Start from current `main`.
2. Keep the change focused and follow `.editorconfig` and [AGENTS.md](AGENTS.md). Use C# 14, nullable types, bounded little-endian reads and actionable diagnostics. Keep format models independent of UI/rendering.
3. Preserve source bytes, unknown fields, record identities and order. Do not infer identity from names. Add meaningful regression/malformed-input tests for behavior changes. Successful parsing alone does not establish game compatibility.
4. Run the relevant checks, document visible behavior and limitations, and update the Unreleased section of the changelog.
5. Open a PR describing the concrete problem, resulting behavior, related issue and verification. Maintainer review and successful CI precede a squash merge. Resolve review conversations and update the branch when `main` changes.

Never commit game data, build output, credentials, local settings, corpus reports or external reconstruction/decompilation sources. Keep dependency lockfiles in sync with intentional package changes. The optional Python export checker must stay independent of the archived CLI.

Every new user-facing feature must also work over MCP. Use shared command/editing services and the same validation, identities, undo and save protections as the GUI. Update typed tool schemas, [the capability inventory](docs/mcp-capabilities.json), [MCP documentation](docs/mcp.md), and protocol tests. CI checks XAML action-handler coverage against the independent inventory; review must additionally cover bound and dynamically constructed controls. A label in the inventory alone is not evidence of working parity. Property fields/actions created through FieldEditor are exposed automatically. MCP must return explicit draft/conflict errors rather than opening a modal dialog or discarding input.

Treat contributors respectfully, discuss the work rather than the person, and avoid harassment or disclosure of private information. Vorta maintains the repository and may moderate discussions to keep them constructive.

Contributions are provided under the project's existing [MIT license](LICENSE). Include attribution and compatible notices for third-party code. See [SECURITY.md](SECURITY.md) for vulnerability reports and [docs/releasing.md](docs/releasing.md) for release procedures.
