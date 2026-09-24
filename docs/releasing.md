# Releases and repository maintenance

## Repository protections

Vorta/zStudio is a public open-source repository. Normal development uses feature branches, maintainer review and passing CI before a squash merge.

The maintainer uses the authenticated GitHub connection for issues, PR reviews and merges, and GitHub CLI for repository settings/releases. Do not put access tokens in the repository or workflow files. CI uses its short-lived GitHub token.

Active branch and tag rules are checked in under `.github/rulesets/`. The main rule requires the GitHub Actions check **Windows build and tests**, an up-to-date base branch, a PR and resolved conversations; it prevents deletion and force pushes. Rules apply without bypass actors. The approval count is zero because Vorta is the sole maintainer, including authenticated assistant work; requiring another approving account would block the owner's own PRs. Review remains the maintainer's responsibility. Squash is the only merge method and merged feature branches are deleted automatically.

Release tags matching `v*` cannot be deleted or force-updated. New release tags can be created after the version change passes CI and merges to main. These rules preserve the source identity of each downloadable release.

Dependency alerts/security update PRs and confidential vulnerability reporting are enabled. Workflow tokens default to read-only and workflows cannot approve PRs. Fork workflows require maintainer approval for all external contributors, allowing proposed workflow changes to be checked before they run. Only the release publication job receives release-write permission.

## Cutting a release

1. Update the version in `Directory.Build.props` and add its dated entry to `CHANGELOG.md`. Update the README download version and applicable guides. Use `MAJOR.MINOR.PATCH`, or an explicit prerelease suffix for previews.
2. Run Release build/tests and the relevant local corpus/UI checks. The CI check also publishes and validates a portable package from a clean Windows runner without game assets. Original-game compatibility still requires separate validation.
3. Merge the reviewed version change to `main` after CI passes. Fetch current `main` and create/push its annotated version tag, for example:

   ```powershell
   git switch main
   git pull --ff-only
   git tag -a v0.3.0 -m 'zStudio 0.3.0'
   git push origin v0.3.0
   ```

4. The Release workflow checks tag/version equality and main ancestry, restores locked dependencies, builds, tests and packages. A separate job with release-write permission uploads the verified ZIP and `SHA256SUMS` to GitHub Releases. Changelog text supplies the notes. Prerelease version suffixes create prereleases; ordinary versions become the latest release.
5. Download and verify the published ZIP; launch its executable on Windows. Record actual verification results. Do not move release tags or replace published assets: fixes receive a new version. A failed unpublished workflow can be rerun after resolving an external failure; source/workflow changes require a new version once a release exists.

The first zStudio release is 0.2.17. Earlier Recoil ZBD Studio builds were local development versions with no reproducible Git release history and are not recreated.

## Portable layout and local packaging

Run `./tools/publish.ps1`. It creates `artifacts/zStudio-win-x64/` and `artifacts/zStudio-<version>-win-x64.zip`, verifies file versions, relative apphost binding, and SHA-256 parity of every ZIP file. The archive root contains exactly `zStudio.exe` and `dependencies/`. Managed/native runtime files, MIT license, third-party notices and documentation are inside dependencies. The archive is self-contained and unsigned.

The executable remains the SDK apphost bound to `dependencies/Recoil.Zbd.Studio.dll`. Internal assembly names and `%LOCALAPPDATA%/RecoilZbdStudio` settings/error logs remain stable across the rename. The icon uses the owner's updated artwork at the existing checked-in resource path. A previous portable directory is retained as a backup during packaging; recycle superseded output only after verifying its replacement. Keep the current folder and ZIP in artifacts, with reports/checksum sidecars in a temporary directory.

The package verifier can be run independently:

```powershell
./tools/verify-package.ps1 -Directory artifacts/zStudio-win-x64 -Archive artifacts/zStudio-0.3.0-win-x64.zip -ExpectedVersion 0.3.0
```

## Maintenance checks

- Inspect committed source, license/notices, documentation and releases. Verify no game data, external research sources, credentials or local reports are included.
- Confirm the main/tag rules remain active and match their checked-in definitions. Keep the required Windows check associated with GitHub Actions.
- Keep confidential vulnerability reporting enabled and verify the Security policy reporting link works.
- Confirm dependency alerts/updates, CODEOWNERS, fork PR checks, issue forms and release downloads work. Fork PR workflows use `pull_request` with read-only permissions and no repository secrets; do not replace this with privileged execution of fork code.
- Keep fork workflow approval set to **all external contributors**.
- Verify repository, download and contribution links anonymously.

Wiki and Discussions are disabled; bugs, feature requests and compatibility proposals use Issues. No workflow automatically approves or merges PRs or changes repository visibility.
