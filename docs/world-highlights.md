# Whole world surface highlights

The four icon buttons after **Bounds** identify stored game surface properties:

| Button | Color | What it shows |
| --- | --- | --- |
| Non-default soils | Yellow | Surfaces whose material has a soil value other than zero (default). |
| CanModify | Green | Nodes marked as regions where craters/quicksand can be created. |
| ClipTo | Red | Nodes carrying the game's ClipTo flag. |
| Zones | One color per zone, grey for any | Each surface by its node zone. |

CanModify and ClipTo can be enabled together or separately. When both are enabled, a surface carrying both flags is red (ClipTo takes precedence). Soils and Zones are alternative modes: enabling either clears the crater highlights, and enabling a crater highlight clears Soils or Zones. Clicking a checked button turns that highlight off. Hover a button to see its name, on/off state and description. At narrow widths the four buttons move together into the toolbar overflow.

Matching surfaces blend their normal RGB equally with the diagnostic color, preserving textures, vertex/material colors, texture holes and transparency. Unmatched surfaces retain their normal appearance. The Textures button also applies to highlighted surfaces: with textures off, the tint blends over the ordinary untextured appearance. Face orientation uses the same 50% blend and temporarily takes precedence over these highlights. Wireframe, bounds, selection, isolation and pickup editing remain available.

Soil is a **material property**, so different surfaces on the same object can have different soil classifications. CanModify and ClipTo are **node flags**: instances sharing a model can have different flags, and a parent's flag does not automatically classify its children. These views inspect the current mission starting scene; they do not predict whether a particular gameplay action will succeed or simulate terrain deformation.

The selected mode survives difficulty, LOD, horizon and texture-variant changes, including canceled refreshes. It is remembered while switching between Whole world views in the same application session and starts off after restarting. The buttons appear only in Whole world. Highlights do not change source records, document revisions, undo history or exports.

MCP uses the same controls through `zstudio_scene_options`:

```json
{
  "preview": "<current preview UUID from zstudio_state>",
  "changes": { "highlight": "canModify" }
}
```

Valid values are `none`, `nonDefaultSoils`, `canModify`, `clipTo`, `canModifyAndClipTo` and `zones`. `zstudio_preview_state.highlight` reports the active mode, or `null` outside Whole world. Mode-only changes retain the preview UUID; other options that replace the scene still follow their existing preview-lifetime rules. See [MCP contract](mcp.md).

Invalid options in the same request are rejected before changing the highlight or other controls. When combined with a scene refresh, the requested highlight waits for successful publication. Failed or canceled refreshes retain the existing mode, and newer GUI highlight choices take precedence over the pending request.
