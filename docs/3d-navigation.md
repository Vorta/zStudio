# 3D navigation

Models, Whole world and animation previews share Blender-style navigation. Smooth camera inertia remains enabled. Zoom moves toward the view center, independent of the pointer position. Perspective zoom retains its distance-based approach, then continues forward past the old target instead of stopping near it. Orthographic zoom changes the view width.

Press the middle button over a visible object to orbit around the surface beneath the pointer. Picking does not move or recenter the camera: an off-center point stays at the same screen position as you orbit. The point remains fixed in world space through the drag and its inertia, even if the object animates. A press over empty space keeps the existing pivot. Hidden objects, the horizon, grid, bounds, AI overlays and editing handles do not supply orbit points. Picking uses scene triangles, including transparent materials, without per-texel alpha testing.

Pan and dolly translate the pivot along with the camera. Framing, explicit camera positioning and named views establish a view-center pivot. Ordinary perspective zoom retains the pivot during its approach; continuous forward travel advances it with the view target. Snapshots preserve the pivot and navigation scale through preview refreshes.

| Input | Action |
|---|---|
| Middle-button drag | Pick the surface under the initial press, then orbit around it |
| Shift + middle-button drag | Pan in the screen plane |
| Ctrl + middle-button drag | Zoom; down zooms in, up zooms out |
| Wheel / Numpad + or − | Zoom in/out |
| Ctrl + Shift + middle-button drag | Dolly the camera and its target forward/backward |
| Numpad 1 / 3 / 7 | Front / right / top |
| Ctrl + Numpad 1 / 3 / 7 | Back / left / bottom |
| Numpad 2 / 4 / 6 / 8 | Orbit down / left / right / up in 15° steps |
| Ctrl + Numpad 2 / 4 / 6 / 8 | Pan down / left / right / up |
| Numpad 9 | Opposite view |
| Numpad 5 | Toggle perspective/orthographic |
| Home | Frame all visible scene geometry |
| Numpad . | Frame the selected scene node, or the active asset without a node selection |
| Numpad 0 | Toggle animation Follow camera |

Use Num Lock for numeric-keypad shortcuts. The top-row digits keep their normal behavior. Shortcuts apply with the 3D surface focused or hovered, except when a text field, menu, timeline or another native input control owns the key. Animation Space transport retains its existing behavior. Named views, projection choices and framing are also available in **View → 3D Navigation**.

Axis shortcuts enter orthographic projection, which has no perspective foreshortening. Orbiting out of an axis view returns to perspective automatically. Choosing orthographic explicitly with Numpad 5 or the menu keeps that projection while orbiting. Switching projections preserves the orbit target and apparent scale at that target.

The game uses +Y up and −Z forward. Front looks from −Z, right from +X, and top from +Y. The view cube uses these same directions. Top and bottom are exactly aligned; free orbit, Fly and authored camera previews retain the upright ±89° pitch limit. Panning and zooming retain an exact axis view.

Framing uses currently visible transformed geometry, excluding the horizon, grid and editing handles. Selected nodes include their visible descendants; pickups frame their complete placed instance. Animation source nodes frame their visible runtime instances without changing sequence/event selection or time. The animation toolbar's Frame button frames the active animation; Home includes the visible mission when Map is enabled. Framing preserves orientation and projection. An unavailable selection leaves the camera in place and reports a status message.

Manual navigation leaves animation Follow camera immediately, starting from the displayed pose. Numpad 0 and the Follow camera button share the same state; reenabling follow applies the authored camera immediately when available. Navigation never edits serialized camera records or pauses playback.

Left-click selects scene geometry and retains pickup-axis editing. Middle-button gestures do not select nodes or move pickups. Right-drag camera bindings are removed. Camera gestures are blocked during pickup drags. Ordinary camera drag state is released on capture/focus loss, deactivation, preview replacement and shutdown.

Fly remains an explicitly activated mode for models and Whole world. Entering it switches to perspective at the current pose, with the existing WASD, Space/C, mouse-look, wheel-speed and Escape controls. Texture navigation is separate. Blender editing shortcuts, camera roll, local-view mode and region zoom are not implemented.

## MCP equivalents

`zstudio_camera` uses the current preview UUID and the same camera operations as the GUI. Existing `read`, `set`, `move`, `rotate` and `frame` actions remain available. The default frame target is `asset` for compatibility; `target=all` matches Home, while `target=selected` uses the shared selection or an explicit `node` index. Hidden/unavailable geometry returns `not_ready` without moving the camera.

Additional actions:

- `pan`: `horizontal` and `vertical` screen deltas in DIP.
- `rotate`: `horizontal` and `vertical` deltas, with optional `screenPoint: [x, y]` in viewport DIP to pick an orbit surface once before rotating. Omit it to keep the current pivot. Empty-space picks keep the pivot; coordinates outside the viewport are rejected. The preview must be rendered before picking.
- `zoom`: signed wheel-equivalent `steps`, positive to zoom in.
- `dolly`: signed `distance` in game units, positive forward.
- `view`: `front`, `back`, `left`, `right`, `top`, `bottom`, or `opposite`.
- `projection`: `perspective` or `orthographic`, with optional positive orthographic `width` in game units.

`set` also accepts projection and orthographic width. Readback includes Position, LookDirection, UpDirection, FieldOfView, Projection, OrthographicWidth, AxisView, AutoPerspective, OrbitPivot, NavigationReferenceDistance and FramingSelection. FieldOfView retains the perspective camera setting while orthographic is active. `preview_state` includes the expanded camera state and `framingSelection`. Animation camera following uses the existing `animation_options.changes.followCamera`.

The continuous-zoom transition is 1% of NavigationReferenceDistance, clamped to 0.01–10 game units and independent of near/far clipping. Framing, explicit positioning and a new surface pick reset that reference. Approach uses an exponential factor of `exp(-0.12 * steps)`; beyond the transition, each step moves forward by `0.12 * transition` game units. Reversing zoom increases distance from the advanced view target.

Invalid arguments are rejected before changing navigation/follow state. Camera mutations return `busy` during pickup drags; reads remain available. MCP uses semantic operations and never captures physical input. Discovery remains windowless.
