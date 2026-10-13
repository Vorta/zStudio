"""Run with Blender 5.2 --background --factory-startup --python ... -- <repo> <helper> <fixture> <output>.

Asset-free end-to-end regression. No user's Blender preferences or open scene are accessed.
"""
import json
import sys
import os
import importlib
from pathlib import Path

import bpy
import addon_utils

repo, helper, fixture, output = map(Path, sys.argv[sys.argv.index("--") + 1:])
archive = helper.parent / "dependencies/blender/zstudio_recoil-0.1.0.zip"
if archive.is_file():
    assert os.environ.get("BLENDER_USER_RESOURCES"), "Installing the integration fixture requires an isolated Blender user directory"
    assert bpy.ops.extensions.package_install_files(filepath=str(archive), repo="user_default", enable_on_install=True) == {"FINISHED"}
    module = "bl_ext.user_default.zstudio_recoil"
else:
    sys.path.insert(0, str(repo / "blender"))
    module = "zstudio_recoil"
    addon_utils.enable(module, default_set=True)
addon = importlib.import_module(module)
bpy.context.preferences.addons[module].preferences.zstudio_path = str(helper)
assert bpy.ops.recoil.import_gltf(filepath=str(fixture / "world.gltf")) == {"FINISHED"}
_, data = addon.session(bpy.context)
world = addon.asset_collection(data, "world.gltf")
part = addon.asset_collection(data, "part.gltf")
holders = sorted((o for o in world.objects if "zs_origin" in o), key=lambda o: o["zs_origin"])
assert [o.recoil.zone_id for o in holders] == [3, 7]
refs = [o for o in world.objects if o.instance_type == "COLLECTION"]
assert len(refs) == 2 and all(o.instance_collection == part for o in refs)
assert bpy.ops.recoil.export_gltf(directory=str(output / "unchanged")) == {"FINISHED"}
mesh_object = next(o for o in part.objects if o.type == "MESH" and o["zs_origin"] == 0)
assert mesh_object.recoil.altitudeSurface
mesh_object.data.vertices[0].co.x += 0.125
assert bpy.ops.recoil.export_gltf(directory=str(output / "quad")) == {"FINISHED"}
quad = json.loads(next((output / "quad").glob("*/part.gltf")).read_text())
quad_mesh = next(n["mesh"] for n in quad["nodes"] if n.get("name") == "surface")
assert len(quad["meshes"][quad_mesh]["primitives"][0]["extras"]["recoil"]["polygons"]) == 1
mesh_object.data.vertices[0].co.x -= 0.125
holders[0].recoil.zone_id = 4
mesh_object.recoil.landmark = True
assert bpy.ops.recoil.export_gltf(directory=str(output / "flags")) == {"FINISHED"}
flags_part = json.loads(next((output / "flags").glob("*/part.gltf")).read_text())
assert flags_part["nodes"][0]["extras"]["recoil"]["landmark"] is True
bpy.context.view_layer.objects.active = holders[1]
holders[1].select_set(True)
assert bpy.ops.recoil.edit_placement() == {"FINISHED"}
proxy = next(o for o in bpy.context.scene.objects if o.type == "MESH" and o["zs_origin"] == 0)
proxy.data.attributes["_recoil_zone0"].data[0].value = 9
assert bpy.ops.recoil.accept_placement() == {"FINISHED"}
assert bpy.ops.recoil.export_gltf(directory=str(output / "placement")) == {"FINISHED"}
generation = sorted((output / "placement").glob("*/recoil-export.json"))[-1].parent
world_json = json.loads((generation / "world.gltf").read_text())
assert "zoneOverrides" not in world_json["nodes"][0]["extras"]["recoil"]
profile = world_json["nodes"][1]["extras"]["recoil"]["zoneOverrides"]
assert profile["meshes"][0]["polygonZones"] == [[9], [3]]
mesh_object.data.vertices[0].co.x += 0.25
assert bpy.ops.recoil.export_gltf(directory=str(output / "geometry")) == {"FINISHED"}
# New geometry has no retail provenance, but its placement zones still have an
# editor identity. A duplicate reference remains a reference to the same file.
new_mesh = bpy.data.meshes.new("new surface")
new_mesh.from_pydata([(0, 0, 0), (1, 0, 0), (0, 1, 0)], [], [(0, 1, 2)])
new_mesh.materials.append(mesh_object.data.materials[0])
new_object = bpy.data.objects.new("new surface", new_mesh)
part.objects.link(new_object)
duplicate = holders[0].copy()
world.objects.link(duplicate)
bpy.context.view_layer.objects.active = duplicate
assert bpy.ops.recoil.edit_placement() == {"FINISHED"}
new_proxy = next(o for o in bpy.context.scene.objects if o.name.startswith("new surface"))
new_proxy.recoil.zone_kind = "ID"; new_proxy.recoil.zone_id = 12
new_proxy.data.attributes["_recoil_count"].data[0].value = 1
new_proxy.data.attributes["_recoil_zone0"].data[0].value = 13
assert bpy.ops.recoil.accept_placement() == {"FINISHED"}
assert bpy.ops.recoil.export_gltf(directory=str(output / "new-geometry")) == {"FINISHED"}
new_world = json.loads(next((output / "new-geometry").glob("*/world.gltf")).read_text())
new_part = json.loads(next((output / "new-geometry").glob("*/part.gltf")).read_text())
new_profile = new_world["nodes"][2]["extras"]["recoil"]["zoneOverrides"]
assert any(n["zone"] == 12 for n in new_profile["nodes"])
assert any(m["polygonZones"] == [[13]] for m in new_profile["meshes"])
assert len(new_part["nodes"]) == 3 and len(new_world["nodes"]) == 3
bpy.ops.wm.save_as_mainfile(filepath=str(output / "roundtrip.blend"))
# Force recovery from the packed baseline without deleting the live import cache.
text, saved = addon.session(bpy.context)
saved["folder"] = str(output / "absent-cache")
text.clear(); text.write(json.dumps(saved))
bpy.ops.wm.save_as_mainfile(filepath=str(output / "roundtrip.blend"))
bpy.ops.wm.open_mainfile(filepath=str(output / "roundtrip.blend"))
assert bpy.ops.recoil.export_gltf(directory=str(output / "reopened")) == {"FINISHED"}
for receipt in output.rglob("recoil-export.json"):
    assert all(not f["path"].endswith("missing.png") for f in json.loads(receipt.read_text())["files"])
first_scene = bpy.context.scene
first_session = first_scene["zs_session"]
assert bpy.ops.recoil.import_gltf(filepath=str(fixture / "world.gltf")) == {"FINISHED"}
assert bpy.context.scene != first_scene and first_scene["zs_session"] == first_session
_, second = addon.session(bpy.context)
for obj in list(addon.asset_collection(second, "part.gltf").objects):
    bpy.data.objects.remove(obj, do_unlink=True)
assert bpy.ops.recoil.export_gltf(directory=str(output / "empty-part")) == {"FINISHED"}
assert not json.loads(next((output / "empty-part").glob("*/part.gltf")).read_text()).get("nodes")
for obj in list(addon.asset_collection(second, "world.gltf").objects):
    bpy.data.objects.remove(obj, do_unlink=True)
assert bpy.ops.recoil.export_gltf(directory=str(output / "removed-references")) == {"FINISHED"}
removed = json.loads(next((output / "removed-references").glob("*/recoil-export.json")).read_text())
assert {m["path"] for m in removed["models"]} == {"world.gltf", "part.gltf"}
assert next((output / "removed-references").glob("*/part.gltf")).is_file()
bpy.context.window.scene = first_scene
assert addon.session(bpy.context)[1]["id"] == first_session
print("ZSTUDIO_BLENDER_ROUNDTRIP_PASS")
