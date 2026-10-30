# 3D navigation

Every 3D view (maps, models and animation previews) uses the same Blender-style controls. The game uses +Y up and −Z forward.

## Mouse and keyboard

| Input | Action |
| --- | --- |
| Middle-button drag | Orbit around the surface under the pointer |
| **Shift** + middle-button drag | Pan |
| **Ctrl** + middle-button drag | Zoom (down zooms in) |
| Wheel / **Numpad +** or **−** | Zoom toward the pointer |
| **Ctrl** + **Shift** + middle-button drag | Dolly the camera and its target |
| **Numpad 1** / **3** / **7** | Front / right / top view |
| **Ctrl** + **Numpad 1** / **3** / **7** | Back / left / bottom view |
| **Numpad 2** / **4** / **6** / **8** | Orbit down / left / right / up in 15° steps |
| **Ctrl** + **Numpad 2** / **4** / **6** / **8** | Pan |
| **Numpad 9** | Opposite view |
| **Numpad 5** | Switch perspective / orthographic |
| **Home** | Frame everything visible (in Animation mode and a compiled animation preview, **Home** and **End** go to the playback range's start and end; frame with the Camera group's **Frame all**, **Frame** in a compiled preview) |
| **Numpad .** | Frame the selection |
| **Numpad 0** | Switch animation Follow camera |
| Left click | Select |

Zooming moves toward the pointer at a speed set by the surface under it: point at distant geometry to travel fast, at nearby geometry to inspect closely. You can press or release **Shift** and **Ctrl** during a middle-button drag to switch between orbit, pan, zoom and dolly. Use Num Lock for the numeric keypad; the shortcuts work while the view is focused or under the pointer, except when a text field or menu has the key.

## Fly camera

Choose **Flycam** (in a map's Camera group; **Fly camera** in a compiled model or Whole world preview) to capture the mouse and keyboard for free flight. Animation previews have no fly camera. A banner shows the speed and **Exit**.

| Input | Action |
| --- | --- |
| **W** / **S** | Forward / backward along the view |
| **A** / **D** | Strafe left / right |
| **Space** / **C** | Up / down |
| Move the mouse | Look around |
| Wheel | Change speed |
| **Escape** | Return to normal controls |

The camera passes through geometry and keeps its speed near and far from surfaces. Switching to another application or replacing the view also ends flight.

## Views

Axis views (**Numpad 1**, **3**, **7** and their opposites) switch to orthographic projection; orbiting out of one returns to perspective. Orthographic chosen with **Numpad 5** stays while you orbit. Cameras stay upright: tilt stops at ±89°, and only the top and bottom views look straight down or up. Framing uses the visible geometry and keeps the view's direction and projection; a pickup or vehicle frames as its whole placement. In a map the **Perspective** chip offers the projections, named views and framing.

Manual navigation leaves animation Follow camera at the shown pose; **Numpad 0** or the Follow camera button returns to the authored camera.

## Hide UI and fullscreen

**F10** (**View → Hide UI**) hides everything but the 3D view: menus, panes, status bar, banners, the Properties window and editor guides such as the grid, axes, bounds, handles, AI nodes, animation paths and drop-window markers. Display choices such as wireframe, textures and overlays stay. While the UI is hidden, clicks select and edit nothing, but navigation and Flycam work. **F10** again shows everything as it was; leaving the 3D view does too.

**F11** (**View → Fullscreen**) fills the monitor. Together, F10 and F11 give the view alone on the whole screen, for footage. Neither is remembered at startup, and neither changes the camera, playback, selection or pending edits.

MCP: `zstudio_camera` (orbit, pan, zoom, dolly, views, projection, framing), `zstudio_presentation` (Hide UI, fullscreen).
