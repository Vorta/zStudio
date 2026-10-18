# SPDX-License-Identifier: MIT
import json
import shutil
import sys
import tempfile
from pathlib import Path

import bpy
from bpy.props import StringProperty

from . import bridge


def api():
    return sys.modules[__package__]


class glTF2ExportUserExtension:
    is_critical = True

    def gather_node_hook(self, node, obj, settings):
        if api()._exporting is None:
            return
        if api().state(obj).get("zs_reference_helper"):
            raise ValueError("A reference display helper reached the source exporter")
        node.extras = {"recoil": api().collect(obj), "zstudio_blender": int(api().state(obj).get("zs_origin", -1)),
                       "zstudio_editor_id": api().state(obj)["zs_uid"],
                       "zstudio_transform_changed": api().transform_changed(obj)}
        node.name = obj.recoil.engine_name if "zs_file" in api().state(obj) else obj.name
        if "zs_placements" in api().state(obj):
            node.extras["zstudio_placements"] = json.loads(api().state(obj)["zs_placements"])
        if "zs_copy_of" in api().state(obj):
            node.extras["zstudio_copy_of"] = api().state(obj)["zs_copy_of"]

    def gather_mesh_hook(self, mesh, blender_mesh, obj, vertex_groups, modifiers, materials, settings):
        if api()._exporting is not None:
            # With modifiers the exporter passes an evaluated mesh without bookkeeping: the staged copy holds it.
            # (Blender passes no object for an unskinned mesh; the modifiers belong to the staged copy.)
            if "zs_uid" in api().state(blender_mesh):
                source = blender_mesh
            else:
                staged = obj if obj is not None else getattr(modifiers, "id_data", None)
                source = getattr(staged, "data", None)
                if source is None or "zs_uid" not in api().state(source):
                    raise ValueError("Only meshes can be exported to RECOIL; convert or remove other objects first")
            api().require_graph(source, api().state(api()._exporting)["zs_session"])
            values = api().state(source)
            mesh.extras = {"recoil": json.loads(values.get("zs_recoil", "{}")),
                           "zstudio_editor_id": values.get("zs_export_uid", values["zs_uid"] + (api().state(obj)["zs_uid"] if obj and obj.modifiers else "")),
                           "zstudio_blender": int(values.get("zs_origin", -1)) if values.get("zs_file") == api().state(api()._exporting)["zs_path"] else -1,
                           "zstudio_face_tree": json.loads(values.get("zs_face_tree", "{}")),
                           "zstudio_mesh_tag": values.get("zs_mesh_tag")}

    def gather_material_hook(self, material, blender_material, settings):
        if api()._exporting is not None:
            api().require_graph(blender_material, api().state(api()._exporting)["zs_session"])
            values = json.loads(api().state(blender_material).get("zs_recoil", "{}"))
            original = json.loads(api().state(blender_material).get("zs_original_recoil", "{}"))
            surface = json.loads(api().state(blender_material).get("zs_surface", "{}"))
            current = api().material_surface(material)
            for field in ("color", "opacity", "backface"):
                if not api().same_surface_value(current.get(field), surface.get(field)) and values.get(field) == original.get(field):
                    values.pop(field, None)
            material.extras = {"recoil": values,
                               "zstudio_blender": int(api().state(blender_material).get("zs_origin", -1)) if api().state(blender_material).get("zs_file") == api().state(api()._exporting)["zs_path"] else -1}
            if "zs_file" in api().state(blender_material):
                material.extras["zstudio_material_source"] = {
                    "path": api().state(blender_material)["zs_file"],
                    "index": int(api().state(blender_material)["zs_origin"])}

    def gather_image_hook(self, image, exported, sockets, settings):
        if api()._exporting is None:
            return
        images = {getattr(fill, "image", None) for fill in exported.fills.values()}
        if exported.original:
            images.add(exported.original)
        for source_image in images:
            if source_image:
                api().require_graph(source_image, api().state(api()._exporting)["zs_session"])
        paths = {api().state(i).get("zs_source_path") for i in images if i and api().state(i).get("zs_source_path")}
        if len(paths) == 1:
            image.extras = {"zstudio_blender": paths.pop()}
            missing = [i for i in images if i and api().state(i).get("zs_missing_file")]
            if len(missing) == 1 and len([i for i in images if i]) == 1 and tuple(missing[0].size) == (2, 2) \
                    and json.dumps(list(missing[0].pixels)) == api().state(missing[0])["zs_missing_pixels"]:
                image.extras["zstudio_missing_image"] = True

    def gather_scene_hook(self, scene, blender_scene, settings):
        if api()._exporting is not None:
            collection = api()._exporting
            scene.extras = json.loads(api().state(collection).get("zs_scene_extras", "{}"))
            recoil = scene.extras.setdefault("recoil", {})
            recoil["sourceFormat"] = 4
            recoil["loadRoot"] = api().collect(collection)


def annotate_polygons(path):
    """Decode transport attributes through Blender's existing glTF reader, not a second accessor parser."""
    from io_scene_gltf2.io.imp.gltf2_io_gltf import glTFImporter
    from io_scene_gltf2.io.imp.gltf2_io_binary import BinaryData
    reader = glTFImporter(str(path), {"import_user_extensions": []})
    try:
        reader.read()
        reader.checks()
        root = bridge.read_json(path)
        for m, mesh in enumerate(reader.data.meshes or []):
            tree = root["meshes"][m].get("extras", {}).pop("zstudio_face_tree", {})
            # The evaluated mesh (a Boolean's operand included) may hold faces of another RECOIL mesh.
            tag = root["meshes"][m].get("extras", {}).pop("zstudio_mesh_tag", None)
            for p, primitive in enumerate(mesh.primitives):
                indices = BinaryData.decode_accessor(reader, primitive.indices).reshape(-1).tolist()
                attributes = {}
                for name, accessor in primitive.attributes.items():
                    if name.lower().startswith("_recoil_"):
                        attributes[name.lower()] = BinaryData.decode_accessor(reader, accessor).reshape(-1).tolist()
                if (mesh.extras or {}).get("zstudio_blender", -1) >= 0 and not all(name in attributes for name in ("_recoil_origin", "_recoil_face", "_recoil_count")):
                    raise ValueError("A modifier removed polygon identities or zones. Apply the geometry change explicitly and assign its new faces before exporting")
                origins, zones, faces, partitions, parents = [], [], [], [], []
                def attribute(name, vertex):
                    data = attributes.get(name)
                    return data[vertex] if data is not None else 0
                for i in range(0, len(indices), 3):
                    triangle = indices[i:i + 3]
                    values = []
                    for vertex in triangle:
                        origin = attribute("_recoil_origin", vertex)
                        count = attribute("_recoil_count", vertex)
                        if count != int(count) or not 0 <= count <= 3 or origin != int(origin):
                            raise ValueError("An edit interpolated polygon identities or zones; assign the affected faces explicitly")
                        word = [attribute("_recoil_zone" + str(z), vertex) for z in range(int(count))]
                        if any(v != int(v) or not 0 <= v <= 255 for v in word):
                            raise ValueError("An edited polygon has invalid zones")
                        values.append((int(origin) - 1, [int(v) for v in word]))
                    if values[1:] != values[:1] * 2:
                        raise ValueError("A polygon combines incompatible identities or zones; assign its faces explicitly")
                    if tag is not None and "_recoil_mesh" in attributes and values[0][0] >= 0 and int(attribute("_recoil_mesh", triangle[0])) != tag:
                        raise ValueError(f"{mesh.name or 'A mesh'} holds {api().JOINED_FACES}")
                    origins.append(values[0][0] if (mesh.extras or {}).get("zstudio_blender", -1) >= 0 else -1); zones.append(values[0][1])
                    identities = [attribute("_recoil_face", v) for v in triangle]
                    if any(v != int(v) for v in identities) or len(set(identities)) != 1:
                        raise ValueError("An edit destroyed face identity; resolve the affected geometry before export")
                    faces.append(int(identities[0]))
                    partitions.append(int(attribute("_recoil_partition", triangle[0])))
                    parents.append(api().face_ancestors(tree, int(identities[0])))
                out = root["meshes"][m]["primitives"][p]
                out.setdefault("extras", {})["zstudio_blender"] = {"origins": origins, "zones": zones, "faces": faces, "partitions": partitions, "parents": parents}
                for name in list(out["attributes"]):
                    if name.lower().startswith("_recoil_"):
                        del out["attributes"][name]
        encoded = bridge.encode_json(root)
        path.write_bytes(encoded)
    finally:
        reader.log.flush()


class RECOIL_OT_export(bpy.types.Operator):
    bl_idname = "recoil.export_gltf"
    bl_label = "Export RECOIL glTF"
    # Undoable: the editor identities the export assigns and the generation it records are kept, saved and undone together.
    bl_options = {"REGISTER", "UNDO"}
    directory: StringProperty(name="Export Folder", subtype="DIR_PATH")

    def execute(self, context):
        previous = context.window.scene
        previous_object = context.object
        previous_mode = previous_object.mode if previous_object else "OBJECT"
        staging_scene = None
        staged_objects, staged_meshes = [], []
        bindings = []
        scratch = None
        try:
            _, data = api().session(context)
            if any(api().state(s).get("zs_session") == data["id"] and "zs_placement" in api().state(s) for s in bpy.data.scenes):
                raise ValueError("Accept or cancel placement zone edits before exporting")
            if context.object and context.object.mode != "OBJECT":
                bpy.ops.object.mode_set(mode="OBJECT")
            destination = Path(self.directory or data["outbox"])
            if not str(self.directory or data["outbox"]):
                raise ValueError("Choose a separate export destination")
            scratch = Path(tempfile.mkdtemp(prefix="zstudio-blender-export-"))
            payload = {"protocol": bridge.PROTOCOL, "session": data["folder"], "import": data["id"], "lineage": list(data.get("exports", []))[-256:], "models": []}
            # Binding one file can remap accepted zones on references in another.
            # Snapshot the entire graph before the first file changes anything.
            bindings = api().binding_snapshot([api().asset_collection(data, item["path"]) for item in data["models"]])
            for item in data["models"]:
                collection = api().asset_collection(data, item["path"])
                if any(value != 0 for value in collection.instance_offset):
                    raise ValueError("Physical source collection '" + item["path"] + "' has a nonzero instance origin. Reset its Instance Offset to zero and move its objects or reference holders instead")
                api().bind_objects(collection)
                api().validate_reference_displays(data, collection)
            owned = {obj for item in data["models"] for obj in api().source_objects(api().asset_collection(data, item["path"]))}
            for scene in bpy.data.scenes:
                if api().state(scene).get("zs_session") == data["id"] and any(obj not in owned for obj in scene.objects):
                    raise ValueError("Objects outside a physical source collection cannot be exported. Move them into the intended source collection")
            assignments = placement_assignments(data)
            signatures = {}
            for item in data["models"]:
                collection = api().asset_collection(data, item["path"])
                staging_scene = bpy.data.scenes.new("RECOIL export staging")
                context.window.scene = staging_scene
                geometry_changed = bool(api().state(collection).get("zs_force_geometry"))
                copies, mesh_copies = {}, {}
                for obj in api().source_objects(collection):
                    if api().state(obj).get("zs_reference_helper"):
                        continue
                    copied = obj.copy()
                    staged_objects.append(copied)
                    copies[obj] = copied
                    copied.instance_type = "NONE"
                    copied.hide_viewport = copied.hide_render = copied.hide_select = False
                    staging_scene.collection.objects.link(copied)
                    if obj.type == "MESH":
                        api().ensure_face_ids(obj.data)
                        api().require_own_faces(obj)
                        object_material = any(s.link == "OBJECT" for s in obj.material_slots)
                        separate_mesh = object_material or bool(obj.modifiers)
                        mesh_key = (obj.data, obj if separate_mesh else None)
                        if mesh_key not in mesh_copies:
                            mesh_copies[mesh_key] = obj.data.copy()
                            api().state(mesh_copies[mesh_key])["zs_export_uid"] = api().state(obj.data)["zs_uid"] + (api().state(obj)["zs_uid"] if separate_mesh else "")
                            staged_meshes.append(mesh_copies[mesh_key])
                            partition_faces(assignments.get((item["path"], api().state(obj.data)["zs_uid"]), []), mesh_copies[mesh_key])
                        copied.data = mesh_copies[mesh_key]
                        geometry_changed |= api().mesh_signature(obj.data) != api().state(obj.data).get("zs_signature", "") or bool(obj.modifiers)
                        geometry_changed |= any(s.link == "OBJECT" or (s.material and api().material_signature(s.material, signatures) != api().state(s.material).get("zs_signature", "")) for s in obj.material_slots)
                        if obj.modifiers and obj.data.shape_keys:
                            raise ValueError("Apply modifiers explicitly before exporting a mesh with shape keys")
                for original, copied in copies.items():
                    if original.parent and original.parent not in copies:
                        raise ValueError("An object is parented outside its physical source file")
                    copied.parent = copies.get(original.parent)
                context.view_layer.update()
                path = bridge.contained(scratch, item["path"])
                path.parent.mkdir(parents=True, exist_ok=True)
                api()._exporting = collection
                result = bpy.ops.export_scene.gltf(filepath=str(path), export_format="GLTF_SEPARATE", use_selection=False,
                    use_active_scene=True, export_extras=False, export_attributes=True, export_yup=True,
                    export_animations=False, export_skins=False, export_cameras=False, export_lights=False,
                    export_morph_normal=False, export_morph_tangent=False,
                    export_apply=True, export_image_format="AUTO", export_gpu_instances=False)
                api()._exporting = None
                if "FINISHED" not in result:
                    raise ValueError("Blender could not export " + item["path"])
                annotate_polygons(path)
                for obj in staged_objects:
                    bpy.data.objects.remove(obj, do_unlink=True)
                staged_objects.clear()
                for mesh in staged_meshes:
                    bpy.data.meshes.remove(mesh)
                staged_meshes.clear()
                payload["models"].append(dict(path=item["path"], geometryChanged=geometry_changed))
                context.window.scene = previous
                bpy.data.scenes.remove(staging_scene); staging_scene = None
            bridge.write_json(scratch / "edit.json", payload)
            result = bridge.call(api().executable(context), "export", edits=str(scratch / "edit.json"), destination=str(destination))
            # Saved with the .blend: an update accepts what earlier updates applied only from exports this file (or its
            # later saves) made, so reopening an older save cannot silently undo an update applied since.
            text, data = api().session(context)
            data["exports"] = (list(data.get("exports", [])) + [Path(result["manifest"]).parent.name])[-256:]
            text.clear(); text.write(json.dumps(data))
            self.report({"INFO"}, "Exported RECOIL graph to " + result["root"])
            return {"FINISHED"}
        except Exception as error:
            api().restore_bindings(bindings)
            self.report({"ERROR"}, str(error))
            return {"CANCELLED"}
        finally:
            api()._exporting = None
            # The helper copied the export into its generation folder; the staging files are not needed again.
            if scratch is not None:
                shutil.rmtree(scratch, ignore_errors=True)
            for obj in staged_objects:
                bpy.data.objects.remove(obj, do_unlink=True)
            for mesh in staged_meshes:
                bpy.data.meshes.remove(mesh)
            context.window.scene = previous
            if staging_scene:
                bpy.data.scenes.remove(staging_scene)
            if previous_object and previous_object.name in context.view_layer.objects and previous_mode != "OBJECT":
                context.view_layer.objects.active = previous_object
                bpy.ops.object.mode_set(mode=previous_mode)

    def invoke(self, context, event):
        try:
            _, data = api().session(context)
            self.directory = data["outbox"]
        except ValueError:
            pass
        context.window_manager.fileselect_add(self)
        return {"RUNNING_MODAL"}


def placement_assignments(data):
    """Faces with different placement assignments must remain separate source polygons."""
    from .placement import resolve_chain
    assignments = {}
    for item in data["models"]:
        for owner in api().source_objects(api().asset_collection(data, item["path"])):
            edits = json.loads(api().state(owner).get("zs_placements", "[]"))
            # A placement edit through a reference removed since went with that reference.
            kept = [(edit, resolve_chain(data, owner, edit["chain"])) for edit in edits]
            kept = [(edit, target) for edit, target in kept if target is not None]
            if len(kept) != len(edits):
                api().state(owner)["zs_placements"] = json.dumps([edit for edit, _ in kept])
            for edit, target in kept:
                for mesh, words in edit["meshes"].items():
                    assignments.setdefault((target, mesh), []).append(words)
    return assignments


def partition_faces(assignments, mesh):
    groups, values = {}, []
    tree = api().face_tree(mesh)
    for face in mesh.polygons:
        identity = mesh.attributes["_recoil_face"].data[face.index].value
        key = json.dumps([api().assigned_word(a, tree, identity) for a in assignments])
        values.append(groups.setdefault(key, len(groups) + 1))
    api().face_attribute(mesh, "_recoil_partition", values)
