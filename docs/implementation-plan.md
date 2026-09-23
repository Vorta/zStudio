# Explorer milestone

Native portable Windows 11 x64 application. C# 14 / .NET 10 / WPF; Direct3D model and whole-world previews; fixed resizable panes and document tabs. Browse and export all five ZBD families in both supplied datasets. Texture channels/palettes, WAV playback/waveforms, ZRD trees, prepared scripts, animation events/keyframes, model/material/node inspection. Export PNG, original members/WAV, script text, JSON, OBJ/MTL and textures. Static worlds; no gameplay or effects simulation.

Core readers use structure/version detection and file-plus-record identities, retain source ranges/unknown data, validate bounds, and report local failures. Decode on demand and cancel obsolete work. Export safely outside original trees. The former Python CLI is archived separately; it is not a build/runtime dependency or promised export-schema consumer. This document records the original explorer milestone; see animation-editor.md for the subsequent editor and preview.

Sequence: solution and guidance; readers and tests; explorer/exports; previews and assembled scenes; corpus/visual validation; self-contained release. Acceptance covers 160 ZBDs, all entries, malformed inputs, reference resolution, independent export consumers, responsiveness, high DPI, and source preservation.

Later editing: in-memory changes, dirty documents, validated per-family writers, and Save As per file. No project format. Only expose editing commands when implemented and verified.
