# Contributing to zStudio

Bug reports, feature requests and pull requests are welcome.

## Reports and proposals

Use the [issue forms](https://github.com/Vorta/zStudio/issues/new/choose). For a bug include the zStudio version from Help → About, the Windows version, the game and data version, the project-relative file path, the object or record name **and index**, steps to reproduce, and expected and actual behaviour. Add relevant Diagnostics, Problems or Event log text with personal information removed. Never attach game archives, extracted assets or decompilation databases; synthetic reproductions and descriptions of format structure are preferred.

For a feature or another game's compatibility, describe the intended workflow and the available evidence. Discuss substantial format or architecture changes in an issue first. zStudio supports RECOIL; other Zipper Interactive titles are not presumed compatible.

## Build and test

Use Windows 11 x64 and the .NET SDK pinned in `global.json` (Visual Studio 2026 with the .NET desktop workload is optional).

```powershell
dotnet restore zStudio.slnx --locked-mode
dotnet build zStudio.slnx -c Release --no-restore
dotnet test --solution zStudio.slnx -c Release --no-build
```

These use synthetic fixtures and need no game data, Python, Blender or external reconstruction project. Corpus, graphics, audio and Blender checks are in [docs/agents/testing.md](https://github.com/Vorta/zStudio/blob/main/docs/agents/testing.md); run UI checks one at a time and keep their outputs outside the source tree. After changing MCP commands regenerate the discovery catalog and rebuild:

```powershell
dotnet run --project tools/zStudio.PreviewCheck -c Release --no-build -- --mcp-catalog src/zStudio.Mcp/CommandCatalog.json
```

## Pull requests

1. Fork the repository (or use a branch if you have access), starting from current `main`.
2. Keep the change focused and follow `.editorconfig` and [AGENTS.md](https://github.com/Vorta/zStudio/blob/main/AGENTS.md): C# 14, nullable types, bounded little-endian reads, actionable diagnostics, format models independent of UI and rendering.
3. Preserve source bytes, unknown fields, record identities and order; never infer identity from names. Add only must-have tests ([test policy](https://github.com/Vorta/zStudio/blob/main/docs/agents/testing.md#minimal-must-have-tests)). A successful parse does not establish game compatibility.
4. Every user-facing feature also works over MCP through the shared command and editing services, with the same validation, identities, undo and save protections. Update the typed tool schemas, [the capability inventory](https://github.com/Vorta/zStudio/blob/main/docs/mcp-capabilities.json), [the MCP guide](docs/mcp.md) and protocol tests. CI checks XAML action handlers against the inventory; reviews also cover bound and dynamically created controls. MCP returns explicit draft and conflict errors instead of opening a dialog or discarding input.
5. Document visible behaviour and limits in the relevant guide and add an entry under the Unreleased section of `CHANGELOG.md`.
6. Open a PR describing the problem, the resulting behaviour, the related issue and how you verified it. Maintainer review and passing CI precede a squash merge; resolve review conversations and update the branch when `main` changes.

Never commit game data, build output, credentials, local settings, corpus reports or external reconstruction/decompilation sources. Keep lock files in sync with intentional package changes.

Treat contributors respectfully, discuss the work rather than the person, and keep private information private. Vorta maintains the repository and may moderate discussions.

Contributions are made under the project's [MIT license](LICENSE); include attribution and compatible notices for third-party code. Vulnerabilities: [SECURITY.md](SECURITY.md). Releases: [docs/agents/releasing.md](https://github.com/Vorta/zStudio/blob/main/docs/agents/releasing.md).
