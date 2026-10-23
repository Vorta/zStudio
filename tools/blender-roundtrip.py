"""Run with Blender 5.2 --background --factory-startup --python ... -- <repo> <helper> <fixture> <output>.

Asset-free end-to-end regression. No user's Blender preferences or open scene are accessed.
"""
import json
import sys
import os
import importlib
import struct
import tomllib
from pathlib import Path

import bpy
import addon_utils

def refused(operation, **kwargs):
    try:
        result = operation(**kwargs)
    except RuntimeError:
        result = {'CANCELLED'}
    assert result == {'CANCELLED'}

repo, helper, fixture, output = map(Path, sys.argv[sys.argv.index("--") + 1:])
version = tomllib.loads((repo / "blender/zstudio_recoil/blender_manifest.toml").read_text(encoding="utf-8"))["version"]
archive = helper.parent / f"zStudio-Blender-extension-{version}.zip"
if not os.environ.get("ZSTUDIO_BLENDER_SOURCE"):
    assert archive.is_file(), f"Missing matching packaged extension: {archive}"
    assert os.environ.get("BLENDER_USER_RESOURCES"), "Installing the integration fixture requires an isolated Blender user directory"
    assert bpy.ops.extensions.package_install_files(filepath=str(archive), repo="user_default", enable_on_install=True) == {"FINISHED"}
    module = "bl_ext.user_default.zstudio_recoil"
else:
    sys.path.insert(0, str(repo / "blender"))
    module = "zstudio_recoil"
    addon_utils.enable(module, default_set=True)
addon = importlib.import_module(module)
bpy.context.preferences.addons[module].preferences.zstudio_path = str(helper)
# Carried material values must survive a no-op, but yield to resolved shader edits.
source_json = json.loads((fixture / 'part.gltf').read_text())
source_json['materials'][0]['extras']['recoil'].update(color=[255, 255, 255], opacity=255, backface=False)
(fixture / 'part.gltf').write_text(json.dumps(source_json))
assert bpy.ops.recoil.import_gltf(filepath=str(fixture / "world.gltf")) == {"FINISHED"}
_, data = addon.session(bpy.context)
world = addon.asset_collection(data, "world.gltf")
part = addon.asset_collection(data, "part.gltf")
holders = sorted((o for o in world.objects if "zs_origin" in addon.state(o)), key=lambda o: addon.state(o)["zs_origin"])
assert [o.recoil.zone_id for o in holders] == [3, 7]
refs = [o for o in world.objects if o.instance_type == "COLLECTION"]
assert len(refs) == 2 and all(o.instance_collection == part for o in refs)
assert bpy.ops.recoil.export_gltf(directory=str(output / "unchanged")) == {"FINISHED"}
# A no-op round trip leaves transforms as the source wrote them (the matrix is not rewritten as Blender's TRS).
unchanged = json.loads(next((output / "unchanged").glob("*/world.gltf")).read_text())
assert "matrix" in unchanged["nodes"][1] and "translation" not in unchanged["nodes"][1], unchanged["nodes"][1]
mesh_object = next(o for o in part.objects if o.type == "MESH" and addon.state(o)["zs_origin"] == 0)
assert mesh_object.recoil.altitudeSurface
material = mesh_object.active_material
original_values = addon.state(material)['zs_recoil']
material_output = next(n for n in material.node_tree.nodes if n.type == 'OUTPUT_MATERIAL' and n.is_active_output)
original_shader = material_output.inputs['Surface'].links[0].from_socket
shader = material.node_tree.nodes.new('ShaderNodeBsdfPrincipled')
shader.inputs['Metallic'].default_value = 0
shader.inputs['Roughness'].default_value = 1
rgb = material.node_tree.nodes.new('ShaderNodeRGB'); rgb.outputs[0].default_value = (1, 0, 0, 1)
alpha = material.node_tree.nodes.new('ShaderNodeValue'); alpha.outputs[0].default_value = .25
material.node_tree.links.new(rgb.outputs[0], shader.inputs['Base Color'])
material.node_tree.links.new(alpha.outputs[0], shader.inputs['Alpha'])
material.node_tree.links.new(shader.outputs['BSDF'], material_output.inputs['Surface'])
material.use_backface_culling = False
assert bpy.ops.recoil.export_gltf(directory=str(output / 'resolved-shader')) == {'FINISHED'}
resolved = json.loads(next((output / 'resolved-shader').glob('*/part.gltf')).read_text())
shown = next(m for m in resolved['materials'] if m.get('pbrMetallicRoughness', {}).get('baseColorFactor') == [1, 0, 0, .25])
assert shown['alphaMode'] == 'BLEND' and shown['doubleSided']
assert not any(key in shown['extras']['recoil'] for key in ('color', 'opacity', 'backface'))
# A deliberate advanced edit keeps precedence over the shader.
values = json.loads(original_values); values.update(color=[0, 255, 0], opacity=192, backface=True)
addon.state(material)['zs_recoil'] = json.dumps(values)
assert bpy.ops.recoil.export_gltf(directory=str(output / 'explicit-surface')) == {'FINISHED'}
explicit = json.loads(next((output / 'explicit-surface').glob('*/part.gltf')).read_text())
assert any(all(m.get('extras', {}).get('recoil', {}).get(k) == values[k] for k in ('color', 'opacity', 'backface')) for m in explicit['materials'])
addon.state(material)['zs_recoil'] = original_values
material.node_tree.links.new(original_shader, material_output.inputs['Surface'])
for node in (shader, rgb, alpha): material.node_tree.nodes.remove(node)
material.use_backface_culling = True
# Refused new references must remain bindable after the artist corrects the target.
new_reference = bpy.data.objects.new("new placement", None)
world.objects.link(new_reference)
new_reference.instance_type = 'COLLECTION'
artist_collection = bpy.data.collections.new('unrelated artist collection')
new_reference.instance_collection = artist_collection
refused(bpy.ops.recoil.export_gltf, directory=str(output / 'invalid-new-reference'))
assert 'zs_file' not in addon.state(new_reference) and not new_reference.recoil.reference
new_reference.instance_collection = part
assert bpy.ops.recoil.export_gltf(directory=str(output / 'corrected-new-reference')) == {'FINISHED'}
assert new_reference.recoil.reference == 'part.gltf'
bpy.data.objects.remove(new_reference, do_unlink=True)
bpy.data.collections.remove(artist_collection)
# Equal relative paths in independently imported graphs are not shared identities.
original_scene = bpy.context.scene
assert bpy.ops.recoil.import_gltf(filepath=str(fixture / 'world.gltf')) == {'FINISHED'}
_, foreign_data = addon.session(bpy.context)
foreign_part = addon.asset_collection(foreign_data, 'part.gltf')
foreign_object = next(o for o in foreign_part.objects if o.type == 'MESH')
bpy.context.window.scene = original_scene
foreign_copy = foreign_object.copy(); foreign_copy.data = foreign_object.data.copy()
part.objects.link(foreign_copy)
refused(bpy.ops.recoil.export_gltf, directory=str(output / 'foreign-object'))
foreign_mesh = foreign_copy.data
bpy.data.objects.remove(foreign_copy, do_unlink=True)
original_mesh = mesh_object.data; mesh_object.data = foreign_mesh
refused(bpy.ops.recoil.export_gltf, directory=str(output / 'foreign-mesh'))
mesh_object.data = original_mesh; bpy.data.meshes.remove(foreign_mesh)
assert not (output / 'foreign-object').exists() and not (output / 'foreign-mesh').exists()
assert bpy.ops.recoil.export_gltf(directory=str(output / 'provenance-retry')) == {'FINISHED'}

# The named editors share saved RNA values; raw node/load-root JSON is not a custom property.
assert all(not any(key in owner for key in addon.LEGACY_KEYS) for owner in [world, part, *holders, mesh_object, mesh_object.data, bpy.context.scene])
# Internal dictionaries and prepared glTF transport extras must not become artist controls.
def assert_hidden_state():
    for kind in ('objects', 'collections', 'scenes', 'meshes', 'materials', 'images', 'textures', 'node_groups'):
        for owner in getattr(bpy.data, kind):
            assert not any(key in owner for key in addon.LEGACY_KEYS), (owner.name, list(owner.keys()))
            assert 'zstudio_blender' not in owner, (owner.name, list(owner.keys()))
            if not isinstance(owner, (bpy.types.Object, bpy.types.Collection)):
                assert 'recoil' not in owner, (owner.name, list(owner.keys()))
assert_hidden_state()
mesh_object['artist_note'] = 'retain after copy and reopen'
mesh_object['zs_artist_note'] = 'not extension bookkeeping'
original_node, original_root = addon.collect(mesh_object), addon.collect(world)
for flag in addon.FLAGS:
    setattr(mesh_object.recoil, flag, True)
mesh_object.recoil.declient = 'clipTo'
mesh_object.recoil.zone_kind = 'ID'; mesh_object.recoil.zone_id = 0
world.recoil.altitudeZoneCheck = True
world.recoil.zone_kind = 'ID'; world.recoil.zone_id = 0
assert bpy.ops.recoil.export_gltf(directory=str(output / 'named-properties')) == {'FINISHED'}
named = json.loads(next((output / 'named-properties').glob('*/part.gltf')).read_text())['nodes'][0]['extras']['recoil']
assert all(named[flag] is True for flag in addon.FLAGS)
assert named['declient'] == 'clipTo' and named['zone'] == 0
named_root = json.loads(next((output / 'named-properties').glob('*/world.gltf')).read_text())['scenes'][0]['extras']['recoil']['loadRoot']
assert named_root['altitudeZoneCheck'] is True and named_root['zone'] == 0
for flag in addon.FLAGS:
    setattr(mesh_object.recoil, flag, False)
mesh_object.recoil.declient = 'canModify'
assert addon.collect(mesh_object)['declient'] == 'canModify'
mesh_object.recoil.declient = 'UNSET'; mesh_object.recoil.zone_kind = 'any'
assert bpy.ops.recoil.export_gltf(directory=str(output / 'cleared-properties')) == {'FINISHED'}
cleared = json.loads(next((output / 'cleared-properties').glob('*/part.gltf')).read_text())['nodes'][0]['extras']['recoil']
assert not any(flag in cleared for flag in addon.FLAGS) and 'declient' not in cleared and cleared['zone'] == 'any'
addon.populate(mesh_object, original_node); addon.populate(world, original_root)
# Obsolete top-level identities never become new objects, and an unrelated artist key survives.
mesh_object['zs_file'] = 'obsolete.gltf'
try:
    refused(bpy.ops.recoil.export_gltf, directory=str(output / 'obsolete'))
finally:
    del mesh_object['zs_file']
original_file = addon.state(mesh_object)['zs_file']
del addon.state(mesh_object)['zs_file']
try:
    refused(bpy.ops.recoil.export_gltf, directory=str(output / 'missing-identity'))
finally:
    addon.state(mesh_object)['zs_file'] = original_file
# Missing imported metadata must not silently become an empty object; no migration fallback.
for owner in (mesh_object, world):
    encoded = addon.state(owner)["source_metadata"]
    addon.state(owner)["source_metadata"] = ''
    try:
        addon.collect(owner)
        raise AssertionError('Missing imported metadata was accepted')
    except ValueError as error:
        assert 'fresh import' in str(error)
    finally:
        addon.state(owner)["source_metadata"] = encoded
metadata_copy = mesh_object.copy()
addon.populate(metadata_copy, dict(original_node, preserved_extra={'sentinel': [2, 9]}))
metadata_copy.recoil.landmark = True
addon.state(metadata_copy)['zs_engine_name'] = 'copy only'
assert addon.state(mesh_object)['zs_engine_name'] != 'copy only'
assert metadata_copy['artist_note'] == mesh_object['artist_note']
assert addon.collect(metadata_copy)['preserved_extra'] == {'sentinel': [2, 9]}
assert addon.collect(mesh_object) == original_node
bpy.data.objects.remove(metadata_copy)
# Outliner organization and viewport/selection restrictions must not delete source data.
child = bpy.data.collections.new("Artist organization")
part.children.link(child); child.objects.link(mesh_object); part.objects.unlink(mesh_object)
mesh_object.hide_viewport = mesh_object.hide_select = True
child.hide_viewport = True
assert bpy.ops.recoil.export_gltf(directory=str(output / "organized")) == {"FINISHED"}
organized = json.loads(next((output / "organized").glob("*/part.gltf")).read_text())
assert len(organized["nodes"]) == 2 and mesh_object.hide_viewport and mesh_object.hide_select and child.hide_viewport
mesh_object.hide_viewport = mesh_object.hide_select = child.hide_viewport = False
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
assert_hidden_state()
proxy = next(o for o in bpy.context.scene.objects if o.type == "MESH" and addon.state(o)["zs_origin"] == 0)
bpy.context.view_layer.objects.active = proxy
before_metadata = addon.source_values(proxy.data)
for advanced_target in ('MESH', 'APPEARANCE', 'MATERIAL'):
    refused(bpy.ops.recoil.advanced_properties, target=advanced_target, value='{}', bit0=True, bit3=True)
assert addon.source_values(proxy.data) == before_metadata
addon.state(proxy.data)['zs_recoil'] = json.dumps(dict(before_metadata, flags=9))
refused(bpy.ops.recoil.accept_placement)
addon.state(proxy.data)['zs_recoil'] = json.dumps(before_metadata)
before_appearance = proxy.recoil.appearance
proxy.recoil.appearance = json.dumps(dict(flags='0x00000002', alphaScale=.25))
refused(bpy.ops.recoil.accept_placement)
proxy.recoil.appearance = before_appearance
before_declient = proxy.recoil.declient
proxy.recoil.declient = 'canModify'
refused(bpy.ops.recoil.accept_placement)
proxy.recoil.declient = before_declient
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
_, reopened_data = addon.session(bpy.context)
reopened_world = addon.asset_collection(reopened_data, 'world.gltf')
reopened_part = addon.asset_collection(reopened_data, 'part.gltf')
reopened_surface = next(o for o in reopened_part.all_objects if o.recoil.engine_name == 'surface')
assert not any(key in reopened_surface or key in reopened_world for key in addon.LEGACY_KEYS)
assert_hidden_state()
assert reopened_surface['artist_note'] == 'retain after copy and reopen'
assert reopened_surface['zs_artist_note'] == 'not extension bookkeeping'
assert addon.collect(reopened_surface)['landmark'] is True
assert addon.collect(reopened_world) == original_root
assert bpy.ops.recoil.export_gltf(directory=str(output / "reopened")) == {"FINISHED"}
for receipt in output.rglob("recoil-export.json"):
    assert all(not f["path"].endswith("missing.png") for f in json.loads(receipt.read_text())["files"])
first_scene = bpy.context.scene
first_session = addon.state(first_scene)["zs_session"]
assert bpy.ops.recoil.import_gltf(filepath=str(fixture / "world.gltf")) == {"FINISHED"}
assert bpy.context.scene != first_scene and addon.state(first_scene)["zs_session"] == first_session
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
# A material/image zone draft must remain isolated through rejection and Cancel.
# Use a real PNG so Save/Reload also exercises clean-image texture publication.
import shutil
texture_fixture = output / "texture-source"
shutil.copytree(fixture, texture_fixture)
texture_json = json.loads((texture_fixture / 'part.gltf').read_text())
texture_binary = (texture_fixture / 'part.bin').read_bytes()
def fixture_vectors(values, width):
    global texture_binary
    # The source can end in 16-bit indices; FLOAT accessors start on four-byte boundaries.
    texture_binary += b'\0' * (-len(texture_binary) % 4)
    view, accessor = len(texture_json['bufferViews']), len(texture_json['accessors'])
    encoded = struct.pack('<' + str(len(values)) + 'f', *values)
    texture_json['bufferViews'].append(dict(buffer=0, byteOffset=len(texture_binary), byteLength=len(encoded)))
    texture_json['accessors'].append(dict(bufferView=view, componentType=5126, count=len(values)//width, type='VEC' + str(width)))
    texture_binary += encoded
    return accessor
cycle_primitive = texture_json['meshes'][1]['primitives'][0]
uv_count = texture_json['accessors'][cycle_primitive['attributes']['TEXCOORD_0']]['count']
cycle_primitive['attributes']['TEXCOORD_1'] = fixture_vectors([v for i in range(uv_count) for v in ((i % 2)*2, (i // 2)*2)], 2)
plain_primitive = texture_json['meshes'][0]['primitives'][0]
point_count = texture_json['accessors'][plain_primitive['attributes']['POSITION']]['count']
plain_primitive['targets'] = [dict(POSITION=fixture_vectors([0, 0, 1]*point_count, 3))]
texture_json['meshes'][0]['weights'] = [0]
texture_json['meshes'][0].setdefault('extras', {})['targetNames'] = ['Authored morph']
texture_json['buffers'][0]['byteLength'] = len(texture_binary)
(texture_fixture / 'part.bin').write_bytes(texture_binary)
(texture_fixture / 'part.gltf').write_text(json.dumps(texture_json))
image = bpy.data.images.new("fixture red", width=2, height=2)
image.pixels[:] = [1, 0, 0, 1] * 4
image.filepath_raw = str(texture_fixture / "missing.png"); image.file_format = 'PNG'; image.save()
bpy.data.images.remove(image)
assert bpy.ops.recoil.import_gltf(filepath=str(texture_fixture / "world.gltf")) == {"FINISHED"}
_, textured = addon.session(bpy.context)
textured_part = addon.asset_collection(textured, "part.gltf")
surface = next(o for o in textured_part.objects if o.type == 'MESH' and addon.state(o)["zs_origin"] == 1)
# Removing a shader image must not silently retain its named engine texture.
material = surface.active_material
shader = next(n for n in material.node_tree.nodes if n.type == 'BSDF_PRINCIPLED')
image_link = shader.inputs['Base Color'].links[0]
image_socket = image_link.from_socket
material.node_tree.links.remove(image_link)
shader.inputs['Base Color'].default_value = (1, 1, 1, 1)
refused(bpy.ops.recoil.export_gltf, directory=str(output / 'removed-image'))
assert not list((output / 'removed-image').glob('*/recoil-export.json'))
original_values = addon.state(material)['zs_recoil']
values = json.loads(original_values); del values['texture']
addon.state(material)['zs_recoil'] = json.dumps(values)
assert bpy.ops.recoil.export_gltf(directory=str(output / 'explicit-untextured')) == {'FINISHED'}
untextured = json.loads(next((output / 'explicit-untextured').glob('*/part.gltf')).read_text())
assert all('texture' not in m.get('extras', {}).get('recoil', {}) and
           'baseColorTexture' not in m.get('pbrMetallicRoughness', {}) for m in untextured['materials'])
addon.state(material)['zs_recoil'] = original_values
material.node_tree.links.new(image_socket, shader.inputs['Base Color'])
shape_object = next(o for o in textured_part.objects if o.type == 'MESH' and addon.state(o)['zs_origin'] == 0)
shape_object.data.shape_keys.key_blocks[1].mute = True
assert bpy.ops.recoil.export_gltf(directory=str(output / 'muted-shape')) == {'FINISHED'}
shape_json = json.loads(next((output / 'muted-shape').glob('*/part.gltf')).read_text())
shape_mesh = next(n['mesh'] for n in shape_json['nodes'] if n.get('name') == 'surface')
assert not shape_json['meshes'][shape_mesh]['primitives'][0].get('targets')
shape_object.data.shape_keys.key_blocks[1].mute = False
uv_signature = addon.mesh_signature(surface.data)
surface.data.uv_layers.active_index = 1
assert addon.mesh_signature(surface.data) == uv_signature
surface.data.uv_layers[1].active_render = True
assert bpy.ops.recoil.export_gltf(directory=str(output / 'render-uv')) == {'FINISHED'}
uv_json = json.loads(next((output / 'render-uv').glob('*/part.gltf')).read_text())
assert next(m['pbrMetallicRoughness']['baseColorTexture']['texCoord'] for m in uv_json['materials']
            if 'baseColorTexture' in m.get('pbrMetallicRoughness', {})) == 1
surface.data.uv_layers[0].active_render = True
uv_name = surface.data.uv_layers[1].name
surface.data.uv_layers[1].name = 'renamed UV'
assert addon.mesh_signature(surface.data) != uv_signature
surface.data.uv_layers[1].name = uv_name
texture_json['materials'][1]['pbrMetallicRoughness']['baseColorTexture']['extensions'] = {
    'KHR_texture_transform': dict(offset=[.25, .125], scale=[2, 3])}
texture_json['extensionsUsed'] = list(set(texture_json.get('extensionsUsed', [])) | {'KHR_texture_transform'})
(texture_fixture / 'part.gltf').write_text(json.dumps(texture_json))
assert bpy.ops.recoil.import_gltf(filepath=str(texture_fixture / 'world.gltf')) == {'FINISHED'}
_, textured = addon.session(bpy.context)
textured_part = addon.asset_collection(textured, 'part.gltf')
surface = next(o for o in textured_part.objects if o.type == 'MESH' and addon.state(o)['zs_origin'] == 1)
original_material = surface.active_material
original_image = next(n.image for n in original_material.node_tree.nodes if n.type == 'TEX_IMAGE')
mapping = next(n for n in original_material.node_tree.nodes if n.type == 'MAPPING')
signature = addon.material_signature(original_material)
mapping.location.x += 50; mapping.select = not mapping.select
assert addon.material_signature(original_material) == signature
mapping.vector_type = 'TEXTURE'
assert addon.material_signature(original_material) != signature
assert bpy.ops.recoil.export_gltf(directory=str(output / 'mapping-type')) == {'FINISHED'}
mapping_json = json.loads(next((output / 'mapping-type').glob('*/part.gltf')).read_text())
mapped = next(m['pbrMetallicRoughness']['baseColorTexture']['extensions']['KHR_texture_transform']
              for m in mapping_json['materials'] if 'baseColorTexture' in m.get('pbrMetallicRoughness', {}))
assert all(abs(a - b) < 1e-6 for a, b in zip(mapped['offset'], [-.125, -1/24]))
assert all(abs(a - b) < 1e-6 for a, b in zip(mapped['scale'], [.5, 1/3]))
group = bpy.data.node_groups.new('nested zone appearance', 'ShaderNodeTree')
group.nodes.new('ShaderNodeTexImage').image = original_image
group_node = original_material.node_tree.nodes.new('ShaderNodeGroup'); group_node.node_tree = group
original_signature = addon.material_signature(original_material)
holder = next(o for o in bpy.context.scene.objects if o.recoil.reference)
bpy.context.view_layer.objects.active = holder
# Geometry Nodes resources can live in node sockets, interface defaults and the
# modifier's RNA inputs. A zone draft must isolate all three, including on failure.
geometry_group = bpy.data.node_groups.new('zone geometry appearance', 'GeometryNodeTree')
geometry_group.interface.new_socket(name='Geometry', in_out='INPUT', socket_type='NodeSocketGeometry')
geometry_group.interface.new_socket(name='Geometry', in_out='OUTPUT', socket_type='NodeSocketGeometry')
material_input = geometry_group.interface.new_socket(name='Material', in_out='INPUT', socket_type='NodeSocketMaterial')
material_input.default_value = original_material
input_node = geometry_group.nodes.new('NodeGroupInput'); output_node = geometry_group.nodes.new('NodeGroupOutput')
assign_material = geometry_group.nodes.new('GeometryNodeSetMaterial')
assign_material.inputs['Material'].default_value = original_material
geometry_group.links.new(input_node.outputs['Geometry'], assign_material.inputs['Geometry'])
geometry_group.links.new(assign_material.outputs['Geometry'], output_node.inputs['Geometry'])
geometry_modifier = surface.modifiers.new('zone geometry appearance', 'NODES'); geometry_modifier.node_group = geometry_group
getattr(geometry_modifier.properties.inputs, material_input.identifier).value = original_material
placement_module = importlib.import_module(module + '.placement')
before_failure = {kind: set(getattr(bpy.data, kind)) for kind in ('objects', 'meshes', 'scenes', 'materials', 'node_groups', 'images')}
original_isolate = placement_module.isolate_appearance
def failed_isolate(*args, **kwargs):
    original_isolate(*args, **kwargs)
    if any(modifier.type == 'NODES' for modifier in args[0].modifiers):
        raise ValueError('injected placement preparation failure')
placement_module.isolate_appearance = failed_isolate
try:
    refused(bpy.ops.recoil.edit_placement)
finally:
    placement_module.isolate_appearance = original_isolate
assert all(set(getattr(bpy.data, kind)) == values for kind, values in before_failure.items())
assert bpy.ops.recoil.edit_placement() == {'FINISHED'}
addon.state(bpy.context.scene).parent_scene.name = 'Artist named source workspace'
bpy.ops.wm.save_as_mainfile(filepath=str(output / 'geometry-zone-draft.blend'))
bpy.ops.wm.open_mainfile(filepath=str(output / 'geometry-zone-draft.blend'))
assert bpy.ops.recoil.accept_placement() == {'FINISHED'}
textured_part = addon.asset_collection(textured, 'part.gltf')
surface = next(o for o in textured_part.objects if o.type == 'MESH' and addon.state(o)['zs_origin'] == 1)
original_material = surface.active_material
original_image = next(n.image for n in original_material.node_tree.nodes if n.type == 'TEX_IMAGE')
group_node = next(n for n in original_material.node_tree.nodes if n.type == 'GROUP'); group = group_node.node_tree
geometry_modifier = surface.modifiers[0]; geometry_group = geometry_modifier.node_group
material_input = next(s for s in geometry_group.interface.items_tree if s.name == 'Material')
assert bpy.ops.recoil.edit_placement() == {"FINISHED"}
proxy = next(o for o in bpy.context.scene.objects if o.type == 'MESH' and addon.state(o)["zs_origin"] == 1)
assert proxy.active_material != original_material
proxy_geometry = proxy.modifiers[0].node_group
assert proxy_geometry != geometry_group
assert next(n for n in proxy_geometry.nodes if n.bl_idname == 'GeometryNodeSetMaterial').inputs['Material'].default_value == proxy.active_material
assert next(s for s in proxy_geometry.interface.items_tree if s.identifier == material_input.identifier).default_value == proxy.active_material
assert getattr(proxy.modifiers[0].properties.inputs, material_input.identifier).value == proxy.active_material
proxy.active_material.diffuse_color = (.25, .5, .75, 1)
refused(bpy.ops.recoil.accept_placement)
proxy.active_material.diffuse_color = original_material.diffuse_color
proxy.data.uv_layers[1].active_render = True
refused(bpy.ops.recoil.accept_placement)
proxy.data.uv_layers[0].active_render = True
proxy_shader = next(n for n in proxy.active_material.node_tree.nodes if n.type == 'BSDF_PRINCIPLED')
proxy_shader.mute = True
refused(bpy.ops.recoil.accept_placement)
proxy_shader.mute = False
proxy_image = next(n.image for n in proxy.active_material.node_tree.nodes if n.type == 'TEX_IMAGE')
assert proxy_image != original_image and proxy_image.filepath != original_image.filepath
proxy_group = next(n.node_tree for n in proxy.active_material.node_tree.nodes if n.type == 'GROUP')
assert proxy_group != group and proxy_group.nodes[0].image == proxy_image
proxy_group.nodes.new('ShaderNodeRGB').outputs[0].default_value = (.5, .2, .1, 1)
proxy_image.pixels[:] = [0, 0, 1, 1] * 4
next(n for n in proxy.active_material.node_tree.nodes if n.type == 'BSDF_PRINCIPLED').inputs['Base Color'].default_value = (.25, .5, .75, 1)
try:
    result = bpy.ops.recoil.accept_placement()
except RuntimeError:
    result = {'CANCELLED'}
assert result == {'CANCELLED'}
# Linking original objects or whole source collections into a rejected draft must
# never make Cancel their owner. The private copies still get cleaned up.
bpy.context.scene.collection.objects.link(surface)
bpy.context.scene.collection.children.link(textured_part)
source_name, source_mesh = surface.name, surface.data
refused(bpy.ops.recoil.accept_placement)
assert bpy.ops.recoil.return_to_world() == {'FINISHED'}
assert bpy.data.objects.get(source_name) == surface and surface.data == source_mesh and surface.name in textured_part.objects
assert addon.material_signature(original_material) == original_signature
surface.modifiers.remove(geometry_modifier); bpy.data.node_groups.remove(geometry_group)
original_material.node_tree.nodes.remove(group_node); bpy.data.node_groups.remove(group)
original_image.pixels[:] = [0, 1, 0, 1] * 4
original_image.save(); original_image.reload()
assert not original_image.is_dirty and addon.material_signature(original_material) != original_signature
assert bpy.ops.recoil.export_gltf(directory=str(output / "saved-texture")) == {'FINISHED'}
exported_image = bpy.data.images.load(str(next((output / "saved-texture").glob('*/missing.png'))), check_existing=False)
assert exported_image.pixels[1] > .99 and exported_image.pixels[0] < .01
# Shared mesh instances retain sharing; an object material override splits only its appearance.
plain = next(o for o in textured_part.objects if o.type == 'MESH' and addon.state(o)["zs_origin"] == 0)
linked = plain.copy(); textured_part.objects.link(linked)
assert bpy.ops.recoil.export_gltf(directory=str(output / "shared-mesh")) == {'FINISHED'}
shared = json.loads(next((output / "shared-mesh").glob('*/part.gltf')).read_text())
shared_meshes = [n['mesh'] for n in shared['nodes'] if n.get('name') == 'surface']
assert len(shared_meshes) == 2 and len(set(shared_meshes)) == 1
linked.material_slots[0].link = 'OBJECT'; linked.material_slots[0].material = plain.active_material.copy()
next(n for n in linked.active_material.node_tree.nodes if n.type == 'BSDF_PRINCIPLED').inputs['Base Color'].default_value = (.1, .2, .3, 1)
assert bpy.ops.recoil.export_gltf(directory=str(output / "object-material")) == {'FINISHED'}
appearance = json.loads(next((output / "object-material").glob('*/part.gltf')).read_text())
assert any(abs(m.get('pbrMetallicRoughness', {}).get('baseColorFactor', [1])[0] - .1) < 1e-5 for m in appearance['materials'])
# Nested zone acceptance belongs to its parent draft until the outermost Accept.
nested_source = output / 'nested-source'
shutil.copytree(fixture, nested_source)
world_json = json.loads((nested_source / 'world.gltf').read_text())
middle = dict(asset=world_json['asset'], scene=0, scenes=[dict(nodes=[0], extras=world_json['scenes'][0]['extras'])],
              nodes=[dict(name='nested reference', extras=dict(recoil=dict(ref='part.gltf', zone='inherit')))])
(nested_source / 'middle.gltf').write_text(json.dumps(middle))
for node in world_json['nodes']:
    node['extras']['recoil']['ref'] = 'middle.gltf'
(nested_source / 'world.gltf').write_text(json.dumps(world_json))
assert bpy.ops.recoil.import_gltf(filepath=str(nested_source / 'world.gltf')) == {'FINISHED'}
_, nested_data = addon.session(bpy.context)
nested_world = addon.asset_collection(nested_data, 'world.gltf')
owners = list(nested_world.objects)
owner = owners[0]
source_mesh = next(o for o in addon.asset_collection(nested_data, 'part.gltf').objects if o.type == 'MESH')
bevel = source_mesh.modifiers.new('existing bevel', 'BEVEL'); bevel.width = .1
def open_nested():
    bpy.context.view_layer.objects.active = owner
    assert bpy.ops.recoil.edit_placement() == {'FINISHED'}
    bpy.context.view_layer.objects.active = next(o for o in bpy.context.scene.objects if o.recoil.reference)
    assert bpy.ops.recoil.edit_placement() == {'FINISHED'}
    return next(o for o in bpy.context.scene.objects if o.type == 'MESH' and addon.state(o).get('zs_origin') == addon.state(source_mesh)['zs_origin'])
proxy = open_nested()
inner_scene = bpy.context.scene
outer_scene = addon.state(inner_scene).parent_scene
old_outer_name = outer_scene.name
outer_scene.name = 'Artist named outer draft'
impostor_scene = bpy.data.scenes.new(old_outer_name)
bpy.context.window.scene = outer_scene
refused(bpy.ops.recoil.accept_placement)
bpy.context.window.scene = inner_scene
proxy.modifiers[0].width = .2
refused(bpy.ops.recoil.accept_placement)
proxy.modifiers[0].width = .1
constraint = proxy.constraints.new('LIMIT_LOCATION')
refused(bpy.ops.recoil.accept_placement)
proxy.constraints.remove(constraint)
proxy.recoil.zone_kind = 'ID'; proxy.recoil.zone_id = 49
assert bpy.ops.recoil.accept_placement() == {'FINISHED'}
assert bpy.context.scene == outer_scene
assert 'zs_placements' not in addon.state(owner)
assert bpy.ops.recoil.return_to_world() == {'FINISHED'}
assert 'zs_placements' not in addon.state(owner)
bpy.data.scenes.remove(impostor_scene)
proxy = open_nested(); proxy.recoil.zone_kind = 'ID'; proxy.recoil.zone_id = 49
assert bpy.ops.recoil.accept_placement() == {'FINISHED'}
bpy.context.view_layer.objects.active = next(o for o in bpy.context.scene.objects if o.recoil.reference)
assert bpy.ops.recoil.edit_placement() == {'FINISHED'}
proxy = next(o for o in bpy.context.scene.objects if o.type == 'MESH' and addon.state(o).get('zs_origin') == addon.state(source_mesh)['zs_origin'])
assert proxy.recoil.zone_id == 49
proxy.recoil.zone_id = 50
assert bpy.ops.recoil.return_to_world() == {'FINISHED'}
assert bpy.ops.recoil.accept_placement() == {'FINISHED'}
accepted = addon.state(owner)['zs_placements']
accepted_zones = [node.get('zone') for edit in json.loads(accepted) for node in edit['nodes'].values()]
assert 49 in accepted_zones and 50 not in accepted_zones and 'zs_placements' not in addon.state(owners[1])
proxy = open_nested(); proxy.recoil.zone_id = 51
assert bpy.ops.recoil.accept_placement() == {'FINISHED'}
assert bpy.ops.recoil.return_to_world() == {'FINISHED'}
assert addon.state(owner)['zs_placements'] == accepted
source_mesh.modifiers.remove(bevel)
part_collection = addon.asset_collection(nested_data, 'part.gltf')
part_collection.instance_offset = (7, 0, 0)
refused(bpy.ops.recoil.export_gltf, directory=str(output / 'unsupported-origin'))
assert not (output / 'unsupported-origin').exists()
part_collection.instance_offset = (0, 0, 0)
assert bpy.ops.recoil.export_gltf(directory=str(output / 'nested-zones')) == {'FINISHED'}
# Mesh-bearing references use a presentation-only child; unsupported changes must refuse.
reference_source = output / 'mesh-reference-source'
shutil.copytree(fixture, reference_source)
world_json = json.loads((reference_source / 'world.gltf').read_text())
part_json = json.loads((reference_source / 'part.gltf').read_text())
for key in ('meshes', 'materials', 'images', 'textures', 'samplers', 'buffers', 'bufferViews', 'accessors'):
    if key in part_json:
        world_json[key] = part_json[key]
world_json['nodes'][0]['mesh'] = 0
(reference_source / 'world.gltf').write_text(json.dumps(world_json))
assert bpy.ops.recoil.import_gltf(filepath=str(reference_source / 'world.gltf')) == {'FINISHED'}
_, reference_data = addon.session(bpy.context)
reference_world = addon.asset_collection(reference_data, 'world.gltf')
empty_holder = next(o for o in reference_world.objects if o.type == 'EMPTY' and not addon.state(o).get('zs_reference_helper'))
mesh_holder = next(o for o in reference_world.objects if o.type == 'MESH')
helper_object = next(o for o in reference_world.objects if addon.state(o).get('zs_reference_helper'))
bad_display = str(output / 'invalid-reference-display')
empty_holder.instance_type = 'NONE'
refused(bpy.ops.recoil.export_gltf, directory=bad_display)
empty_holder.instance_type = 'COLLECTION'
helper_object.location.x = 7
refused(bpy.ops.recoil.export_gltf, directory=bad_display)
helper_object.location.x = 0
duplicate_helper = helper_object.copy(); reference_world.objects.link(duplicate_helper)
refused(bpy.ops.recoil.export_gltf, directory=bad_display)
bpy.data.objects.remove(duplicate_helper, do_unlink=True)
helper_object.parent = empty_holder
refused(bpy.ops.recoil.export_gltf, directory=bad_display)
helper_object.parent = mesh_holder
target_collection = helper_object.instance_collection
unrelated_collection = bpy.data.collections.new('unrelated display target')
helper_object.instance_collection = unrelated_collection
refused(bpy.ops.recoil.export_gltf, directory=bad_display)
helper_object.instance_collection = target_collection
bpy.data.collections.remove(unrelated_collection)
reference_world.objects.unlink(helper_object)
refused(bpy.ops.recoil.export_gltf, directory=bad_display)
reference_world.objects.link(helper_object)
assert not Path(bad_display).exists()
mesh_holder.location.x = 4
helper_object.hide_viewport = helper_object.hide_select = True
bpy.ops.wm.save_as_mainfile(filepath=str(output / 'mesh-reference.blend'))
bpy.ops.wm.open_mainfile(filepath=str(output / 'mesh-reference.blend'))
assert bpy.ops.recoil.export_gltf(directory=str(output / 'mesh-reference')) == {'FINISHED'}
reference_result = json.loads(next((output / 'mesh-reference').glob('*/world.gltf')).read_text())
moved_holder = next(n for n in reference_result['nodes'] if 'mesh' in n)
assert moved_holder['translation'][0] == 4 and moved_holder['extras']['recoil']['ref'] == 'part.gltf'
# Accepted placement painting survives a shared mesh becoming single-user. The
# original and copied datablocks can be encountered in either collection order.
split_source = output / 'placement-split-source'
shutil.copytree(fixture, split_source)
split_part = json.loads((split_source / 'part.gltf').read_text())
copy_index = len(split_part['nodes'])
split_part['nodes'].append(dict(split_part['nodes'][0], name='second surface', translation=[4, 0, 0]))
split_part['scenes'][0]['nodes'].append(copy_index)
(split_source / 'part.gltf').write_text(json.dumps(split_part))
assert bpy.ops.recoil.import_gltf(filepath=str(split_source / 'world.gltf')) == {'FINISHED'}
_, split_data = addon.session(bpy.context)
split_world = addon.asset_collection(split_data, 'world.gltf')
split_part_collection = addon.asset_collection(split_data, 'part.gltf')
split_owner = next(o for o in addon.source_objects(split_world) if o.recoil.reference)
split_objects = [o for o in addon.source_objects(split_part_collection) if o.type == 'MESH' and addon.state(o).get('zs_origin') in (0, copy_index)]
assert len(split_objects) == 2 and split_objects[0].data == split_objects[1].data
original_uid = addon.state(split_objects[0].data)['zs_uid']
bpy.context.view_layer.objects.active = split_owner
assert bpy.ops.recoil.edit_placement() == {'FINISHED'}
for proxy in bpy.context.scene.objects:
    if proxy.type == 'MESH' and addon.state(proxy).get('zs_origin') in (0, copy_index):
        for value in proxy.data.attributes['_recoil_zone0'].data: value.value = 9
assert bpy.ops.recoil.accept_placement() == {'FINISHED'}
accepted = addon.state(split_owner)['zs_placements']
bpy.context.view_layer.objects.active = split_owner
assert bpy.ops.recoil.edit_shared_model() == {'FINISHED'}
split_objects[1].data = split_objects[1].data.copy()
assert bpy.ops.recoil.return_to_world() == {'FINISHED'}
# A later helper rejection must roll back both regenerated IDs and the owner in
# another physical source file; retrying then migrates the same accepted edit.
refused(bpy.ops.recoil.export_gltf, directory=str(split_source))
assert addon.state(split_owner)['zs_placements'] == accepted
assert all(addon.state(obj.data)['zs_uid'] == original_uid for obj in split_objects)
assert bpy.ops.recoil.export_gltf(directory=str(output / 'placement-split')) == {'FINISHED'}
def split_zones(folder):
    result = json.loads(next(folder.glob('*/world.gltf')).read_text())
    profile = next(n['extras']['recoil']['zoneOverrides'] for n in result['nodes'] if 'zoneOverrides' in n.get('extras', {}).get('recoil', {}))
    return sorted(mesh['polygonZones'] for mesh in profile['meshes'] if mesh['polygonZones'] and mesh['polygonZones'][0])
assert split_zones(output / 'placement-split') == [[[9]], [[9]]]
assert len({addon.state(obj.data)['zs_uid'] for obj in split_objects}) == 2
# The split identities are independent, including after saving and reopening the
# Blender project. Reopening a placement must show its accepted values as well.
bpy.ops.wm.save_as_mainfile(filepath=str(output / 'placement-split.blend'))
bpy.ops.wm.open_mainfile(filepath=str(output / 'placement-split.blend'))
_, split_data = addon.session(bpy.context)
split_owner = next(o for o in addon.source_objects(addon.asset_collection(split_data, 'world.gltf')) if 'zs_placements' in addon.state(o))
bpy.context.view_layer.objects.active = split_owner
assert bpy.ops.recoil.edit_placement() == {'FINISHED'}
proxies = [o for o in bpy.context.scene.objects if o.type == 'MESH' and addon.state(o).get('zs_origin') in (0, copy_index)]
assert all(all(value.value == 9 for value in obj.data.attributes['_recoil_zone0'].data) for obj in proxies)
for value in proxies[0].data.attributes['_recoil_zone0'].data: value.value = 11
assert bpy.ops.recoil.accept_placement() == {'FINISHED'}
assert bpy.ops.recoil.export_gltf(directory=str(output / 'placement-independent')) == {'FINISHED'}
assert split_zones(output / 'placement-independent') == [[[9]], [[11]]]
# Checks that split faces take identities never used before in their mesh (none is reused once its face is gone)
# and keep their whole ancestry, so an update maps a face split twice to the polygon it came from.
split_mesh = bpy.data.meshes.new('split ancestry')
split_mesh.from_pydata([(0, 0, 0), (1, 0, 0), (0, 1, 0), (1, 1, 0), (2, 0, 0), (2, 1, 0)], [], [(0, 1, 2), (1, 3, 2), (1, 4, 3), (4, 5, 3)])
addon.face_attribute(split_mesh, '_recoil_face', [1, 2, 3, 3])
addon.state(split_mesh)['zs_face_next'] = 4
addon.ensure_face_ids(split_mesh)
addon.face_attribute(split_mesh, '_recoil_face', [1, 2, 4, 4])
addon.ensure_face_ids(split_mesh)
assert [v.value for v in split_mesh.attributes['_recoil_face'].data] == [1, 2, 4, 5]
assert addon.face_ancestors(json.loads(addon.state(split_mesh)['zs_face_tree']), 5) == [4, 3]
assert addon.state(split_mesh)['zs_face_next'] == 6
# Faces joined in from another RECOIL mesh keep that mesh's polygon identities: the export refuses them.
addon.face_attribute(split_mesh, '_recoil_origin', [1, 2, 3, 3])
addon.state(split_mesh)['zs_mesh_tag'] = 7
addon.face_attribute(split_mesh, '_recoil_mesh', [7, 7, 7, 7])
joined = bpy.data.objects.new('joined faces', split_mesh)
addon.require_own_faces(joined)
addon.face_attribute(split_mesh, '_recoil_mesh', [7, 7, 9, 9])
try:
    addon.require_own_faces(joined)
    raise AssertionError('joined faces were accepted')
except ValueError as error:
    assert 'joined from another RECOIL mesh' in str(error)
bpy.data.objects.remove(joined)
bpy.data.meshes.remove(split_mesh)
print("ZSTUDIO_BLENDER_ROUNDTRIP_PASS")
