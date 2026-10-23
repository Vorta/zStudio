# Releasing

## Repository protections

- Vorta/zStudio is public. Feature branch → PR → maintainer review → passing CI → squash merge (the only merge method); merged feature branches are deleted automatically.
- Rules are checked in under `.github/rulesets/` and apply without bypass actors. `main` requires the GitHub Actions check **Windows build and tests**, an up-to-date base branch, a PR and resolved conversations, and forbids deletion and force pushes. The approval count is zero because Vorta is the sole maintainer (including authenticated assistant work); review stays the maintainer's responsibility.
- Release tags `v*` cannot be deleted or force-updated.
- Authentication: the GitHub connection for issues, reviews and merges; GitHub CLI for settings and releases. No tokens in the repository or workflows; CI uses its short-lived token. Workflow tokens default to read-only, workflows cannot approve PRs, only the release publication job has release-write permission. Fork workflows need maintainer approval for all external contributors and run as `pull_request` with read-only permissions and no secrets; never `pull_request_target`.

## Cutting a release

1. Dependabot PRs first ([AGENTS.md](../../AGENTS.md#build-test-package)); then merge `main` into the release branch and revalidate it.
2. Set the same `MAJOR.MINOR.PATCH` (or an explicit prerelease suffix) in `Directory.Build.props` and `blender/zstudio_recoil/blender_manifest.toml` (the guides name the extension `zStudio-Blender-extension-<version>.zip` generically). Turn `## X.Y.Z — Unreleased` in `CHANGELOG.md` into `## X.Y.Z — YYYY-MM-DD` (the release workflow extracts that section as the notes). Update the README download link, the only other place a version is pinned.
3. Run the Release build and tests and the relevant corpus/UI checks ([testing](testing.md)). CI also publishes and verifies the package on a clean runner without game assets; original-game compatibility needs separate validation.
4. Merge the reviewed version change to `main` after CI passes (expected-head squash merge). Then tag current `main`:

   ```powershell
   git switch main
   git pull --ff-only
   git tag -a vX.Y.Z -m 'zStudio X.Y.Z'
   git push origin vX.Y.Z
   ```

5. The Release workflow checks tag/version equality and `main` ancestry, restores locked dependencies, builds, tests and packages, then a separate job uploads the verified ZIP and `SHA256SUMS`. Prerelease suffixes create prereleases; ordinary versions become the latest release.
6. Download the published ZIP into an empty folder, check it against `SHA256SUMS`, verify it, and launch its `zStudio.exe` on a private desktop with a private profile ([testing](testing.md#environment)). The package's `LICENSE`, notices and Blender extension hold the runner's CRLF copies of the sources, so verify a downloaded release from a CRLF worktree of the tag: `git -c core.autocrlf=true worktree add --detach <temp> vX.Y.Z`, run `<temp>/tools/verify-package.ps1 -Directory <extracted> -Archive <zip> -ExpectedVersion X.Y.Z`, then remove the worktree.
7. Never move a tag or replace published assets: fixes get a new version. A failed unpublished workflow may be rerun after an external failure; source or workflow changes need a new version once a release exists.

## Package

- Owner decision (0.9.7): zStudio ships as one self-contained, compressed `zStudio.exe` with only the necessary files beside it. The ZIP root holds exactly `zStudio.exe`, `LICENSE`, `THIRD-PARTY-NOTICES.md` and the Blender extension `zStudio-Blender-extension-<version>.zip` (the extension id inside stays `zstudio_recoil`); README, CHANGELOG, CONTRIBUTING, SECURITY and the user guides are not shipped (**Help** opens the guides online at the installed version's tag). Unsigned.
- `zStudio.exe` is the .NET single-file bundle of `zStudio.Desktop`: `./tools/publish.ps1` publishes it with `-p:StudioPackage=true` (Desktop project: self-contained, single file, native libraries inside, compressed, per-assembly ReadyToRun because the SDK's composite default is larger and starts slower, the single-file analyzer off because its implicit ILLink package breaks locked restores; `Directory.Build.props`: embedded symbols) and renames it from `zStudio.Desktop.exe` (the bundle keeps its assembly name). It deletes the Desktop ReadyToRun intermediates first, so images of an earlier configuration (composite stubs) are never bundled. The icon is the checked-in `src/zStudio.Desktop/Assets/studio.ico` (the owner's artwork).
- At run time only the five WPF native libraries (about 8 MB) extract, once per build, to `%TEMP%\.net\zStudio\<bundle id>`; the managed assemblies load from the bundle and `AppContext.BaseDirectory` is the folder of `zStudio.exe`, where the app and the Blender extension find `zStudio-Blender-extension-<version>.zip`.
- `THIRD-PARTY-NOTICES.md` holds every licence text (packages with identical texts share one section) and the .NET runtime's licence and notices; a dependency update changes its row and, when its terms change, its section.
- Publishing creates `artifacts/zStudio-win-x64/` and `artifacts/zStudio-<version>-win-x64.zip`, staged below `artifacts/`: it moves the previous output aside, swaps in the new one and deletes the old folder and older ZIPs; a custom `-Output` must also be below `artifacts/`. It then runs `tools/verify-package.ps1`: the root inventory; the bundle (a format-6 bundle header, not extract-all, compressed, with `zStudio.Desktop.dll`, its `deps.json` and `runtimeconfig.json` and exactly the five WPF native libraries; no composite ReadyToRun image, symbol files, `.zbd`, `.zrd`, `.bndb`, `.pfx`, `.p12` or `.key`); the notices (every package in the bundled `deps.json` has a row of the same version, every row a shipped package, every row's link a heading of the file); the file and product version of `zStudio.exe`; `LICENSE` and the notices against the checkout; the Blender extension's version and file hashes against its sources; SHA-256 parity of every ZIP entry.
- Manual distribution check: extract into a folder with spaces, start `zStudio.exe` from an unrelated working directory with a dataset path containing spaces and installed-runtime discovery disabled; only the WPF native libraries appear under `%TEMP%\.net\zStudio`; the six RT_ICON resources match the six ICO frames.

## Local artifacts

- Keep `artifacts/zStudio-win-x64` and its ZIP the latest validated local build. It is the owner's build, which the owner may be running: launch it for the user and for live GUI/MCP work on the owner's workspace, including unreleased features; never point the user at `src/**/bin`.
- Agents' own test runs and package smoke checks never run this folder ([testing](testing.md#environment)).
- Republish after application changes before handing off or using the changed application; stop running zStudio instances first ([AGENTS.md](../../AGENTS.md#never): publishing needs no asking).
- Keep no superseded package folders or ZIPs (in `artifacts`, `.agent` or temporary folders). Reports and checksum sidecars go to the OS temporary folder. A local package refresh is not a release and changes no version.

## Maintenance checks

- Committed source, licences, notices, documentation and releases hold no game data, external research sources, credentials or local reports.
- The main and tag rules stay active and match their checked-in definitions; the required Windows check stays tied to GitHub Actions.
- Confidential vulnerability reporting stays enabled and the Security policy link works.
- Dependency alerts and updates, CODEOWNERS, fork PR checks (approval for all external contributors), issue forms and release downloads work.
- Repository, download and contribution links work anonymously.
- Wiki and Discussions stay disabled; bugs, features and compatibility requests use Issues. No workflow approves or merges PRs or changes repository visibility.
