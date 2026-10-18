# SPDX-License-Identifier: MIT
"""Zone-only placement workspaces. Geometry remains owned by the shared source file."""
import json
import sys
import uuid
import tempfile
import hashlib
from pathlib import Path
import bpy


def api():
    return sys.modules[__package__]


def remap_accepted_identities(session_id, nodes, meshes):
    """Stage copied identities and their accepted zones as one binding change."""
    if not nodes and not meshes:
        return []
    if any(api().state(scene).get('zs_session') == session_id and 'zs_placement' in api().state(scene)
           for scene in bpy.data.scenes):
        raise ValueError("Accept or cancel placement drafts before binding duplicated objects or single-user meshes")
    from itertools import product
    from . import bridge
    updates = []
    total = 0
    owners = {owner for collection in bpy.data.collections
              if api().state(collection).get('zs_session') == session_id and api().state(collection).get('zs_path')
              for owner in api().source_objects(collection)}
    for owner in owners:
        values = api().state(owner)
        if values.get('zs_session') != session_id or values.get('zs_zone_proxy') or 'zs_placements' not in values:
            continue
        raw = values['zs_placements']
        if len(raw) > bridge.MAX_JSON:
            raise ValueError("Accepted placement zones exceed 32 MiB")
        edits = json.loads(raw)
        changed = False
        output = []
        used = 2
        for edit in edits:
            # Admit duplicated assignment values before copying their dictionaries
            # or expanding reference chains. Values remain shared until encoding.
            estimate = len(bridge.encode_json(edit))
            for field, copies in (('nodes', nodes), ('meshes', meshes)):
                for old, replacements in copies.items():
                    if old in edit[field]:
                        estimate += (len(bridge.encode_json(edit[field][old])) + 40) * len(replacements)
            choices = [[identity, *nodes.get(identity, [])] for identity in edit['chain']]
            count = 1
            for choice in choices:
                count *= len(choice)
                if count * (estimate + 2) > bridge.MAX_JSON - used:
                    raise ValueError("Copied placement zones exceed 32 MiB; export smaller edits before duplicating")
            if count * (estimate + 2) > bridge.MAX_JSON - used:
                raise ValueError("Copied placement zones exceed 32 MiB; export smaller edits before duplicating")
            used += count * (estimate + 2)
            if used > 512 * 1024 * 1024 - total:
                raise ValueError("Copied placement zones exceed the operation allowance")
            transformed = dict(edit)
            for field, copies in (('nodes', nodes), ('meshes', meshes)):
                assignments = dict(edit[field])
                for old, replacements in copies.items():
                    if old in edit[field]:
                        for new in replacements:
                            assignments[new] = edit[field][old]
                        changed = True
                transformed[field] = assignments
            for chain in product(*choices):
                output.append(dict(transformed, chain=list(chain)))
            changed |= count != 1
        if changed:
            encoded = bridge.encode_json(output).decode('ascii')
            total += len(encoded)
            updates.append((owner, encoded))
    return updates


def locate(data, uid):
    found = [o for c in bpy.data.collections if api().state(c).get("zs_session") == data["id"]
             for o in api().source_objects(c) if api().state(o).get("zs_uid") == uid and not api().state(o).get("zs_zone_proxy")]
    if len(found) != 1:
        raise ValueError("The placement owner was deleted or duplicated ambiguously")
    return found[0]


def target_path(data, holder):
    root = (Path(data["folder"]) / "baseline").resolve()
    relative = ((root / api().state(holder)["zs_file"]).parent / holder.recoil.reference).resolve().relative_to(root).as_posix()
    return api().state(api().asset_collection(data, relative))["zs_path"]


def resolve_chain(data, owner, chain):
    """The file a placement edit's reference chain leads to, or None when a reference on it was removed."""
    target = target_path(data, owner)
    for identity in chain:
        holders = [o for o in api().source_objects(api().asset_collection(data, target)) if api().state(o).get("zs_uid") == identity]
        if not holders:
            return None
        if len(holders) > 1:
            raise ValueError("An edited reference was duplicated; reopen its placement zones")
        target = target_path(data, holders[0])
    return target


def profile_for(data, owner, chain):
    scopes = [api().collect(owner).get("zoneOverrides", {})]
    target = target_path(data, owner)
    for identity in chain:
        collection = api().asset_collection(data, target)
        holder = next((o for o in api().source_objects(collection) if api().state(o).get("zs_uid") == identity), None)
        if holder is None:
            raise ValueError("A reference this placement passes through was removed; its placement zones went with it")
        index = api().state(holder).get("zs_origin", -1)
        scopes = [s.get("references", {}).get(str(index), {}) for s in scopes]
        scopes.append(api().collect(holder).get("zoneOverrides", {}))
        target = target_path(data, holder)
    return target, next((s for s in scopes if "fingerprint" in s), None)


def appearance_types():
    return (bpy.types.Material, bpy.types.Texture, bpy.types.Image, bpy.types.NodeTree)


def resource_slots(value, visited=None):
    """Find editable resource references, including socket defaults and modifier RNA inputs."""
    visited = set() if visited is None else visited
    pointer = (value.bl_rna.identifier, value.as_pointer())
    if pointer in visited:
        return
    visited.add(pointer)
    for prop in value.bl_rna.properties:
        key = prop.identifier
        if key in {'rna_type', 'recoil_internal'} or key.startswith('bl_'):
            continue
        if prop.type not in {'POINTER', 'COLLECTION'}:
            continue
        current = getattr(value, key)
        if prop.type == 'COLLECTION':
            for item in current:
                # ID collections are ownership/catalog links, not editable socket values.
                if not isinstance(item, bpy.types.ID):
                    yield from resource_slots(item, visited)
        elif isinstance(current, bpy.types.ID):
            if isinstance(current, appearance_types()) and not prop.is_readonly:
                yield value, key, current, False
        elif current is not None:
            yield from resource_slots(current, visited)
    # Blender 4.x uses modifier ID properties; 5.2 uses properties.inputs RNA.
    try:
        keys = list(value.keys())
    except TypeError:
        keys = []
    for key in keys:
        current = value[key]
        if isinstance(current, appearance_types()):
            yield value, key, current, True


def isolate_appearance(obj, token, folder, copies):
    """Copy the complete editable material graph, including images and nested groups."""
    def duplicate(block):
        if block in copies:
            return copies[block]
        copied = block.copy()
        copies[block] = copied
        copies[copied] = copied
        api().state(copied)["zs_zone_workspace"] = token
        if isinstance(copied, bpy.types.Image):
            # Image Save in the zone workspace must never overwrite the source image.
            # ID.copy does not retain an edited/generated pixel buffer, and filepath
            # reloads it. Preserve those pixels explicitly before packing the private copy.
            from array import array
            pixels = array('f', [0]) * len(block.pixels)
            block.pixels.foreach_get(pixels)
            copied.filepath_raw = str(folder / (uuid.uuid4().hex + ".png"))
            copied.file_format = 'PNG'
            copied.pixels.foreach_set(pixels)
            copied.pack()
        tree = copied.node_tree if isinstance(copied, bpy.types.Material) else copied if isinstance(copied, bpy.types.NodeTree) else None
        for owner, key, resource, custom in resource_slots(tree if tree else copied):
            replacement = duplicate(resource)
            if custom:
                owner[key] = replacement
            else:
                setattr(owner, key, replacement)
        return copied
    for slot in obj.material_slots:
        if slot.material:
            slot.material = duplicate(slot.material)
    for modifier in obj.modifiers:
        for owner, key, resource, custom in resource_slots(modifier):
            replacement = duplicate(resource)
            if custom:
                owner[key] = replacement
            else:
                setattr(owner, key, replacement)


def workspace_scenes(scene):
    """Saved temporary-workspace subtree, including shared-model navigation steps."""
    scenes = {scene}
    pending = [scene]
    while pending:
        parent = pending.pop()
        for child in bpy.data.scenes:
            if child not in scenes and api().state(child).parent_scene == parent and api().return_workspace(child):
                scenes.add(child)
                pending.append(child)
    return scenes


def discard_workspace(scene):
    # Scene membership is not ownership: Link Objects to Scene (or linking a
    # collection) can bring originals into a draft. Detach only its own root links.
    scenes = workspace_scenes(scene)
    tokens = {api().state(item).get('zs_zone_workspace') for item in scenes} - {None, ''}
    for item in scenes:
        for obj in list(item.collection.objects):
            item.collection.objects.unlink(obj)
        for child in list(item.collection.children):
            item.collection.children.unlink(child)

    owned = {block for kind in ('objects', 'meshes', 'materials', 'node_groups', 'textures', 'images')
             for block in getattr(bpy.data, kind) if api().state(block).get('zs_zone_workspace') in tokens}
    # A private copy may have been adopted by another scene, object or draft. Keep
    # every externally used ID and its dependencies, even when our token remains.
    # Unknown artist-created IDs are not ours to globally delete on cancellation.
    users = bpy.data.user_map(subset=owned)
    retained = {block for block in owned if block.use_fake_user or users[block] - owned - scenes}
    dependents = {}
    for block, owners in users.items():
        for owner in owners:
            dependents.setdefault(owner, set()).add(block)
    pending = list(retained)
    while pending:
        for block in dependents.get(pending.pop(), ()):
            if block not in retained:
                retained.add(block)
                pending.append(block)
    if owned - retained:
        bpy.data.batch_remove(ids=owned - retained)
    for child in scenes - {scene}:
        bpy.data.scenes.remove(child)


def behavior_signature(obj):
    """Snapshot editable modifier/constraint and instancing settings, not runtime caches."""
    resources = {}
    def resource_state(value):
        identity = (value.bl_rna.identifier, value.name_full, value.library.filepath if value.library else '')
        if not isinstance(value, appearance_types()):
            return identity
        if value in resources:
            return identity
        resources[value] = None
        if isinstance(value, bpy.types.Image):
            from array import array
            pixels = array('f', [0]) * len(value.pixels)
            value.pixels.foreach_get(pixels)
            result = (tuple(value.size), value.colorspace_settings.name, value.alpha_mode, value.source,
                      str(Path(bpy.path.abspath(value.filepath)).resolve()), hashlib.sha256(memoryview(pixels)).hexdigest())
        elif isinstance(value, bpy.types.Material):
            result = (api().material_signature(value), resource_state(value.node_tree) if value.node_tree else None)
        elif isinstance(value, bpy.types.NodeTree):
            ui = {'name', 'label', 'location', 'location_absolute', 'width', 'height', 'select', 'hide',
                  'show_options', 'show_preview', 'show_texture', 'use_custom_color', 'color', 'parent'}
            indices = {node: i for i, node in enumerate(value.nodes)}
            result = dict(nodes=[fields(node, ignored=ui | {'internal_links'}) for node in value.nodes],
                          interface=fields(value.interface),
                          links=[(indices[link.from_node], link.from_socket.identifier,
                                  indices[link.to_node], link.to_socket.identifier, link.is_muted) for link in value.links])
        else:
            result = fields(value)
        resources[value] = result
        return identity

    def custom_state(value):
        if isinstance(value, bpy.types.ID):
            return resource_state(value)
        if hasattr(value, 'keys'):
            return {key: custom_state(value[key]) for key in value.keys()}
        if hasattr(value, 'to_list'):
            return value.to_list()
        return value

    def fields(value, ancestors=(), ignored=()):
        pointer = (value.bl_rna.identifier, value.as_pointer())
        if pointer in ancestors:
            return None
        ancestors += (pointer,)
        result = {}
        for prop in value.bl_rna.properties:
            if prop.identifier in {'rna_type', 'recoil_internal'} or prop.identifier in ignored or prop.identifier.startswith('bl_'):
                continue
            if prop.is_readonly and prop.type not in {'COLLECTION', 'POINTER'}:
                continue
            current = getattr(value, prop.identifier)
            if prop.type == 'COLLECTION':
                result[prop.identifier] = [resource_state(item) if isinstance(item, bpy.types.ID) else fields(item, ancestors) for item in current]
            elif prop.type == 'POINTER':
                if isinstance(current, bpy.types.ID):
                    if not prop.is_readonly:
                        result[prop.identifier] = resource_state(current)
                elif current is not None:
                    result[prop.identifier] = fields(current, ancestors)
            elif getattr(prop, 'is_array', False):
                result[prop.identifier] = list(current)
            elif prop.type == 'ENUM' and prop.is_enum_flag:
                result[prop.identifier] = sorted(current)
            else:
                result[prop.identifier] = current
        try:
            result['custom'] = {key: custom_state(value[key]) for key in value.keys()}
        except TypeError:
            pass
        return result
    instance = obj.instance_collection
    result = dict(modifiers=[fields(m) for m in obj.modifiers], constraints=[fields(c) for c in obj.constraints],
                  instance=(obj.instance_type, instance.name_full if instance else None, list(instance.instance_offset) if instance else None))
    result['resources'] = [(value.bl_rna.identifier, value.name_full, content) for value, content in resources.items()]
    return json.dumps(result, sort_keys=True)


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
            if api().state(selected).get("zs_reference_helper"):
                selected = selected.parent
            if not selected.recoil.reference:
                raise ValueError("Select a reference holder")
            parent = json.loads(api().state(previous)["zs_placement"]) if "zs_placement" in api().state(previous) else None
            owner = locate(data, parent["owner"]) if parent else selected
            if parent is None:
                api().bind_objects(api().asset_collection(data, api().state(previous)["zs_active_file"]))
                # Blender duplicates custom properties together with the object. A newly
                # opened placement receives its own editor identity without changing provenance.
                duplicates = [o for c in bpy.data.collections if api().state(c).get("zs_session") == data["id"] for o in api().source_objects(c) if api().state(o).get("zs_uid") == api().state(owner).get("zs_uid")]
                if len(duplicates) > 1:
                    api().state(owner)["zs_uid"] = uuid.uuid4().hex
            chain = parent["chain"] + [api().state(selected)["zs_uid"]] if parent else []
            target, profile = profile_for(data, owner, chain)
            collection = api().asset_collection(data, target)
            api().bind_objects(collection)
            api().validate_reference_displays(data, collection)
            descriptor = json.loads(api().state(collection)["zs_metadata"])
            created = bpy.data.scenes.new("RECOIL Placement Zones")
            api().state(created)["zs_zone_workspace"] = uuid.uuid4().hex
            image_folder = Path(tempfile.mkdtemp(prefix="zstudio-zone-images-"))
            state = dict(owner=api().state(owner)["zs_uid"], chain=chain, target=target)
            api().state(created)["zs_placement"] = json.dumps(state)
            api().state(created)["zs_session"] = data["id"]
            api().state(created)["zs_active_file"] = target
            api().state(created)["zs_parent_zone"] = api().effective_zone(selected, context)
            api().state(created).parent_scene = previous
            pending = api().state(previous).get("zs_pending_placements", "[]") if parent else api().state(owner).get("zs_placements", "[]")
            api().state(created)["zs_pending_placements"] = pending
            api().state(created)["zs_pending_baseline"] = pending
            copies, mesh_copies, appearance_copies = {}, {}, {}
            for obj in api().source_objects(collection):
                if obj.type == "MESH":
                    api().ensure_face_ids(obj.data)
                copied = obj.copy()
                api().state(copied)["zs_zone_workspace"] = api().state(created)["zs_zone_workspace"]
                # Register the copy before any further allocation or fallible resource work,
                # so failure cleanup owns it (and only removes a mesh after its last user).
                created.collection.objects.link(copied)
                api().state(copied)["zs_zone_proxy"] = True
                api().state(copied)["zs_proxy_uid"] = uuid.uuid4().hex
                if obj.type == "MESH":
                    if obj.data not in mesh_copies:
                        mesh_copies[obj.data] = obj.data.copy()
                        api().state(mesh_copies[obj.data])["zs_zone_workspace"] = api().state(created)["zs_zone_workspace"]
                    copied.data = mesh_copies[obj.data]
                isolate_appearance(copied, api().state(created)["zs_zone_workspace"], image_folder, appearance_copies)
                copies[obj] = copied
                if profile and api().state(obj).get("zs_origin", -1) >= 0:
                    ordinal = descriptor["nodeOrder"].index(api().state(obj)["zs_origin"])
                    assignment = profile["nodes"][ordinal]
                    values = api().collect(obj)
                    values["zone"] = assignment.get("zone", "inherit")
                    values["altitudeZoneCheck"] = assignment.get("altitudeZoneCheck", False)
                    api().populate(copied, values)
                    if copied.type == "MESH" and api().state(copied.data).get("zs_origin", -1) >= 0:
                        mesh_ordinal = next(i for i, m in enumerate(descriptor["meshes"]) if m["index"] == api().state(copied.data)["zs_origin"])
                        zones = profile["meshes"][mesh_ordinal]["polygonZones"]
                        # A face made in Blender (no source polygon) keeps the zones it was given there.
                        attrs = copied.data.attributes
                        def own(face):
                            count = attrs["_recoil_count"].data[face].value if "_recoil_count" in attrs else 0
                            return [attrs["_recoil_zone" + str(i)].data[face].value for i in range(min(count, 3)) if "_recoil_zone" + str(i) in attrs]
                        assigned = [(zones[v.value - 1] or []) if 0 < v.value <= len(zones) else own(f) for f, v in enumerate(attrs["_recoil_origin"].data)]
                        api().face_attribute(copied.data, "_recoil_count", [len(z) for z in assigned])
                        for i in range(3):
                            api().face_attribute(copied.data, "_recoil_zone" + str(i), [z[i] if len(z) > i else 0 for z in assigned])
            for original, copied in copies.items():
                copied.parent = copies.get(original.parent)
                api().state(copied)["zs_proxy_transform"] = json.dumps([list(row) for row in copied.matrix_local])
                api().state(copied)["zs_proxy_parent"] = api().state(copied.parent).get("zs_proxy_uid", "") if copied.parent else ""
                if copied.type == 'MESH':
                    api().state(copied.data)["zs_proxy_signature"] = api().mesh_signature(copied.data)
                    api().state(copied)["zs_proxy_materials"] = json.dumps([api().material_signature(s.material) if s.material else None for s in copied.material_slots])
            api().state(created)["zs_proxy_ids"] = json.dumps([api().state(o)["zs_proxy_uid"] for o in copies.values()])
            # Reopening an accepted zone workspace starts with the pending Blender edits.
            previous_edit = next((p for p in json.loads(pending) if p["chain"] == chain), None)
            if previous_edit:
                for copied in copies.values():
                    if api().state(copied).get("zs_reference_helper"):
                        continue
                    origin = api().state(copied)["zs_uid"]
                    if origin in previous_edit["nodes"]:
                        values = api().collect(copied); values.update(previous_edit["nodes"][origin]); api().populate(copied, values)
                    if copied.type == "MESH":
                        assigned = previous_edit["meshes"].get(api().state(copied.data)["zs_uid"], {})
                        face_ids = copied.data.attributes["_recoil_face"]
                        tree = api().face_tree(copied.data)
                        for face in copied.data.polygons:
                            word = api().assigned_word(assigned, tree, face_ids.data[face.index].value)
                            if word is None:
                                continue
                            copied.data.attributes["_recoil_count"].data[face.index].value = len(word)
                            for i in range(3):
                                copied.data.attributes["_recoil_zone" + str(i)].data[face.index].value = word[i] if i < len(word) else 0
            context.window.scene = created
            context.view_layer.update()
            depsgraph = context.evaluated_depsgraph_get()
            for copied in copies.values():
                api().state(copied)["zs_proxy_behavior"] = behavior_signature(copied)
                api().state(copied)["zs_proxy_evaluated"] = evaluated_signature(copied, depsgraph)
                api().state(copied)["zs_proxy_non_zone_metadata"] = non_zone_metadata(copied)
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
            state = json.loads(api().state(context.scene)["zs_placement"])
            owner = locate(data, state["owner"])
            parent = api().state(context.scene).parent_scene
            if parent is None or parent == context.scene or api().state(parent).get("zs_session") != data["id"]:
                raise ValueError("The original parent workspace is unavailable. Cancel and reopen this placement draft")
            nested = bool(state["chain"])
            parent_state = json.loads(api().state(parent)["zs_placement"]) if "zs_placement" in api().state(parent) else None
            if nested:
                if parent_state is None or parent_state["owner"] != state["owner"] or parent_state["chain"] != state["chain"][:-1]:
                    raise ValueError("This draft no longer belongs to its original nested placement. Cancel and reopen it")
            elif parent_state is not None or api().state(parent).get("zs_active_file") != api().state(owner)["zs_file"]:
                raise ValueError("This draft no longer belongs to its original source workspace. Cancel and reopen it")
            if any('zs_placement' in api().state(s) for s in workspace_scenes(context.scene) - {context.scene}):
                raise ValueError("Accept or cancel the nested placement draft before accepting its parent")
            if context.object and context.object.mode != "OBJECT":
                bpy.ops.object.mode_set(mode="OBJECT")
            context.view_layer.update()
            depsgraph = context.evaluated_depsgraph_get()
            nodes, meshes = {}, {}
            if sorted(api().state(o).get("zs_proxy_uid", "") for o in context.scene.objects) != sorted(json.loads(api().state(context.scene)["zs_proxy_ids"])):
                raise ValueError("Objects were added, deleted or duplicated in this zone workspace. Cancel and use Edit Shared Model")
            for obj in context.scene.objects:
                if json.dumps([list(row) for row in obj.matrix_local]) != api().state(obj)["zs_proxy_transform"] or (api().state(obj.parent).get("zs_proxy_uid", "") if obj.parent else "") != api().state(obj)["zs_proxy_parent"]:
                    raise ValueError("An object was moved or reparented in this zone workspace. Cancel and use Edit Shared Model")
                if behavior_signature(obj) != api().state(obj)["zs_proxy_behavior"] or evaluated_signature(obj, depsgraph) != api().state(obj)["zs_proxy_evaluated"]:
                    raise ValueError("Modifiers, constraints or instancing changed in this zone workspace. Cancel and use Edit Shared Model")
                if api().state(obj).get("zs_reference_helper"):
                    continue
                if "zs_proxy_non_zone_metadata" not in api().state(obj):
                    raise ValueError("This placement draft has no original RECOIL property snapshot. Cancel and reopen the zone workspace")
                if non_zone_metadata(obj) != api().state(obj).get("zs_proxy_non_zone_metadata"):
                    raise ValueError("Non-zone RECOIL properties changed in this zone workspace. Cancel and use Edit Shared Model for flags, appearance or mesh properties")
                settings = obj.recoil
                nodes[api().state(obj)["zs_uid"]] = {"zone": settings.zone_id if settings.zone_kind == "ID" else settings.zone_kind,
                                               "altitudeZoneCheck": settings.altitudeZoneCheck}
                if obj.type == "MESH":
                    if api().mesh_signature(obj.data) != api().state(obj.data)["zs_proxy_signature"]:
                        raise ValueError("Geometry changed in this zone workspace. Edit Shared Model for geometry changes")
                    if json.dumps([api().material_signature(s.material) if s.material else None for s in obj.material_slots]) != api().state(obj)["zs_proxy_materials"]:
                        raise ValueError("Materials or images changed in this zone workspace. Cancel and use Edit Shared Model")
                    words = {}
                    for face in obj.data.polygons:
                        count = obj.data.attributes["_recoil_count"].data[face.index].value
                        words[str(obj.data.attributes["_recoil_face"].data[face.index].value)] = [obj.data.attributes["_recoil_zone" + str(i)].data[face.index].value for i in range(count)]
                    meshes[api().state(obj.data)["zs_uid"]] = words
            edit = dict(chain=state["chain"], nodes=nodes, meshes=meshes)
            target = parent if nested else owner
            field = "zs_pending_placements" if nested else "zs_placements"
            if api().state(target).get(field, "[]") != api().state(context.scene)["zs_pending_baseline"]:
                raise ValueError("The placement was edited in another workspace. Cancel and reopen this draft")
            # Edits through a reference removed meanwhile went with it.
            pending = [p for p in json.loads(api().state(context.scene)["zs_pending_placements"]) if p["chain"] != state["chain"] and resolve_chain(data, owner, p["chain"]) is not None]
            pending.append(edit)
            if not nested:
                # Only the outermost Accept publishes the entire draft and its split requirements.
                changed_collections = [api().asset_collection(data, profile_for(data, owner, p["chain"])[0]) for p in pending]
                for collection in changed_collections:
                    api().state(collection)["zs_force_geometry"] = True
            api().state(target)[field] = json.dumps(pending)
            return bpy.ops.recoil.return_to_world()
        except Exception as error:
            self.report({"ERROR"}, str(error))
            return {"CANCELLED"}
