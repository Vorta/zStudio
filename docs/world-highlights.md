# Whole world surface highlights

The three icon buttons after **Bounds** identify stored game surface properties:

| Button | Color | What it shows |
| --- | --- | --- |
| Non-default soils | Yellow | Surfaces whose material has a soil value other than zero (default). |
| CanModify | Green | Nodes marked as regions where craters/quicksand can be created. |
| ClipTo | Red | Nodes carrying the game's ClipTo flag. |

Only one highlight can be active. Click another button to switch modes, or click the checked button again to restore normal appearance. Hover a button to see its name, on/off state and description. At narrow widths the three buttons move together into the toolbar overflow.

Matching surfaces use solid colors while preserving texture holes and transparency. Unmatched surfaces retain their normal appearance. The Textures button controls normal surfaces; highlighted surfaces retain their solid colors and alpha masks. Wireframe, bounds, selection, isolation and pickup editing remain available.

Soil is a **material property**, so different surfaces on the same object can have different soil classifications. CanModify and ClipTo are **node flags**: instances sharing a model can have different flags, and a parent's flag does not automatically classify its children. These views inspect the current mission starting scene; they do not predict whether a particular gameplay action will succeed or simulate terrain deformation.

The selected mode survives difficulty, LOD, horizon and texture-variant changes, including canceled refreshes. It is remembered while switching between Whole world views in the same application session and starts off after restarting. The buttons appear only in Whole world. Highlights do not change source records, document revisions, undo history or exports.

MCP uses the same controls through `zstudio_scene_options`:

```json
{
  "preview": "<current preview UUID from zstudio_state>",
  "changes": { "highlight": "canModify" }
}
```

Valid values are `none`, `nonDefaultSoils`, `canModify` and `clipTo`. `zstudio_preview_state.highlight` reports the active mode, or `null` outside Whole world. Mode-only changes retain the preview UUID; other options that replace the scene still follow their existing preview-lifetime rules. See [MCP contract](mcp.md).

Invalid options in the same request are rejected before changing the highlight or other controls. When combined with a scene refresh, the requested highlight waits for successful publication. Failed or canceled refreshes retain the existing mode, and newer GUI highlight choices take precedence over the pending request.
