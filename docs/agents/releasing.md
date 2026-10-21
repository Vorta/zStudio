# Releasing

## Repository protections

- Vorta/zStudio is public. Feature branch → PR → maintainer review → passing CI → squash merge (the only merge method); merged feature branches are deleted automatically.
- Rules are checked in under `.github/rulesets/` and apply without bypass actors. `main` requires the GitHub Actions check **Windows build and tests**, an up-to-date base branch, a PR and resolved conversations, and forbids deletion and force pushes. The approval count is zero because Vorta is the sole maintainer (including authenticated assistant work); review stays the maintainer's responsibility.
- Release tags `v*` cannot be deleted or force-updated.
- Authentication: the GitHub connection for issues, reviews and merges; GitHub CLI for settings and releases. No tokens in the repository or workflows; CI uses its short-lived token. Workflow tokens default to read-only, workflows cannot approve PRs, only the release publication job has release-write permission. Fork workflows need maintainer approval for all external contributors and run as `pull_request` with read-only permissions and no secrets; never `pull_request_target`.

## Cutting a release

1. Merge open Dependabot PRs first, then merge `main` into the release branch and revalidate it ([AGENTS.md](../../AGENTS.md#build-test-package)).
2. Set the same `MAJOR.MINOR.PATCH` (or an explicit prerelease suffix) in `Directory.Build.props` and `blender/zstudio_recoil/blender_manifest.toml` (the guides name the extension `zstudio_recoil-<version>.zip` generically). Turn `## X.Y.Z — Unreleased` in `CHANGELOG.md` into `## X.Y.Z — YYYY-MM-DD` (the release workflow extracts that section as the notes). Update the README download link, the only other place a version is pinned.
3. Run the Release build and tests and the relevant corpus/UI checks ([testing](testing.md)). CI also publishes and verifies a portable package on a clean runner without game assets; original-game compatibility needs separate validation.
4. Merge the reviewed version change to `main` after CI passes (expected-head squash merge). Then tag current `main`:

   ```powershell
   git switch main
   git pull --ff-only
   git tag -a vX.Y.Z -m 'zStudio X.Y.Z'
   git push origin vX.Y.Z
   ```

5. The Release workflow checks tag/version equality and `main` ancestry, restores locked dependencies, builds, tests and packages, then a separate job uploads the verified ZIP and `SHA256SUMS`. Prerelease suffixes create prereleases; ordinary versions become the latest release.
6. Download the published ZIP into an empty folder, check it against `SHA256SUMS`, verify it, and launch its `zStudio.exe` on a private desktop ([testing](testing.md#environment)). The packaged Blender extension holds the runner's CRLF copies of the sources, so verify a downloaded release from a CRLF worktree of the tag: `git -c core.autocrlf=true worktree add --detach <temp> vX.Y.Z`, run `<temp>/tools/verify-package.ps1 -Directory <extracted> -Archive <zip> -ExpectedVersion X.Y.Z`, then remove the worktree.
7. Never move a tag or replace published assets: fixes get a new version. A failed unpublished workflow may be rerun after an external failure; source or workflow changes need a new version once a release exists.

## Portable package

- `./tools/publish.ps1` creates `artifacts/zStudio-win-x64/` and `artifacts/zStudio-<version>-win-x64.zip` and runs `tools/verify-package.ps1`: the root layout, required files and user guides, the absence of `docs/agents/` and `docs/mcp-capabilities.json`, file versions, the relative apphost binding, the Blender extension's version and file hashes against its sources, and SHA-256 parity of every ZIP entry.
- The ZIP root holds exactly `zStudio.exe` and `dependencies/`: managed and native runtime, `LICENSE`, `THIRD-PARTY-NOTICES.md` and `licenses/`, `README.md`, `CHANGELOG.md`, `CONTRIBUTING.md`, `SECURITY.md`, the user guides (`docs/` without `docs/agents/` and `docs/mcp-capabilities.json`) and the Blender extension ZIP plus its guide under `dependencies/blender/`. Self-contained and unsigned.
- `zStudio.exe` is the SDK apphost bound to `dependencies/zStudio.Desktop.dll` by relative path; no wrapper process and no custom assembly resolver. The icon is the checked-in `src/zStudio.Desktop/Assets/studio.ico` (the owner's artwork).
- Publishing stages a fresh build below `artifacts/`, moves the previous output aside, swaps in the new one and deletes the old folder and older ZIPs; a custom `-Output` must also be below `artifacts/`.
- Manual distribution check: extract into a folder with spaces, start `zStudio.exe` from an unrelated working directory with a dataset path containing spaces and installed-runtime discovery disabled; `hostfxr.dll`, `hostpolicy.dll`, `coreclr.dll` and WPF native modules load from `dependencies/`; the six RT_ICON resources match the six ICO frames.

## Local artifacts

- Keep `artifacts/zStudio-win-x64` and its ZIP the latest validated local build. Launch it for the user and for all live GUI/MCP work, including unreleased features; never point the user at `src/**/bin`.
- Republish after application changes before handing off or using the changed application; stop running zStudio instances first, without asking (owner decision; it applies to publishing only).
- Keep no superseded portable folders or ZIPs (in `artifacts`, `.agent` or temporary folders). Reports and checksum sidecars go to the OS temporary folder. A local package refresh is not a release and changes no version.

## Maintenance checks

- Committed source, licences, notices, documentation and releases hold no game data, external research sources, credentials or local reports.
- The main and tag rules stay active and match their checked-in definitions; the required Windows check stays tied to GitHub Actions.
- Confidential vulnerability reporting stays enabled and the Security policy link works.
- Dependency alerts and updates, CODEOWNERS, fork PR checks (approval for all external contributors), issue forms and release downloads work.
- Repository, download and contribution links work anonymously.
- Wiki and Discussions stay disabled; bugs, features and compatibility requests use Issues. No workflow approves or merges PRs or changes repository visibility.
