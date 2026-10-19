# Whole world surface highlights

The six icon buttons after **Bounds** identify stored game surface properties. In a source map the command bar's **Overlay** group holds the others, while Object zones and Polygon zones are the World tree's **Zones** row: each zone's colour is shown on its own with its eye (all start hidden; any shown turns the overlay on), the row's master eye shows or hides every zone and its **Objects | Polygons** switch chooses the kind; another overlay hides every zone, and a selected shown zone is drawn strong, the others faint ([source map editor](scene-inspection.md#source-map-editor)):

| Button | Color | What it shows |
| --- | --- | --- |
| Non-default soils | Yellow | Surfaces whose material has a soil value other than zero (default). |
| CanModify | Green | Nodes marked as regions where craters/quicksand can be created. |
| ClipTo | Red | Nodes carrying the game's ClipTo flag. |
| Object zones | One color per zone, grey for any | Each surface by its node (object) zone: the zone that decides whether the renderer draws it. |
| Polygon zones | One color per zone, grey for any; diagonal stripes of each zone where a polygon has two or three | Each polygon by its own zone set: the zones it makes current when the camera or a vehicle is on it. Polygons without zones keep their normal appearance. |
| Landmarks | Magenta | Landmark nodes (flag 0x80: the engine never culls them) and everything below a landmark object. The horizon is a landmark too but stays untinted. |

CanModify and ClipTo can be enabled together or separately. When both are enabled, a surface carrying both flags is red (ClipTo takes precedence). Soils, Object zones, Polygon zones and Landmarks are alternative modes: enabling one clears the crater highlights and the others, and enabling a crater highlight clears them. Clicking a checked button turns that highlight off. In the compiled toolbar, hover a button to see its name, on/off state and description; a source map's Overlay buttons have short fixed tooltips. In the compiled Whole world toolbar, the six buttons move together into the toolbar overflow at narrow widths.

Matching surfaces blend their normal RGB equally with the diagnostic color, preserving textures, vertex/material colors, texture holes and transparency; Polygon zones instead replaces vertex colours with its zone encoding (RECOIL source meshes have none). Unmatched surfaces retain their normal appearance. The Textures button also applies to highlighted surfaces: with textures off, the tint blends over the ordinary untextured appearance. Face orientation uses the same 50% blend and temporarily takes precedence over these highlights. Wireframe (light polygon outlines on a dark halo over the surfaces), bounds, selection, isolation and pickup editing remain available.

Soil is a **material property**, so different surfaces on the same object can have different soil classifications. CanModify and ClipTo are **node flags**: instances sharing a model can have different flags, and a parent's flag does not automatically classify its children. Landmark is the exception: a landmark object's geometry is drawn with it, so Landmarks also highlights what lies below one. These views inspect the current mission starting scene; they do not predict whether a particular gameplay action will succeed or simulate terrain deformation.

The selected mode survives difficulty, LOD, horizon and texture-variant changes, including canceled refreshes. It is remembered while switching between Whole world views in the same application session and starts off after restarting. The buttons appear only in Whole world (a source map's Overlay group). Highlights do not change source records, document revisions, undo history or exports.

MCP uses the same controls through `zstudio_scene_options`:

```json
{
  "preview": "<current preview UUID from zstudio_state>",
  "changes": { "highlight": "canModify" }
}
```

Valid values are `none`, `nonDefaultSoils`, `canModify`, `clipTo`, `canModifyAndClipTo`, `zones` (object zones), `polygonZones` and `landmarks`. `zstudio_preview_state.highlight` reports the active mode, or `null` outside Whole world. Mode-only changes retain the preview UUID; other options that replace the scene still follow their existing preview-lifetime rules. See [MCP contract](mcp.md).

Invalid options in the same request are rejected before changing the highlight or other controls. When combined with a scene refresh, the requested highlight waits for successful publication. Failed or canceled refreshes retain the existing mode, and newer GUI highlight choices take precedence over the pending request.
