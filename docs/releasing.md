# Releases and repository maintenance

## Initial private review

Vorta/zStudio is initially **private**. The owner alone decides when to make it public; neither scripts nor workflows change visibility. Releases, source and issue/PR discussions follow repository access. Normal development uses feature branches, maintainer review and passing CI before a squash merge.

The maintainer uses the authenticated GitHub connection for issues, PR reviews and merges, and GitHub CLI for repository settings/releases. Do not put access tokens in the repository or workflow files. CI uses its short-lived GitHub token. The account's existing GitHub Actions limits apply during private review.

Desired branch and tag rules are checked in under `.github/rulesets/`. The main rule requires the GitHub Actions check **Windows build and tests**, current base branch, a PR and resolved conversations; it prevents deletion/force pushes. The initial approval count is zero because Vorta is the sole maintainer, including authenticated assistant work. Review remains the maintainer's responsibility. Squash is the only merge method and merged feature branches are deleted automatically.

Private repository rulesets require an eligible GitHub plan. If GitHub rejects private enforcement, keep the desired definitions and use the same CI/review workflow manually until the owner converts the repository to public. Do not claim rules are enforced until the repository API confirms it. CODEOWNERS review requests and other private features can also depend on the account plan.

Initial setup verification on 2026-09-23: Vorta has administrator access through both GitHub CLI and the connected integration; the repository is private and anonymous requests return 404. Dependency alerts/security update PRs are enabled, workflow tokens default to read-only, and workflows cannot approve PRs. GitHub rejected private branch/tag rules with an upgrade-or-make-public response, so those rules are **not currently enforced**. GitHub also rejected the public fork-contributor approval policy while the repository is private. Both are explicit public-launch follow-ups; no account upgrade or visibility change was made. CODEOWNERS has no reported parsing errors.

## Cutting a release

1. Update the version in `Directory.Build.props` and add its dated entry to `CHANGELOG.md`. Update the README download version and applicable guides. Use `MAJOR.MINOR.PATCH`, or an explicit prerelease suffix for previews.
2. Run Release build/tests and the relevant local corpus/UI checks. The CI check also publishes and validates a portable package from a clean Windows runner without game assets. Original-game compatibility still requires separate validation.
3. Merge the reviewed version change to `main` after CI passes. Fetch current `main` and create/push its annotated version tag, for example:

   ```powershell
   git switch main
   git pull --ff-only
   git tag -a v0.2.17 -m 'zStudio 0.2.17'
   git push origin v0.2.17
   ```

4. The Release workflow checks tag/version equality and main ancestry, restores locked dependencies, builds, tests and packages. A separate job with release-write permission uploads the verified ZIP and `SHA256SUMS` to GitHub Releases. Changelog text supplies the notes. Prerelease version suffixes create prereleases; ordinary versions become the latest release.
5. Download and verify the published ZIP; launch its executable on Windows. Record actual verification results. Do not move release tags or replace published assets: fixes receive a new version. A failed unpublished workflow can be rerun after resolving an external failure; source/workflow changes require a new version once a release exists.

The first zStudio release is 0.2.17. Earlier Recoil ZBD Studio builds were local development versions with no reproducible Git release history and are not recreated.

## Portable layout and local packaging

Run `./tools/publish.ps1`. It creates `artifacts/zStudio-win-x64/` and `artifacts/zStudio-<version>-win-x64.zip`, verifies file versions, relative apphost binding, and SHA-256 parity of every ZIP file. The archive root contains exactly `zStudio.exe` and `dependencies/`. Managed/native runtime files, MIT license, third-party notices and documentation are inside dependencies. The archive is self-contained and unsigned.

The executable remains the SDK apphost bound to `dependencies/Recoil.Zbd.Studio.dll`. Internal assembly names and `%LOCALAPPDATA%/RecoilZbdStudio` settings/error logs remain stable across the rename. The icon uses the owner's updated artwork at the existing checked-in resource path. A previous portable directory is retained as a backup during packaging; recycle superseded output only after verifying its replacement. Keep the current folder and ZIP in artifacts, with reports/checksum sidecars in a temporary directory.

The package verifier can be run independently:

```powershell
./tools/verify-package.ps1 -Directory artifacts/zStudio-win-x64 -Archive artifacts/zStudio-0.2.17-win-x64.zip -ExpectedVersion 0.2.17
```

## Owner's public-launch checklist

- Inspect the committed source, license/notices, documentation, issue forms and first release. Verify no game data, private research sources, credentials or local reports are included.
- Make the repository public yourself in GitHub's visibility settings when satisfied.
- Confirm the main/tag rules are active; if private enforcement was unavailable, import the checked-in rulesets now. Confirm the required Actions check has run and is associated with GitHub Actions.
- Enable private vulnerability reporting under repository security settings, and verify the Security policy reporting link works before inviting public reports.
- Confirm dependency alerts/updates, CODEOWNERS, fork PR checks, issue forms and release downloads work. Fork PR workflows use `pull_request` with read-only permissions and no repository secrets; do not replace this with privileged execution of fork code.
- Set fork workflow approval to **all external contributors** after the visibility change. The setting is unavailable during private review; approval lets a maintainer check proposed workflow changes before running them.
- Remove the temporary private-review note from the README through a PR. Verify the download and contribution links anonymously.

Wiki and Discussions start disabled; bugs, feature requests and compatibility proposals use Issues. No workflow automatically approves/merges PRs or changes repository visibility.
