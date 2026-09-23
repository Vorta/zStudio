# Changelog

Version numbers follow `MAJOR.MINOR.PATCH`; the 0.x series is under active development. Release tags and downloadable archives use the same version.

## Unreleased

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
