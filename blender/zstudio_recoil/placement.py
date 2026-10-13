# SPDX-License-Identifier: MIT
"""Zone-only placement workspaces. Geometry remains owned by the shared source file."""
import json
import sys
import uuid
import tempfile
from pathlib import Path
import bpy


def api():
    return sys.modules[__package__]


def locate(data, uid):
    found = [o for c in bpy.data.collections if c.get("zs_session") == data["id"]
             for o in api().source_objects(c) if o.get("zs_uid") == uid and not o.get("zs_zone_proxy")]
    if len(found) != 1:
        raise ValueError("The placement owner was deleted or duplicated ambiguously")
    return found[0]


def target_path(data, holder):
    root = (Path(data["folder"]) / "baseline").resolve()
    relative = ((root / holder["zs_file"]).parent / holder.recoil.reference).resolve().relative_to(root).as_posix()
    return api().asset_collection(data, relative)["zs_path"]


def profile_for(data, owner, chain):
    scopes = [api().collect(owner).get("zoneOverrides", {})]
    target = target_path(data, owner)
    for identity in chain:
        collection = api().asset_collection(data, target)
        holder = next(o for o in api().source_objects(collection) if o.get("zs_uid") == identity)
        index = holder.get("zs_origin", -1)
        scopes = [s.get("references", {}).get(str(index), {}) for s in scopes]
        scopes.append(api().collect(holder).get("zoneOverrides", {}))
        target = target_path(data, holder)
    return target, next((s for s in scopes if "fingerprint" in s), None)


def isolate_appearance(obj, token, folder, copies):
    """Copy the complete editable material graph, including images and nested groups."""
    def duplicate(block):
        if block in copies:
            return copies[block]
        copied = block.copy()
        copies[block] = copied
        copies[copied] = copied
        copied["zs_zone_workspace"] = token
        if isinstance(copied, bpy.types.Image):
            # Image Save in the zone workspace must never overwrite the source image.
            copied.filepath = str(folder / (uuid.uuid4().hex + ".png"))
            copied.file_format = 'PNG'
            copied.pack()
        elif isinstance(copied, bpy.types.Texture) and getattr(copied, 'image', None):
            copied.image = duplicate(copied.image)
        tree = copied.node_tree if isinstance(copied, bpy.types.Material) else copied if isinstance(copied, bpy.types.NodeTree) else None
        if tree:
            for node in tree.nodes:
                if getattr(node, "image", None):
                    node.image = duplicate(node.image)
                if getattr(node, "node_tree", None):
                    node.node_tree = duplicate(node.node_tree)
        return copied
    for slot in obj.material_slots:
        if slot.material:
            slot.material = duplicate(slot.material)
    for modifier in obj.modifiers:
        if modifier.type == 'NODES' and modifier.node_group:
            modifier.node_group = duplicate(modifier.node_group)
            for key in modifier.keys():
                value = modifier[key]
                if isinstance(value, (bpy.types.Material, bpy.types.Texture, bpy.types.Image, bpy.types.NodeTree)):
                    modifier[key] = duplicate(value)
        if getattr(modifier, 'texture', None):
            modifier.texture = duplicate(modifier.texture)


def discard_workspace(scene):
    token = scene.get("zs_zone_workspace")
    for child in list(bpy.data.scenes):
        if child.get('zs_return_scene') == scene.name and 'zs_placement' in child:
            discard_workspace(child)
            bpy.data.scenes.remove(child)
    for obj in list(scene.objects):
        mesh = obj.data if obj.type == 'MESH' else None
        bpy.data.objects.remove(obj, do_unlink=True)
        if mesh and mesh.users == 0:
            bpy.data.meshes.remove(mesh)
    for kind in ('materials', 'node_groups', 'textures', 'images'):
        blocks = getattr(bpy.data, kind)
        for block in list(blocks):
            if token and block.get("zs_zone_workspace") == token:
                blocks.remove(block, do_unlink=True)


def behavior_signature(obj):
    """Snapshot editable modifier/constraint and instancing settings, not runtime caches."""
    def fields(value):
        result = {}
        for prop in value.bl_rna.properties:
            if prop.identifier == 'rna_type' or prop.is_readonly and prop.type != 'COLLECTION':
                continue
            current = getattr(value, prop.identifier)
            if prop.type == 'COLLECTION':
                result[prop.identifier] = [fields(item) for item in current]
            elif prop.type == 'POINTER':
                result[prop.identifier] = (current.bl_rna.identifier, current.name_full, current.library.filepath if current.library else '') if isinstance(current, bpy.types.ID) else None
            elif getattr(prop, 'is_array', False):
                result[prop.identifier] = list(current)
            elif prop.type == 'ENUM' and prop.is_enum_flag:
                result[prop.identifier] = sorted(current)
            else:
                result[prop.identifier] = current
        try:
            result['custom'] = {key: str(value[key]) for key in value.keys()}
        except TypeError:
            pass
        return result
    instance = obj.instance_collection
    return json.dumps(dict(modifiers=[fields(m) for m in obj.modifiers], constraints=[fields(c) for c in obj.constraints],
                           instance=(obj.instance_type, instance.name_full if instance else None, list(instance.instance_offset) if instance else None)), sort_keys=True)


def evaluated_signature(obj, depsgraph):
    evaluated = obj.evaluated_get(depsgraph)
    matrix = [list(row) for row in evaluated.matrix_world]
    if obj.type != 'MESH':
        return json.dumps(matrix)
    try:
        mesh = evaluated.to_mesh(preserve_all_data_layers=True, depsgraph=depsgraph)
        return json.dumps((matrix, api().mesh_signature(mesh)))
    finally:
        evaluated.to_mesh_clear()


def non_zone_metadata(obj):
    values = api().collect(obj)
    values.pop('zone', None)
    values.pop('altitudeZoneCheck', None)
    return json.dumps(dict(node=values, mesh=api().source_values(obj.data) if obj.type == 'MESH' else None), sort_keys=True)


class RECOIL_OT_edit_placement(bpy.types.Operator):
    bl_idname = "recoil.edit_placement"
    bl_label = "Edit Placement Zones"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        created = None
        previous = context.scene
        try:
            _, data = api().session(context)
            selected = context.object
            if selected is None:
                raise ValueError("Select a reference holder")
            if selected.get("zs_reference_helper"):
                selected = selected.parent
            if not selected.recoil.reference:
                raise ValueError("Select a reference holder")
            parent = json.loads(previous["zs_placement"]) if "zs_placement" in previous else None
            owner = locate(data, parent["owner"]) if parent else selected
            if parent is None:
                api().bind_objects(api().asset_collection(data, previous["zs_active_file"]))
                # Blender duplicates custom properties together with the object. A newly
                # opened placement receives its own editor identity without changing provenance.
                duplicates = [o for c in bpy.data.collections if c.get("zs_session") == data["id"] for o in api().source_objects(c) if o.get("zs_uid") == owner.get("zs_uid")]
                if len(duplicates) > 1:
                    owner["zs_uid"] = uuid.uuid4().hex
            chain = parent["chain"] + [selected["zs_uid"]] if parent else []
            target, profile = profile_for(data, owner, chain)
            collection = api().asset_collection(data, target)
            api().bind_objects(collection)
            api().validate_reference_displays(data, collection)
            descriptor = json.loads(collection["zs_metadata"])
            created = bpy.data.scenes.new("RECOIL Placement Zones")
            created["zs_zone_workspace"] = uuid.uuid4().hex
            image_folder = Path(tempfile.mkdtemp(prefix="zstudio-zone-images-"))
            state = dict(owner=owner["zs_uid"], chain=chain, target=target)
            created["zs_placement"] = json.dumps(state)
            created["zs_session"] = data["id"]
            created["zs_active_file"] = target
            created["zs_parent_zone"] = api().effective_zone(selected, context)
            created["zs_return_scene"] = previous.name
            pending = previous.get("zs_pending_placements", "[]") if parent else owner.get("zs_placements", "[]")
            created["zs_pending_placements"] = pending
            created["zs_pending_baseline"] = pending
            copies, mesh_copies, appearance_copies = {}, {}, {}
            for obj in api().source_objects(collection):
                if obj.type == "MESH":
                    api().ensure_face_ids(obj.data)
                copied = obj.copy()
                copied["zs_zone_proxy"] = True
                copied["zs_proxy_uid"] = uuid.uuid4().hex
                if obj.type == "MESH":
                    if obj.data not in mesh_copies:
                        mesh_copies[obj.data] = obj.data.copy()
                    copied.data = mesh_copies[obj.data]
                isolate_appearance(copied, created["zs_zone_workspace"], image_folder, appearance_copies)
                created.collection.objects.link(copied)
                copies[obj] = copied
                if profile and obj.get("zs_origin", -1) >= 0:
                    ordinal = descriptor["nodeOrder"].index(obj["zs_origin"])
                    assignment = profile["nodes"][ordinal]
                    values = api().collect(obj)
                    values["zone"] = assignment.get("zone", "inherit")
                    values["altitudeZoneCheck"] = assignment.get("altitudeZoneCheck", False)
                    api().populate(copied, values)
                    if copied.type == "MESH" and copied.data.get("zs_origin", -1) >= 0:
                        mesh_ordinal = next(i for i, m in enumerate(descriptor["meshes"]) if m["index"] == copied.data["zs_origin"])
                        zones = profile["meshes"][mesh_ordinal]["polygonZones"]
                        assigned = [(zones[v.value - 1] or []) if 0 < v.value <= len(zones) else [] for v in copied.data.attributes["_recoil_origin"].data]
                        api().face_attribute(copied.data, "_recoil_count", [len(z) for z in assigned])
                        for i in range(3):
                            api().face_attribute(copied.data, "_recoil_zone" + str(i), [z[i] if len(z) > i else 0 for z in assigned])
            for original, copied in copies.items():
                copied.parent = copies.get(original.parent)
                copied["zs_proxy_transform"] = json.dumps([list(row) for row in copied.matrix_local])
                copied["zs_proxy_parent"] = copied.parent.get("zs_proxy_uid", "") if copied.parent else ""
                if copied.type == 'MESH':
                    copied.data["zs_proxy_signature"] = api().mesh_signature(copied.data)
                    copied["zs_proxy_materials"] = json.dumps([api().material_signature(s.material) if s.material else None for s in copied.material_slots])
            created["zs_proxy_ids"] = json.dumps([o["zs_proxy_uid"] for o in copies.values()])
            # Reopening an accepted zone workspace starts with the pending Blender edits.
            previous_edit = next((p for p in json.loads(pending) if p["chain"] == chain), None)
            if previous_edit:
                for copied in copies.values():
                    if copied.get("zs_reference_helper"):
                        continue
                    origin = copied["zs_uid"]
                    if origin in previous_edit["nodes"]:
                        values = api().collect(copied); values.update(previous_edit["nodes"][origin]); api().populate(copied, values)
                    if copied.type == "MESH":
                        assigned = previous_edit["meshes"].get(copied.data["zs_uid"], {})
                        face_ids = copied.data.attributes["_recoil_face"]
                        for face in copied.data.polygons:
                            word = assigned.get(str(face_ids.data[face.index].value))
                            if word is None:
                                continue
                            copied.data.attributes["_recoil_count"].data[face.index].value = len(word)
                            for i in range(3):
                                copied.data.attributes["_recoil_zone" + str(i)].data[face.index].value = word[i] if i < len(word) else 0
            context.window.scene = created
            context.view_layer.update()
            depsgraph = context.evaluated_depsgraph_get()
            for copied in copies.values():
                copied["zs_proxy_behavior"] = behavior_signature(copied)
                copied["zs_proxy_evaluated"] = evaluated_signature(copied, depsgraph)
                copied["zs_proxy_non_zone_metadata"] = non_zone_metadata(copied)
            return {"FINISHED"}
        except Exception as error:
            context.window.scene = previous
            if created:
                discard_workspace(created)
                bpy.data.scenes.remove(created)
            self.report({"ERROR"}, str(error))
            return {"CANCELLED"}


class RECOIL_OT_accept_placement(bpy.types.Operator):
    bl_idname = "recoil.accept_placement"
    bl_label = "Accept Placement Zones"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        try:
            _, data = api().session(context)
            state = json.loads(context.scene["zs_placement"])
            owner = locate(data, state["owner"])
            if any(s.get('zs_return_scene') == context.scene.name and 'zs_placement' in s for s in bpy.data.scenes):
                raise ValueError("Accept or cancel the nested placement draft before accepting its parent")
            if context.object and context.object.mode != "OBJECT":
                bpy.ops.object.mode_set(mode="OBJECT")
            context.view_layer.update()
            depsgraph = context.evaluated_depsgraph_get()
            nodes, meshes = {}, {}
            if sorted(o.get("zs_proxy_uid", "") for o in context.scene.objects) != sorted(json.loads(context.scene["zs_proxy_ids"])):
                raise ValueError("Objects were added, deleted or duplicated in this zone workspace. Cancel and use Edit Shared Model")
            for obj in context.scene.objects:
                if json.dumps([list(row) for row in obj.matrix_local]) != obj["zs_proxy_transform"] or (obj.parent.get("zs_proxy_uid", "") if obj.parent else "") != obj["zs_proxy_parent"]:
                    raise ValueError("An object was moved or reparented in this zone workspace. Cancel and use Edit Shared Model")
                if behavior_signature(obj) != obj["zs_proxy_behavior"] or evaluated_signature(obj, depsgraph) != obj["zs_proxy_evaluated"]:
                    raise ValueError("Modifiers, constraints or instancing changed in this zone workspace. Cancel and use Edit Shared Model")
                if obj.get("zs_reference_helper"):
                    continue
                if "zs_proxy_non_zone_metadata" not in obj:
                    raise ValueError("This placement draft has no original RECOIL property snapshot. Cancel and reopen the zone workspace")
                if non_zone_metadata(obj) != obj.get("zs_proxy_non_zone_metadata"):
                    raise ValueError("Non-zone RECOIL properties changed in this zone workspace. Cancel and use Edit Shared Model for flags, appearance or mesh properties")
                settings = obj.recoil
                nodes[obj["zs_uid"]] = {"zone": settings.zone_id if settings.zone_kind == "ID" else settings.zone_kind,
                                               "altitudeZoneCheck": settings.altitudeZoneCheck}
                if obj.type == "MESH":
                    if api().mesh_signature(obj.data) != obj.data["zs_proxy_signature"]:
                        raise ValueError("Geometry changed in this zone workspace. Edit Shared Model for geometry changes")
                    if json.dumps([api().material_signature(s.material) if s.material else None for s in obj.material_slots]) != obj["zs_proxy_materials"]:
                        raise ValueError("Materials or images changed in this zone workspace. Cancel and use Edit Shared Model")
                    words = {}
                    for face in obj.data.polygons:
                        count = obj.data.attributes["_recoil_count"].data[face.index].value
                        words[str(obj.data.attributes["_recoil_face"].data[face.index].value)] = [obj.data.attributes["_recoil_zone" + str(i)].data[face.index].value for i in range(count)]
                    meshes[obj.data["zs_uid"]] = words
            edit = dict(chain=state["chain"], nodes=nodes, meshes=meshes)
            parent = bpy.data.scenes.get(context.scene.get("zs_return_scene", ""))
            if parent is None:
                raise ValueError("The parent workspace was closed. Cancel this placement draft")
            nested = "zs_placement" in parent
            target = parent if nested else owner
            field = "zs_pending_placements" if nested else "zs_placements"
            if target.get(field, "[]") != context.scene["zs_pending_baseline"]:
                raise ValueError("The placement was edited in another workspace. Cancel and reopen this draft")
            pending = [p for p in json.loads(context.scene["zs_pending_placements"]) if p["chain"] != state["chain"]]
            pending.append(edit)
            if not nested:
                # Only the outermost Accept publishes the entire draft and its split requirements.
                changed_collections = [api().asset_collection(data, profile_for(data, owner, p["chain"])[0]) for p in pending]
                for collection in changed_collections:
                    collection["zs_force_geometry"] = True
            target[field] = json.dumps(pending)
            return bpy.ops.recoil.return_to_world()
        except Exception as error:
            self.report({"ERROR"}, str(error))
            return {"CANCELLED"}
