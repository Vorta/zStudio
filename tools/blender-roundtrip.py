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
assert addon.source_metadata({'recoil': {'legacy': 1}}) == {'legacy': 1}
assert addon.source_metadata({'zstudio': {}, 'recoil': {'legacy': 1}}) == {}
assert addon.source_metadata({'zstudio': {'selected': 2}, 'recoil': {'selected': 1}}) == {'selected': 2}
assert addon.canonical_extras({'recoil': {'legacy': 1}, 'artist': 3}) == {'zstudio': {'legacy': 1}, 'artist': 3}
color_probe = bpy.data.meshes.new('vertex color change probe')
color_probe.from_pydata([(0, 0, 0), (1, 0, 0), (0, 1, 0)], [], [(0, 1, 2)])
color_layer = color_probe.color_attributes.new(name='Color', type='FLOAT_COLOR', domain='CORNER')
color_before = addon.mesh_signature(color_probe)
color_layer.data[0].color = (0, 1, 0, 1)
assert addon.mesh_signature(color_probe) != color_before, 'Vertex painting must trigger geometry export'
bpy.data.meshes.remove(color_probe)
bpy.context.preferences.addons[module].preferences.zstudio_path = str(helper)
if os.environ.get('ZSTUDIO_BLENDER_RECREATE'):
    assert bpy.ops.recoil.import_gltf(filepath=str(fixture)) == {'FINISHED'}
    _, data = addon.session(bpy.context)
    collection = addon.asset_collection(data, data['root'])
    output.mkdir(parents=True, exist_ok=True)
    (output / 'session-input.json').write_text((Path(data['folder']) / 'session.json').read_text(), encoding='utf-8')
    recipe_path = Path(os.environ['ZSTUDIO_BLENDER_RECREATE_RECIPE'])
    if os.environ['ZSTUDIO_BLENDER_RECREATE'] == 'delete':
        original = json.loads(fixture.read_text())
        recipes = []
        for name in ('sam_01', 'tur_101'):
            index = next(i for i, n in enumerate(original['nodes']) if n['name'] == name)
            node = original['nodes'][index]
            parent = next(i for i, n in enumerate(original['nodes']) if index in n.get('children', []))
            child = original['nodes'][node['children'][0]]
            recipes.append(dict(name=name, parent=original['nodes'][parent]['name'], position=original['nodes'][parent]['children'].index(index),
                transform={k: node[k] for k in ('matrix', 'translation', 'rotation', 'scale') if k in node},
                properties=node['extras']['zstudio'], child_name=child['name'], child_properties=child['extras']['zstudio']))
            obj = next(o for o in addon.source_objects(collection) if addon.state(o).get('zs_origin') == index)
            for removed in list(obj.children_recursive) + [obj]: bpy.data.objects.remove(removed, do_unlink=True)
        recipe_path.write_text(json.dumps(recipes))
    elif os.environ['ZSTUDIO_BLENDER_RECREATE'] == 'add':
        import posixpath
        for row in json.loads(recipe_path.read_text()):
            parent = next(o for o in addon.source_objects(collection) if o.recoil.engine_name == row['parent'])
            bpy.context.view_layer.objects.active = parent
            model = posixpath.normpath(posixpath.join(posixpath.dirname(data['root']), row['child_properties']['ref']))
            assert bpy.ops.recoil.add_model_reference(model=model, placement_name=row['name'], reference_name=row['child_name'],
                sibling_index=row['position'], transform=json.dumps(row['transform']), properties=json.dumps(row['properties']),
                reference_properties=json.dumps(row['child_properties'])) == {'FINISHED'}
    else:
        terrain = next(o for o in addon.source_objects(collection) if o.recoil.engine_name == 'terrain')
        donor = next(o for o in addon.source_objects(collection) if o.recoil.engine_name == 'morph donor')
        assert addon.state(terrain.data)['zs_origin'] == addon.state(donor.data)['zs_origin']
        terrain.data = donor.data
        bpy.data.objects.remove(donor, do_unlink=True)
    assert bpy.ops.recoil.export_gltf(directory=str(output)) == {'FINISHED'}
    if os.environ['ZSTUDIO_BLENDER_RECREATE'] == 'morph':
        result = json.loads(next(output.glob('*/data/models/world.gltf')).read_text())
        node = next(n for n in result['nodes'] if n['name'] == 'terrain')
        assert node.get('weights', result['meshes'][node['mesh']].get('weights', [0])) == [.75]
    if os.environ['ZSTUDIO_BLENDER_RECREATE'] == 'add' and os.environ.get('ZSTUDIO_BLENDER_RECREATE_CHECKS'):
        sam = next(o for o in addon.source_objects(collection) if o.recoil.engine_name == 'sam_01')
        sam.location.x += 1
        terrain = next(o for o in addon.source_objects(collection) if o.type == 'MESH')
        values = addon.source_values(terrain.data); values['flags'] = 9
        addon.state(terrain.data)['zs_recoil'] = json.dumps(values)
        shared_morph = bpy.data.objects.new('shared morph control', terrain.data)
        collection.objects.link(shared_morph)
        checks = output.parent / 'recreate-shared-morph'
        assert bpy.ops.recoil.export_gltf(directory=str(checks)) == {'FINISHED'}
        result = json.loads(next(checks.glob('*/data/models/world.gltf')).read_text())
        shared_node = next(n for n in result['nodes'] if n['name'] == 'shared morph control')
        assert shared_node.get('weights', result['meshes'][shared_node['mesh']].get('weights', [0])) == [.75]
        bpy.data.objects.remove(shared_morph, do_unlink=True)
        for geometry in (False, True):
            if geometry: terrain.data.vertices[0].co.x += .125
            checks = output.parent / ('recreate-geometry' if geometry else 'recreate-properties')
            assert bpy.ops.recoil.export_gltf(directory=str(checks)) == {'FINISHED'}
            result = json.loads(next(checks.glob('*/data/models/world.gltf')).read_text())
            nodes = {n['name']: n for n in result['nodes']}
            assert [result['nodes'][i]['name'] for i in nodes['placements']['children']] == ['sam_01', 'tur_101', 'untouched']
            moved = nodes['sam_01']
            assert abs((moved['matrix'][12] if 'matrix' in moved else moved['translation'][0]) - 2482.6077) < .001
            assert result['meshes'][nodes['terrain']['mesh']]['extras']['zstudio']['flags'] == 9
        bpy.context.view_layer.objects.active = sam
        sam.select_set(False)
        assert bpy.ops.recoil.add_model_reference(model=model, placement_name='root control', parent_mode='ROOT') == {'FINISHED'}
        assert bpy.context.object.parent is None
        count = len(bpy.data.objects)
        refused(bpy.ops.recoil.add_model_reference, model=model, parent_mode='ROOT', transform=json.dumps({'matrix':[1,0,0,0,.5,1,0,0,0,0,1,0,0,0,0,1]}))
        assert len(bpy.data.objects) == count
        parent = next(o for o in addon.source_objects(collection) if o.recoil.engine_name == 'placements')
        for name, position in [('insert X', 2), ('insert Y', 0)]:
            bpy.context.view_layer.objects.active = parent
            assert bpy.ops.recoil.add_model_reference(model=model, placement_name=name, sibling_index=position) == {'FINISHED'}
        checks = output.parent / 'recreate-order'
        assert bpy.ops.recoil.export_gltf(directory=str(checks)) == {'FINISHED'}
        result = json.loads(next(checks.glob('*/data/models/world.gltf')).read_text())
        group = next(n for n in result['nodes'] if n['name'] == 'placements')
        assert [result['nodes'][i]['name'] for i in group['children']] == ['insert Y', 'sam_01', 'tur_101', 'insert X', 'untouched']
        # An artist-created sibling has no identity until binding at export/add.
        loose = bpy.data.objects.new('loose sibling', None)
        collection.objects.link(loose); loose.parent = parent
        bpy.context.view_layer.objects.active = parent
        assert bpy.ops.recoil.add_model_reference(model=model, placement_name='anchor control', sibling_index=5) == {'FINISHED'}
        checks = output.parent / 'recreate-unbound-anchor'
        assert bpy.ops.recoil.export_gltf(directory=str(checks)) == {'FINISHED'}
        result = json.loads(next(checks.glob('*/data/models/world.gltf')).read_text())
        group = next(n for n in result['nodes'] if n['name'] == 'placements')
        assert [result['nodes'][i]['name'] for i in group['children']][-2:] == ['anchor control', 'loose sibling']
        # Rebinding a duplicate must not change the concrete insertion target.
        duplicate = loose.copy(); duplicate.name = 'duplicate sibling'
        duplicate.recoil.engine_name = 'duplicate sibling'
        collection.objects.link(duplicate)
        organized = bpy.data.collections.new('Artist organization control')
        collection.children.link(organized); organized.objects.link(loose); collection.objects.unlink(loose)
        checks = output.parent / 'recreate-duplicate-anchor'
        assert bpy.ops.recoil.export_gltf(directory=str(checks)) == {'FINISHED'}
        result = json.loads(next(checks.glob('*/data/models/world.gltf')).read_text())
        group = next(n for n in result['nodes'] if n['name'] == 'placements')
        names = [result['nodes'][i]['name'] for i in group['children']]
        assert names.index('anchor control') + 1 == names.index('loose sibling'), names
        bpy.ops.wm.save_as_mainfile(filepath=str(output.parent / 'anchor-roundtrip.blend'))
        bpy.ops.wm.open_mainfile(filepath=str(output.parent / 'anchor-roundtrip.blend'))
        _, data = addon.session(bpy.context)
        collection = addon.asset_collection(data, data['root'])
        loose = next(o for o in addon.source_objects(collection) if o.recoil.engine_name == 'loose sibling')
        anchored = next(o for o in addon.source_objects(collection) if o.recoil.engine_name == 'anchor control')
        assert addon.state(anchored)['zs_insert_target'] == loose
        parent = next(o for o in addon.source_objects(collection) if o.recoil.engine_name == 'placements')
        terrain = next(o for o in addon.source_objects(collection) if o.recoil.engine_name == 'terrain')
        parent_uid = addon.state(parent)['zs_uid']
        parent_copy = parent.copy(); parent_copy.recoil.engine_name = 'parent copy control'
        collection.objects.link(parent_copy)
        parent_folder = bpy.data.collections.new('Parent organization control')
        collection.children.link(parent_folder); parent_folder.objects.link(parent); collection.objects.unlink(parent)
        untouched = next(o for o in addon.source_objects(collection) if o.recoil.engine_name == 'untouched')
        untouched.location.x += 5
        checks = output.parent / 'recreate-parent-identity'
        assert bpy.ops.recoil.export_gltf(directory=str(checks)) == {'FINISHED'}
        assert addon.state(parent)['zs_uid'] != parent_uid
        result = json.loads(next(checks.glob('*/data/models/world.gltf')).read_text())
        nodes = {n['name']: n for n in result['nodes']}
        expected = json.loads(recipe_path.read_text())[1]['transform']
        assert {k: nodes['tur_101'][k] for k in expected} == expected
        assert nodes['untouched']['matrix'][12] == 2486.6077
        # Never leave an old Edit Mode mesh behind while changing the active object.
        bpy.ops.object.select_all(action='DESELECT')
        terrain.select_set(True); bpy.context.view_layer.objects.active = terrain
        assert bpy.ops.object.mode_set(mode='EDIT') == {'FINISHED'}
        count = len(bpy.data.objects)
        refused(bpy.ops.recoil.add_model_reference, model=model, parent_mode='ROOT')
        assert len(bpy.data.objects) == count and terrain.mode == 'EDIT'
        assert bpy.ops.object.mode_set(mode='OBJECT') == {'FINISHED'}
        # A real inline prefix is protected before creating any objects.
        original_values = addon.source_values(parent)
        values = dict(original_values, inlineSource={'id': 'section', 'count': 3})
        addon.populate(parent, values)
        bpy.context.view_layer.objects.active = parent
        count = len(bpy.data.objects)
        refused(bpy.ops.recoil.add_model_reference, model=model, sibling_index=0)
        assert len(bpy.data.objects) == count
        addon.populate(parent, original_values)
        bpy.data.objects.remove(loose, do_unlink=True)
        assert addon.state(anchored).get('zs_insert_target') is None
        refused(bpy.ops.recoil.export_gltf, directory=str(output.parent / 'removed-anchor'))
    print('ZSTUDIO_BLENDER_RECREATE_PASS')
    sys.exit(0)
if os.environ.get('ZSTUDIO_BLENDER_INVERSE_PHASE'):
    # Separate OS processes deliberately share only the completed graph export.
    assert bpy.ops.recoil.import_gltf(filepath=str(fixture)) == {'FINISHED'}
    _, session_data = addon.session(bpy.context)
    output.mkdir(parents=True, exist_ok=True)
    (output / 'session-input.json').write_text((Path(session_data['folder']) / 'session.json').read_text(), encoding='utf-8')
    collection = addon.asset_collection(session_data, session_data['root'])
    objects = [o for o in addon.source_objects(collection) if o.recoil.engine_name == os.environ.get('ZSTUDIO_BLENDER_INVERSE_OBJECT', 'sam_01')]
    assert len(objects) == 1, [o.name for o in objects]
    obj = objects[0]
    bpy.context.view_layer.update()
    before = obj.matrix_world.copy()
    obj.location.x += float(os.environ['ZSTUDIO_BLENDER_INVERSE_PHASE'])
    bpy.context.view_layer.update()
    assert abs(obj.matrix_world.translation.x - before.translation.x - float(os.environ['ZSTUDIO_BLENDER_INVERSE_PHASE'])) < 1e-5
    assert bpy.ops.recoil.export_gltf(directory=str(output)) == {'FINISHED'}
    print('ZSTUDIO_BLENDER_INVERSE_PASS')
    sys.exit(0)
# Carried material values must survive a no-op, but yield to resolved shader edits.
source_json = json.loads((fixture / 'part.gltf').read_text())
source_json['materials'][0]['extras']['zstudio'].update(color=[255, 255, 255], opacity=255, backface=False)
source_json['scene'] = 0.0
source_json['scenes'][0]['nodes'] = [float(index) for index in source_json['scenes'][0]['nodes']]
for node in source_json['nodes']:
    if 'mesh' in node:
        node['mesh'] = float(node['mesh'])
(fixture / 'part.gltf').write_text(json.dumps(source_json))
def legacy_metadata(node):
    if isinstance(node, dict):
        extras = node.get('extras')
        if isinstance(extras, dict) and 'zstudio' in extras:
            extras['recoil'] = extras.pop('zstudio')
        for key, value in node.items():
            if key != 'extras':
                legacy_metadata(value)
    elif isinstance(node, list):
        for value in node:
            legacy_metadata(value)
legacy_metadata(source_json)
(fixture / 'part.gltf').write_text(json.dumps(source_json))
source_world = json.loads((fixture / 'world.gltf').read_text())
source_world['scene'] = 0.0
source_world['scenes'][0]['nodes'] = [float(index) for index in source_world['scenes'][0]['nodes']]
(fixture / 'world.gltf').write_text(json.dumps(source_world))
assert bpy.ops.recoil.import_gltf(filepath=str(fixture / "world.gltf")) == {"FINISHED"}
_, data = addon.session(bpy.context)
world = addon.asset_collection(data, "world.gltf")
part = addon.asset_collection(data, "part.gltf")
# Previously saved scenes carry this legacy wrapper in internal state.
stored_scene = json.loads(addon.state(world)['zs_scene_extras'])
stored_scene['recoil'] = stored_scene.pop('zstudio')
addon.state(world)['zs_scene_extras'] = json.dumps(stored_scene)
holders = sorted((o for o in world.objects if "zs_origin" in addon.state(o)), key=lambda o: addon.state(o)["zs_origin"])
assert [o.recoil.zone_id for o in holders] == [3, 7]
refs = [o for o in world.objects if o.instance_type == "COLLECTION"]
assert len(refs) == 2 and all(o.instance_collection == part for o in refs)
assert bpy.ops.recoil.export_gltf(directory=str(output / "unchanged")) == {"FINISHED"}
# A no-op round trip leaves transforms as the source wrote them (the matrix is not rewritten as Blender's TRS).
unchanged = json.loads(next((output / "unchanged").glob("*/world.gltf")).read_text())
assert 'zstudio' in unchanged['scenes'][0]['extras'] and 'recoil' not in unchanged['scenes'][0]['extras']
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
assert not any(key in shown['extras']['zstudio'] for key in ('color', 'opacity', 'backface'))
# A deliberate advanced edit keeps precedence over the shader.
values = json.loads(original_values); values.update(color=[0, 255, 0], opacity=192, backface=True)
addon.state(material)['zs_recoil'] = json.dumps(values)
assert bpy.ops.recoil.export_gltf(directory=str(output / 'explicit-surface')) == {'FINISHED'}
explicit = json.loads(next((output / 'explicit-surface').glob('*/part.gltf')).read_text())
assert any(all(m.get('extras', {}).get('zstudio', {}).get(k) == values[k] for k in ('color', 'opacity', 'backface')) for m in explicit['materials'])
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
named = json.loads(next((output / 'named-properties').glob('*/part.gltf')).read_text())['nodes'][0]['extras']['zstudio']
assert all(named[flag] is True for flag in addon.FLAGS)
assert named['declient'] == 'clipTo' and named['zone'] == 0
named_root = json.loads(next((output / 'named-properties').glob('*/world.gltf')).read_text())['scenes'][0]['extras']['zstudio']['loadRoot']
assert named_root['altitudeZoneCheck'] is True and named_root['zone'] == 0
for flag in addon.FLAGS:
    setattr(mesh_object.recoil, flag, False)
mesh_object.recoil.declient = 'canModify'
assert addon.collect(mesh_object)['declient'] == 'canModify'
mesh_object.recoil.declient = 'UNSET'; mesh_object.recoil.zone_kind = 'any'
assert bpy.ops.recoil.export_gltf(directory=str(output / 'cleared-properties')) == {'FINISHED'}
cleared = json.loads(next((output / 'cleared-properties').glob('*/part.gltf')).read_text())['nodes'][0]['extras']['zstudio']
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
assert len(quad["meshes"][quad_mesh]["primitives"][0]["extras"]["zstudio"]["polygons"]) == 1
mesh_object.data.vertices[0].co.x -= 0.125
holders[0].recoil.zone_id = 4
mesh_object.recoil.landmark = True
assert bpy.ops.recoil.export_gltf(directory=str(output / "flags")) == {"FINISHED"}
flags_part = json.loads(next((output / "flags").glob("*/part.gltf")).read_text())
assert flags_part["nodes"][0]["extras"]["zstudio"]["landmark"] is True
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
assert "zoneOverrides" not in world_json["nodes"][0]["extras"]["zstudio"]
profile = world_json["nodes"][1]["extras"]["zstudio"]["zoneOverrides"]
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
new_profile = new_world["nodes"][2]["extras"]["zstudio"]["zoneOverrides"]
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
assert all('texture' not in m.get('extras', {}).get('zstudio', {}) and
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
    node['extras']['zstudio']['ref'] = 'middle.gltf'
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
assert moved_holder['translation'][0] == 4 and moved_holder['extras']['zstudio']['ref'] == 'part.gltf'
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
    profile = next(n['extras']['zstudio']['zoneOverrides'] for n in result['nodes'] if 'zoneOverrides' in n.get('extras', {}).get('zstudio', {}))
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
# Constraint-only moves must reach both metadata-only and structural exports.
# The target is itself constrained; reevaluating copied constraints against the
# originals from an isolated export scene would produce a different pose.
constraint_source = output / 'constraint-source'
shutil.copytree(fixture, constraint_source)
constraint_json = json.loads((constraint_source / 'world.gltf').read_text())
constraint_json['nodes'][0]['name'] = 'constrained placement'
constraint_json['nodes'][1]['name'] = 'untouched placement'
identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]
constraint_json['nodes'].extend([
    dict(name='constraint target', matrix=identity),
    dict(name='constrained parent', translation=[10, 0, 0], children=[4.0]),
    dict(name='constrained child', translation=[2, 0, 0]),
    dict(name='neutral constraint', matrix=identity),
    dict(name='neutral parent', translation=[12345, 900, -4567], scale=[2, 3, .5],
         rotation=[0, .3826834323650898, 0, .9238795325112867], children=[7.0]),
    dict(name='neutral child', translation=[.03125, .0625, -.125]),
    dict(name='external placement'),
    dict(name='context placement'),
])
constraint_json['scenes'][0]['nodes'].extend([2.0, 3.0, 5.0, 6.0, 8.0, 9.0])
(constraint_source / 'world.gltf').write_text(json.dumps(constraint_json))
assert bpy.ops.recoil.import_gltf(filepath=str(constraint_source / 'world.gltf')) == {'FINISHED'}
_, constraint_data = addon.session(bpy.context)
constraint_world = addon.asset_collection(constraint_data, 'world.gltf')
constraint_part = addon.asset_collection(constraint_data, 'part.gltf')
constraint_objects = {obj.recoil.engine_name: obj for obj in constraint_world.objects}
target = constraint_objects['constraint target']
parent = constraint_objects['constrained parent']
child = constraint_objects['constrained child']
placement = constraint_objects['constrained placement']
neutral = constraint_objects['neutral constraint']

def limit_x(obj, position):
    constraint = obj.constraints.new('LIMIT_LOCATION')
    constraint.owner_space = 'WORLD'
    constraint.use_min_x = constraint.use_max_x = True
    constraint.min_x = constraint.max_x = position

def copy_location(obj, target):
    constraint = obj.constraints.new('COPY_LOCATION')
    constraint.target = target
    constraint.owner_space = constraint.target_space = 'WORLD'

limit_x(target, 3)
limit_x(parent, 7)
copy_location(placement, target)
copy_location(child, target)
limit_x(neutral, 0)
neutral_child = constraint_objects['neutral child']
neutral_limit = neutral_child.constraints.new('LIMIT_LOCATION')
neutral_limit.owner_space = 'LOCAL'
neutral_limit.use_min_x = neutral_limit.use_max_x = True
neutral_limit.min_x = -100; neutral_limit.max_x = 100
# Artist helpers can live in another scene. Their complete animated/constrained
# dependency chain participates in evaluation, but never becomes source content.
artist_scene = bpy.data.scenes.new('Artist constraint targets')
artist_driver = bpy.data.objects.new('Artist animated helper', None)
artist_target = bpy.data.objects.new('Artist constrained helper', None)
artist_scene.collection.objects.link(artist_driver)
artist_scene.collection.objects.link(artist_target)
artist_driver.driver_add('location', 0).driver.expression = 'frame / 10.0'
copy_location(artist_target, artist_driver)
copy_location(constraint_objects['external placement'], artist_target)
context_target = bpy.data.objects.new('Artist scene-context helper', None)
artist_scene.collection.objects.link(context_target)
context_driver = context_target.driver_add('location', 0).driver
context_variable = context_driver.variables.new()
context_variable.name = 'active_fps'; context_variable.type = 'CONTEXT_PROP'
context_variable.targets[0].context_property = 'ACTIVE_SCENE'
context_variable.targets[0].data_path = 'render.fps'
context_driver.expression = 'active_fps'
copy_location(constraint_objects['context placement'], context_target)
bpy.context.scene.render.fps = 47
shared_surface = next(obj for obj in constraint_part.objects if addon.state(obj).get('zs_origin') == 0)
limit_x(shared_surface, 2)
wave = shared_surface.modifiers.new('Driven surface wave', 'WAVE')
wave.use_x = True; wave.use_y = False; wave.speed = 0
wave.start_position_x = .2; wave.width = 2
wave.height = .125
wave.driver_add('height').driver.expression = 'frame / 10.0'
mirror = shared_surface.modifiers.new('Animated external mirror target', 'MIRROR')
mirror.use_axis = (True, False, False)
mirror.use_mirror_merge = False
mirror.mirror_object = artist_driver
shared_surface.hide_viewport = True
bpy.context.scene.frame_set(37, subframe=.25)
# Keep the original channels unchanged: only dependency-graph evaluation moves
# these objects. The imported local baseline is saved in the .blend itself.
assert not addon.transform_changed(target) and not addon.transform_changed(child)
text, saved = addon.session(bpy.context)
saved['folder'] = str(output / 'constraint-absent-cache')
text.clear(); text.write(json.dumps(saved))
bpy.ops.wm.save_as_mainfile(filepath=str(output / 'constraints.blend'))
bpy.ops.wm.open_mainfile(filepath=str(output / 'constraints.blend'))
_, constraint_data = addon.session(bpy.context)
constraint_world = addon.asset_collection(constraint_data, 'world.gltf')
constraint_part = addon.asset_collection(constraint_data, 'part.gltf')
constraint_objects = {obj.recoil.engine_name: obj for obj in constraint_world.objects}
shared_surface = next(obj for obj in constraint_part.objects if addon.state(obj).get('zs_origin') == 0)
# Independently read the original evaluated mesh in its shared-file workspace.
# Its driver differs from the stored modifier value, so clearing the copy's
# animation without first freezing modifier inputs would lose this geometry.
bpy.context.view_layer.objects.active = constraint_objects['constrained placement']
assert bpy.ops.recoil.edit_shared_model() == {'FINISHED'}
bpy.context.scene.frame_set(37, subframe=.25)
shared_surface.hide_viewport = False
# The independent expected mesh uses the original target at this frame, with
# the same dependency membership that the main export must keep alive.
bpy.context.scene.collection.objects.link(shared_surface.modifiers['Animated external mirror target'].mirror_object)
bpy.context.view_layer.update()
evaluated_surface = shared_surface.evaluated_get(bpy.context.evaluated_depsgraph_get())
assert abs(evaluated_surface.modifiers['Driven surface wave'].height - 3.725) < 1e-5
expected_positions = [(vertex.co.x, vertex.co.z, -vertex.co.y) for vertex in evaluated_surface.data.vertices]
assert len(expected_positions) == 8
assert any(abs(position[1]) > 1e-5 for position in expected_positions)
shared_surface.hide_viewport = True
assert bpy.ops.recoil.return_to_world() == {'FINISHED'}

# A visible library-linked target is read-only: evaluation must avoid even a
# no-op visibility assignment, while restoring hidden local source objects.
library_path = output / 'constraint-target-library.blend'
library_object = bpy.data.objects.new('Linked constraint helper', None)
library_object.location.x = 6.5
bpy.data.libraries.write(str(library_path), {library_object})
bpy.data.objects.remove(library_object)
with bpy.data.libraries.load(str(library_path), link=True) as (library_data, linked_data):
    linked_data.objects = ['Linked constraint helper']
linked_target = linked_data.objects[0]
assert linked_target.library is not None and not linked_target.is_editable and not linked_target.hide_viewport
bpy.data.scenes['Artist constraint targets'].collection.objects.link(linked_target)
external_constraint = constraint_objects['external placement'].constraints[0]
previous_target = external_constraint.target
hidden_local = constraint_objects['neutral constraint']
hidden_local.hide_viewport = True
external_constraint.target = linked_target
linked_scene = bpy.context.scene
linked_scenes = set(bpy.data.scenes)
try:
    assert bpy.ops.recoil.export_gltf(directory=str(output / 'linked-constraint-target')) == {'FINISHED'}
    linked_result = json.loads(next((output / 'linked-constraint-target').glob('*/world.gltf')).read_text())
    linked_placement = next(node for node in linked_result['nodes'] if node['name'] == 'external placement')
    assert abs(linked_placement['translation'][0] - 6.5) < 1e-5
    assert hidden_local.hide_viewport and not linked_target.hide_viewport
    assert bpy.context.scene == linked_scene and set(bpy.data.scenes) == linked_scenes
finally:
    external_constraint.target = previous_target
    hidden_local.hide_viewport = False
    bpy.data.objects.remove(linked_target, do_unlink=True)

def assert_constraint_export(name):
    original_scene = bpy.context.scene
    original_time = (original_scene.frame_current, original_scene.frame_subframe)
    scene_count = len(bpy.data.scenes)
    assert bpy.ops.recoil.export_gltf(directory=str(output / name)) == {'FINISHED'}
    result = json.loads(next((output / name).glob('*/world.gltf')).read_text())
    nodes = {node['name']: node for node in result['nodes']}
    for engine_name, x in [('constrained placement', 3), ('constraint target', 3),
                           ('constrained parent', 7), ('constrained child', -4), ('external placement', 3.725),
                           ('context placement', 47)]:
        actual_x = nodes[engine_name]['matrix'][12] if 'matrix' in nodes[engine_name] else nodes[engine_name]['translation'][0]
        assert abs(actual_x - x) < 1e-5, (engine_name, nodes[engine_name])
    parent_index = result['nodes'].index(nodes['constrained parent'])
    child_index = result['nodes'].index(nodes['constrained child'])
    assert result['nodes'][parent_index]['children'] == [child_index]
    for engine_name, source_index in [('untouched placement', 1), ('neutral constraint', 5), ('neutral child', 7)]:
        exported_transform = {k: nodes[engine_name][k] for k in ('matrix', 'translation', 'rotation', 'scale') if k in nodes[engine_name]}
        source_transform = {k: constraint_json['nodes'][source_index][k] for k in ('matrix', 'translation', 'rotation', 'scale') if k in constraint_json['nodes'][source_index]}
        assert json.dumps(exported_transform, sort_keys=True) == json.dumps(source_transform, sort_keys=True), \
               dict(export=name, node=engine_name, expected=source_transform, actual=exported_transform)
    part = json.loads(next((output / name).glob('*/part.gltf')).read_text())
    surface = next(node for node in part['nodes'] if node['name'] == 'surface')
    assert abs(surface['translation'][0] - 2) < 1e-5
    from io_scene_gltf2.io.imp.gltf2_io_gltf import glTFImporter
    from io_scene_gltf2.io.imp.gltf2_io_binary import BinaryData
    reader = glTFImporter(str(next((output / name).glob('*/part.gltf'))), {'import_user_extensions': []})
    try:
        reader.read(); reader.checks()
        actual_positions = [position for primitive in reader.data.meshes[surface['mesh']].primitives
                            for position in BinaryData.decode_accessor(reader, primitive.attributes['POSITION']).tolist()]
        def matches(a, b):
            return all(abs(x - y) < 1e-5 for x, y in zip(a, b))
        geometry_diagnostic = dict(export=name, surface=surface, expected=expected_positions, actual=actual_positions,
                                   scene=bpy.context.scene.name, frame=bpy.context.scene.frame_current,
                                   subframe=bpy.context.scene.frame_subframe, fps=bpy.context.scene.render.fps,
                                   source_transform=addon.local_matrix(shared_surface),
                                   modifier_targets=[(modifier.name, getattr(getattr(modifier, 'mirror_object', None), 'name', None))
                                                     for modifier in shared_surface.modifiers])
        assert all(any(matches(actual, expected) for expected in expected_positions) for actual in actual_positions), geometry_diagnostic
        assert all(any(matches(actual, expected) for actual in actual_positions) for expected in expected_positions), geometry_diagnostic
    finally:
        reader.log.flush()
    assert all(nodes[name]['extras']['zstudio']['ref'] in ('part.gltf', './part.gltf')
               for name in ('constrained placement', 'untouched placement'))
    assert bpy.context.scene == original_scene and len(bpy.data.scenes) == scene_count
    assert (original_scene.frame_current, original_scene.frame_subframe) == original_time
    assert shared_surface.hide_viewport and len(shared_surface.constraints) == 1
    assert constraint_objects['constrained child'].constraints[0].target == constraint_objects['constraint target']
    assert not addon.transform_changed(constraint_objects['constraint target'])
    assert len(bpy.data.scenes['Artist constraint targets'].objects) == 3
    assert len(result['nodes']) == len(constraint_json['nodes']) + (name == 'constraints-structural')

assert_constraint_export('constraints')
new_object = bpy.data.objects.new('new constraint neighbor', None)
constraint_world.objects.link(new_object)
assert_constraint_export('constraints-structural')
# The same parent must retain a real clamp below world-space float precision.
# LOCAL operates on matrix_basis; the parent inverse belongs in the exported
# local pose afterwards. Influence must retain the small displacement as well.
from mathutils import Matrix
tiny_child = constraint_objects['neutral child']
tiny_child.matrix_parent_inverse = Matrix.Translation((.5, 0, 0))
tiny_limit = tiny_child.constraints[0]
tiny_limit.max_x = .03124
tiny_limit.influence = .25
assert bpy.ops.recoil.export_gltf(directory=str(output / 'constraints-tiny-limit')) == {'FINISHED'}
tiny_result = json.loads(next((output / 'constraints-tiny-limit').glob('*/world.gltf')).read_text())
tiny_node = next(node for node in tiny_result['nodes'] if node['name'] == 'neutral child')
tiny_expected = [.5 + .03125 + (.03124 - .03125) * .25, .0625, -.125]
assert all(abs(actual - expected) < 5e-8 for actual, expected in zip(tiny_node['translation'], tiny_expected)), \
       dict(node='neutral child', expected=tiny_expected, actual=tiny_node)
# A world-space rotation below a rotated nonuniformly scaled parent can require
# shear in the child's local transform. The TRS exporter must refuse that pose.
parent = constraint_objects['constrained parent']
parent.rotation_mode = 'XYZ'
parent.rotation_euler.z = .6
parent.scale = (2, 1, .5)
rotation_constraint = constraint_objects['constrained child'].constraints.new('COPY_ROTATION')
rotation_constraint.target = constraint_objects['neutral constraint']
rotation_constraint.owner_space = rotation_constraint.target_space = 'WORLD'
scene_before_shear = bpy.context.scene
scenes_before_shear = set(bpy.data.scenes)
try:
    result = bpy.ops.recoil.export_gltf(directory=str(output / 'constraint-shear'))
except RuntimeError as error:
    assert 'shear' in str(error), error
else:
    assert result == {'CANCELLED'}
assert not (output / 'constraint-shear').exists()
assert bpy.context.scene == scene_before_shear and set(bpy.data.scenes) == scenes_before_shear
assert len(constraint_objects['constrained child'].constraints) == 2 and shared_surface.hide_viewport
# Vertex parenting contributes an offset/orientation beyond parent-inverse and
# channel matrices. A normal artist-created child must retain that effective
# local pose when staging switches to ordinary object parenting.
assert bpy.ops.recoil.import_gltf(filepath=str(fixture / 'world.gltf')) == {'FINISHED'}
_, vertex_data = addon.session(bpy.context)
vertex_world = addon.asset_collection(vertex_data, 'world.gltf')
vertex_part = addon.asset_collection(vertex_data, 'part.gltf')
bpy.context.view_layer.objects.active = next(obj for obj in vertex_world.objects if obj.recoil.reference)
assert bpy.ops.recoil.edit_shared_model() == {'FINISHED'}
vertex_surface = next(obj for obj in vertex_part.objects if addon.state(obj).get('zs_origin') == 0)

def vertex_at(coordinates):
    return next(vertex.index for vertex in vertex_surface.data.vertices
                if all(abs(actual - expected) < 1e-6 for actual, expected in zip(vertex.co, coordinates)))

vertex_index = vertex_at((2, 0, 0))
triangle_indices = (vertex_at((0, 0, 0)), vertex_index, vertex_at((0, -2, 0)))
vertex_children = []
for name, kind, indices in [('vertex child', 'VERTEX', (vertex_index,) * 3),
                            ('triangle child', 'VERTEX_3', triangle_indices)]:
    child = bpy.data.objects.new(name, None)
    vertex_part.objects.link(child)
    child.parent = vertex_surface; child.parent_type = kind; child.parent_vertices = indices
    vertex_children.append(child)
bpy.context.view_layer.update()
vertex_depsgraph = bpy.context.evaluated_depsgraph_get()
expected_parent_poses = {}
for child in vertex_children:
    evaluated_child = child.evaluated_get(vertex_depsgraph)
    expected_parent_poses[child.name] = evaluated_child.parent.matrix_world.inverted() @ evaluated_child.matrix_world
assert abs(expected_parent_poses['vertex child'].translation.x - 2) < 1e-6, expected_parent_poses['vertex child']
assert any(abs(expected_parent_poses['triangle child'][row][column] - (row == column)) > 1e-5
           for row in range(3) for column in range(3))
assert bpy.ops.recoil.export_gltf(directory=str(output / 'vertex-parenting')) == {'FINISHED'}
vertex_result = json.loads(next((output / 'vertex-parenting').glob('*/part.gltf')).read_text())
from mathutils import Quaternion, Vector
axis_change = Matrix(((1, 0, 0, 0), (0, 0, 1, 0), (0, -1, 0, 0), (0, 0, 0, 1)))
for name, expected in expected_parent_poses.items():
    node = next(node for node in vertex_result['nodes'] if node['name'] == name)
    rotation = node.get('rotation', [0, 0, 0, 1])
    actual = Matrix.LocRotScale(Vector(node.get('translation', [0, 0, 0])),
                               Quaternion((rotation[3], *rotation[:3])), Vector(node.get('scale', [1, 1, 1])))
    expected = axis_change @ expected @ axis_change.transposed()
    assert all(abs(a - b) < 1e-5 for before, after in zip(expected, actual) for a, b in zip(before, after)), \
           dict(node=name, expected=[list(row) for row in expected], actual=node)
assert [child.parent_type for child in vertex_children] == ['VERTEX', 'VERTEX_3']
# Ordinary Keep Transform parenting can introduce a sheared parent inverse even
# without constraints. Both the direct-channel and evaluated paths must refuse
# it before staging can decompose the artist's pose and silently move the child.
assert bpy.ops.recoil.import_gltf(filepath=str(fixture / 'world.gltf')) == {'FINISHED'}
_, inverse_data = addon.session(bpy.context)
inverse_world = addon.asset_collection(inverse_data, 'world.gltf')
inverse_parent = bpy.data.objects.new('Keep Transform parent', None)
inverse_child = bpy.data.objects.new('Keep Transform child', None)
for obj in (inverse_parent, inverse_child):
    inverse_world.objects.link(obj)
inverse_parent.location = (4, 2, 1)
inverse_parent.rotation_euler = (0, 0, .6)
inverse_parent.scale = (2, 1, .5)
inverse_child.location = (1, 3, 2)
inverse_child.rotation_euler = (.3, -.2, 1)
bpy.context.view_layer.update()
unparented_world = inverse_child.matrix_world.copy()
bpy.ops.object.select_all(action='DESELECT')
for obj in (inverse_parent, inverse_child):
    obj.select_set(True)
bpy.context.view_layer.objects.active = inverse_parent
assert bpy.ops.object.parent_set(type='OBJECT', keep_transform=True) == {'FINISHED'}
bpy.context.view_layer.update()
assert all(abs(a - b) < 1e-5 for before, after in zip(unparented_world, inverse_child.matrix_world) for a, b in zip(before, after))
inverse_pose = inverse_child.matrix_parent_inverse @ inverse_child.matrix_basis
decomposed_pose = Matrix.LocRotScale(*inverse_pose.decompose())
assert max(abs(a - b) for before, after in zip(inverse_pose, decomposed_pose) for a, b in zip(before, after)) > .1
inverse_child.hide_viewport = True
inverse_sources = {obj for item in inverse_data['models'] for obj in addon.source_objects(addon.asset_collection(inverse_data, item['path']))}
assert all(not obj.constraints and not obj.modifiers and not obj.animation_data and obj.parent_type == 'OBJECT' for obj in inverse_sources)
input_snapshot = {path: path.read_bytes() for path in fixture.iterdir() if path.is_file()}
for evaluate in (False, True):
    if evaluate:
        # Evaluate a neutral constraint on the parent while the sheared child
        # remains an unconstrained ordinary-object fallback in that same graph.
        inverse_parent.constraints.new('LIMIT_LOCATION')
    original_scene = bpy.context.scene
    original_time = (original_scene.frame_current, original_scene.frame_subframe)
    original_active = bpy.context.view_layer.objects.active
    original_selection = {obj for obj in original_scene.objects if obj.select_get()}
    original_scenes, original_objects = set(bpy.data.scenes), set(bpy.data.objects)
    original_visibility = {obj: (obj.hide_viewport, obj.hide_render, obj.hide_select) for obj in inverse_sources}
    original_transforms = {obj: (obj.parent, obj.parent_type, obj.matrix_parent_inverse.copy(), obj.matrix_basis.copy())
                           for obj in inverse_sources}
    destination = output / ('parent-inverse-evaluated' if evaluate else 'parent-inverse-direct')
    try:
        result = bpy.ops.recoil.export_gltf(directory=str(destination))
    except RuntimeError as error:
        assert inverse_child.name in str(error) and 'shear' in str(error) and 'parenting' in str(error), error
    else:
        raise AssertionError(('Keep Transform shear must report an actionable refusal', evaluate, result))
    assert not destination.exists()
    assert bpy.context.scene == original_scene and set(bpy.data.scenes) == original_scenes and set(bpy.data.objects) == original_objects
    assert (original_scene.frame_current, original_scene.frame_subframe) == original_time
    assert bpy.context.view_layer.objects.active == original_active
    assert {obj for obj in original_scene.objects if obj.select_get()} == original_selection
    for obj in inverse_sources:
        assert (obj.hide_viewport, obj.hide_render, obj.hide_select) == original_visibility[obj]
        parent, kind, parent_inverse, basis = original_transforms[obj]
        assert obj.parent == parent and obj.parent_type == kind and obj.matrix_parent_inverse == parent_inverse and obj.matrix_basis == basis
    assert all(path.read_bytes() == content for path, content in input_snapshot.items())
assert not inverse_child.constraints and len(inverse_parent.constraints) == 1 and inverse_child.hide_viewport
# Zero axes leave part of a rotation unobservable, while right-angle rotations
# with large scale magnify tiny quaternion rounding. These are valid TRS poses,
# including reflections, and must survive both baking and the node transport.
import math
from mathutils import Euler
trs_source = output / 'valid-trs-source'
shutil.copytree(fixture, trs_source)
trs_json = json.loads((trs_source / 'world.gltf').read_text())
trs_cases = [('zero axis', (.3, -.2, 1), (0, 1, 1)),
             ('two zero axes', (.3, -.2, 1), (0, 0, 1)),
             ('all zero axes', (.3, -.2, 1), (0, 0, 0)),
             ('negative scale', (.3, -.2, 1), (-1, 2, .5)),
             ('right angle large scale', (math.pi / 2, 0, 0), (1, 100, .5))]
for name, angles, scale in trs_cases:
    rotation = Euler(angles, 'XYZ').to_quaternion()
    trs_json['scenes'][0]['nodes'].append(len(trs_json['nodes']))
    trs_json['nodes'].append(dict(name=name, translation=[5, 6, 7],
                                 rotation=[rotation.x, rotation.y, rotation.z, rotation.w],
                                 scale=list(scale), extras=dict(recoil={})))
(trs_source / 'world.gltf').write_text(json.dumps(trs_json))
assert bpy.ops.recoil.import_gltf(filepath=str(trs_source / 'world.gltf')) == {'FINISHED'}
_, trs_data = addon.session(bpy.context)
trs_world = addon.asset_collection(trs_data, 'world.gltf')
trs_objects = {obj.recoil.engine_name: obj for obj in trs_world.objects if obj.recoil.engine_name in {case[0] for case in trs_cases}}
trs_objects['zero axis'].hide_viewport = True
trs_inputs = {path: path.read_bytes() for path in trs_source.iterdir() if path.is_file()}
exporter_module = importlib.import_module(module + '.exporter')

def assert_valid_trs_export(name, unchanged):
    expected = {name: obj.matrix_basis.copy() for name, obj in trs_objects.items()}
    visibility = {name: obj.hide_viewport for name, obj in trs_objects.items()}
    if unchanged:
        assert all(not addon.transform_changed(obj) for obj in trs_objects.values())
    captured = {}
    original_hook = exporter_module.glTF2ExportUserExtension.gather_node_hook

    def observe_staging(self, node, obj, settings):
        if addon._exporting is not None and obj.recoil.engine_name in trs_objects:
            captured[obj.recoil.engine_name] = obj.matrix_basis.copy()
        original_hook(self, node, obj, settings)

    exporter_module.glTF2ExportUserExtension.gather_node_hook = observe_staging
    original_scene, original_scenes = bpy.context.scene, set(bpy.data.scenes)
    try:
        assert bpy.ops.recoil.export_gltf(directory=str(output / name)) == {'FINISHED'}
    finally:
        exporter_module.glTF2ExportUserExtension.gather_node_hook = original_hook
    result = json.loads(next((output / name).glob('*/world.gltf')).read_text())
    assert set(captured) == set(trs_objects)
    for engine_name, basis in expected.items():
        node = next(node for node in result['nodes'] if node['name'] == engine_name)
        rotation = node.get('rotation', [0, 0, 0, 1])
        actual = Matrix.LocRotScale(Vector(node.get('translation', [0, 0, 0])),
                                   Quaternion((rotation[3], *rotation[:3])), Vector(node.get('scale', [1, 1, 1])))
        actual = axis_change.transposed() @ actual @ axis_change
        for column in range(4):
            tolerance = 1e-6 * max(1, math.sqrt(sum(basis[row][column] ** 2 for row in range(3))))
            assert all(abs(basis[row][column] - captured[engine_name][row][column]) <= tolerance for row in range(3)), \
                   dict(node=engine_name, expected=[list(row) for row in basis], staged=[list(row) for row in captured[engine_name]])
            assert all(abs(basis[row][column] - actual[row][column]) <= tolerance for row in range(3)), \
                   dict(node=engine_name, expected=[list(row) for row in basis], actual=node)
        if unchanged:
            original = next(node for node in trs_json['nodes'] if node['name'] == engine_name)
            assert json.dumps({key: node.get(key) for key in ('translation', 'rotation', 'scale')}, sort_keys=True) == \
                   json.dumps({key: original.get(key) for key in ('translation', 'rotation', 'scale')}, sort_keys=True)
        assert trs_objects[engine_name].matrix_basis == basis and trs_objects[engine_name].hide_viewport == visibility[engine_name]
    assert bpy.context.scene == original_scene and set(bpy.data.scenes) == original_scenes
    assert all(path.read_bytes() == content for path, content in trs_inputs.items())

assert_valid_trs_export('valid-trs-direct', unchanged=True)
trs_trigger_owner = next(obj for obj in trs_world.objects if obj.recoil.reference)
trs_trigger = trs_trigger_owner.constraints.new('LIMIT_LOCATION')
assert_valid_trs_export('valid-trs-evaluated', unchanged=True)
for obj in trs_objects.values():
    obj.location.x += .125
assert_valid_trs_export('valid-trs-edited', unchanged=False)
# Keep Transform with aligned rotations remains TRS. A short inverse-scale
# column contains more angular roundoff than the long observable columns;
# acceptance must preserve the emitted forward world pose, including reflection.
trs_trigger_owner.constraints.remove(trs_trigger)
aligned_pairs = []
for name, angles, scale in [('ordinary', (.3, -.2, 1), (1, 100, .5)),
                            ('right angle', (math.pi / 2, 0, 0), (1, 100, .5)),
                            ('negative', (.3, -.2, 1), (1, -100, .5))]:
    parent = bpy.data.objects.new('aligned ' + name + ' parent', None)
    child = bpy.data.objects.new('aligned ' + name + ' child', None)
    for obj in (parent, child):
        trs_world.objects.link(obj)
    parent.location = (4, 2, 1); parent.rotation_euler = angles; parent.scale = scale
    child.location = (1, 3, 2); child.rotation_euler = angles
    bpy.context.view_layer.update()
    before = child.matrix_world.copy()
    bpy.ops.object.select_all(action='DESELECT')
    parent.select_set(True); child.select_set(True)
    bpy.context.view_layer.objects.active = parent
    assert bpy.ops.object.parent_set(type='OBJECT', keep_transform=True) == {'FINISHED'}
    bpy.context.view_layer.update()
    assert max(abs(a - b) for old, new in zip(before, child.matrix_world) for a, b in zip(old, new)) < 1e-5
    aligned_pairs.append((parent, child))
assert all(not obj.constraints and not obj.modifiers and not obj.animation_data and obj.parent_type == 'OBJECT'
           for item in trs_data['models'] for obj in addon.source_objects(addon.asset_collection(trs_data, item['path'])))
for evaluate in (False, True):
    if evaluate:
        trs_trigger_owner.constraints.new('LIMIT_LOCATION')
    expected_world = {child: child.matrix_world.copy() for _, child in aligned_pairs}
    source_channels = {obj: (obj.parent, obj.matrix_parent_inverse.copy(), obj.matrix_basis.copy())
                       for pair in aligned_pairs for obj in pair}
    original_scene, original_scenes = bpy.context.scene, set(bpy.data.scenes)
    folder = output / ('aligned-trs-evaluated' if evaluate else 'aligned-trs-direct')
    assert bpy.ops.recoil.export_gltf(directory=str(folder)) == {'FINISHED'}
    nodes = {node['name']: node for node in json.loads(next(folder.glob('*/world.gltf')).read_text())['nodes']}

    def emitted_pose(obj):
        node = nodes[obj.name]
        rotation = node.get('rotation', [0, 0, 0, 1])
        result = Matrix.LocRotScale(Vector(node.get('translation', [0, 0, 0])),
                                   Quaternion((rotation[3], *rotation[:3])), Vector(node.get('scale', [1, 1, 1])))
        return axis_change.transposed() @ result @ axis_change

    for parent, child in aligned_pairs:
        actual = emitted_pose(parent) @ emitted_pose(child)
        expected = expected_world[child]
        matrix_error = max(abs(a - b) for before, after in zip(expected, actual) for a, b in zip(before, after))
        point = Vector((2, 3, 4))
        expected_point, actual_point = expected @ point, actual @ point
        point_error = (actual_point - expected_point).length
        diagnostic = dict(node=child.name, evaluated=evaluate, matrix_error=matrix_error, point_error=point_error)
        print('ZSTUDIO_ALIGNED_TRS ' + json.dumps(diagnostic))
        assert matrix_error <= 1e-5 * max(1, max(abs(value) for row in expected for value in row)), \
               dict(diagnostic, expected=[list(row) for row in expected], actual=[list(row) for row in actual])
        assert point_error <= 1e-5 * max(1, expected_point.length), dict(diagnostic, expected=list(expected_point), actual=list(actual_point))
    for obj, (parent, parent_inverse, basis) in source_channels.items():
        assert obj.parent == parent and obj.matrix_parent_inverse == parent_inverse and obj.matrix_basis == basis
    assert bpy.context.scene == original_scene and set(bpy.data.scenes) == original_scenes and trs_objects['zero axis'].hide_viewport
    assert all(path.read_bytes() == content for path, content in trs_inputs.items())
# Standalone native import/export uses the same hooks and helper, with actual vertex painting in Blender.
native_source = output.parent / 'native-color-source'
native_source.mkdir()
# Three XYZ positions followed by three RGBA colours and three triangle indices.
native_binary = struct.pack('<21f3I', 0, 0, 0, 1, 0, 0, 0, 0, 1,
                            1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 0, 1, 2)
native_json = dict(asset=dict(version='2.0'), scene=0,
    scenes=[dict(nodes=[0], extras=dict(zstudio=dict(sourceFormat=4, game='mw3')))],
    nodes=[dict(name='painted', mesh=0)],
    meshes=[dict(primitives=[dict(attributes=dict(POSITION=0, COLOR_0=1), indices=2, material=0)])],
    materials=[dict(name='paint', pbrMetallicRoughness=dict(metallicFactor=0), extras=dict(zstudio=dict(normals=False)))],
    buffers=[dict(uri='model.bin', byteLength=len(native_binary))],
    bufferViews=[dict(buffer=0, byteOffset=0, byteLength=36), dict(buffer=0, byteOffset=36, byteLength=48), dict(buffer=0, byteOffset=84, byteLength=12)],
    accessors=[dict(bufferView=0, componentType=5126, count=3, type='VEC3', min=[0, 0, 0], max=[1, 0, 1]),
               dict(bufferView=1, componentType=5126, count=3, type='VEC4'), dict(bufferView=2, componentType=5125, count=3, type='SCALAR')])
(native_source / 'model.gltf').write_text(json.dumps(native_json))
(native_source / 'model.bin').write_bytes(native_binary)
assert bpy.ops.recoil.import_gltf(filepath=str(native_source / 'model.gltf')) == {'FINISHED'}
_, native_data = addon.session(bpy.context)
native_collection = addon.asset_collection(native_data, native_data['root'])
native_mesh = next(obj.data for obj in addon.source_objects(native_collection) if obj.type == 'MESH')
assert native_mesh.color_attributes.active_color is not None
for color in native_mesh.color_attributes.active_color.data:
    color.color = (0, 1, 0, 1)
native_output = output / 'native-painted'
assert bpy.ops.recoil.export_gltf(directory=str(native_output)) == {'FINISHED'}
native_result = next(native_output.glob('*/model.gltf'))
native_export = json.loads(native_result.read_text())
native_primitive = native_export['meshes'][0]['primitives'][0]
native_accessor = native_export['accessors'][native_primitive['attributes']['COLOR_0']]
native_view = native_export['bufferViews'][native_accessor['bufferView']]
native_bytes = (native_result.parent / native_export['buffers'][native_view['buffer']]['uri']).read_bytes()
native_component, native_scale = {5126: ('f', 1), 5121: ('B', 255), 5123: ('H', 65535)}[native_accessor['componentType']]
assert native_component == 'f' or native_accessor.get('normalized') is True
native_size = struct.calcsize(native_component)
native_width = 4 if native_accessor['type'] == 'VEC4' else 3
for vertex in range(native_accessor['count']):
    at = native_view.get('byteOffset', 0) + native_accessor.get('byteOffset', 0) + vertex * native_view.get('byteStride', native_width * native_size)
    assert tuple(value / native_scale for value in struct.unpack_from('<3' + native_component, native_bytes, at)) == (0, 1, 0), 'Native vertex painting was lost'
assert native_export['scenes'][0]['extras']['zstudio']['game'] == 'mw3'
assert (native_source / 'model.bin').read_bytes() == native_binary
print("ZSTUDIO_BLENDER_ROUNDTRIP_PASS")
