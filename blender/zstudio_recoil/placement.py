# SPDX-License-Identifier: MIT
"""Zone-only placement workspaces. Geometry remains owned by the shared source file."""
import json
import sys
import uuid
from pathlib import Path
import bpy


def api():
    return sys.modules[__package__]


def locate(data, uid):
    found = [o for c in bpy.data.collections if c.get("zs_session") == data["id"]
             for o in c.objects if o.get("zs_uid") == uid and not o.get("zs_zone_proxy")]
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
        holder = next(o for o in collection.objects if o.get("zs_uid") == identity)
        index = holder.get("zs_origin", -1)
        scopes = [s.get("references", {}).get(str(index), {}) for s in scopes]
        scopes.append(api().collect(holder).get("zoneOverrides", {}))
        target = target_path(data, holder)
    return target, next((s for s in scopes if "fingerprint" in s), None)


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
                duplicates = [o for c in bpy.data.collections if c.get("zs_session") == data["id"] for o in c.objects if o.get("zs_uid") == owner.get("zs_uid")]
                if len(duplicates) > 1:
                    owner["zs_uid"] = uuid.uuid4().hex
            chain = parent["chain"] + [selected["zs_uid"]] if parent else []
            target, profile = profile_for(data, owner, chain)
            collection = api().asset_collection(data, target)
            api().bind_objects(collection)
            descriptor = json.loads(collection["zs_metadata"])
            created = bpy.data.scenes.new("RECOIL Placement Zones")
            state = dict(owner=owner["zs_uid"], chain=chain, target=target)
            created["zs_placement"] = json.dumps(state)
            created["zs_session"] = data["id"]
            created["zs_active_file"] = target
            created["zs_parent_zone"] = api().effective_zone(selected, context)
            created["zs_return_scene"] = previous.name
            copies, mesh_copies = {}, {}
            for obj in collection.objects:
                if obj.type == "MESH":
                    api().ensure_face_ids(obj.data)
                copied = obj.copy()
                copied["zs_zone_proxy"] = True
                copied["zs_proxy_uid"] = uuid.uuid4().hex
                if obj.type == "MESH":
                    if obj.data not in mesh_copies:
                        mesh_copies[obj.data] = obj.data.copy()
                        mesh_copies[obj.data]["zs_proxy_signature"] = api().mesh_signature(mesh_copies[obj.data])
                    copied.data = mesh_copies[obj.data]
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
            created["zs_proxy_ids"] = json.dumps([o["zs_proxy_uid"] for o in copies.values()])
            # Reopening an accepted zone workspace starts with the pending Blender edits.
            pending = json.loads(owner.get("zs_placements", "[]"))
            previous_edit = next((p for p in pending if p["chain"] == chain), None)
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
            return {"FINISHED"}
        except Exception as error:
            if created:
                for obj in list(created.objects):
                    bpy.data.objects.remove(obj, do_unlink=True)
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
            if context.object and context.object.mode != "OBJECT":
                bpy.ops.object.mode_set(mode="OBJECT")
            nodes, meshes = {}, {}
            if sorted(o.get("zs_proxy_uid", "") for o in context.scene.objects) != sorted(json.loads(context.scene["zs_proxy_ids"])):
                raise ValueError("Objects were added, deleted or duplicated in this zone workspace. Cancel and use Edit Shared Model")
            for obj in context.scene.objects:
                if json.dumps([list(row) for row in obj.matrix_local]) != obj["zs_proxy_transform"] or (obj.parent.get("zs_proxy_uid", "") if obj.parent else "") != obj["zs_proxy_parent"]:
                    raise ValueError("An object was moved or reparented in this zone workspace. Cancel and use Edit Shared Model")
                if obj.get("zs_reference_helper"):
                    continue
                settings = obj.recoil
                nodes[obj["zs_uid"]] = {"zone": settings.zone_id if settings.zone_kind == "ID" else settings.zone_kind,
                                               "altitudeZoneCheck": settings.altitudeZoneCheck}
                if obj.type == "MESH":
                    if api().mesh_signature(obj.data) != obj.data["zs_proxy_signature"]:
                        raise ValueError("Geometry changed in this zone workspace. Edit Shared Model for geometry changes")
                    words = {}
                    for face in obj.data.polygons:
                        count = obj.data.attributes["_recoil_count"].data[face.index].value
                        words[str(obj.data.attributes["_recoil_face"].data[face.index].value)] = [obj.data.attributes["_recoil_zone" + str(i)].data[face.index].value for i in range(count)]
                    meshes[obj.data["zs_uid"]] = words
            edit = dict(chain=state["chain"], nodes=nodes, meshes=meshes)
            pending = [p for p in json.loads(owner.get("zs_placements", "[]")) if p["chain"] != state["chain"]]
            pending.append(edit)
            owner["zs_placements"] = json.dumps(pending)
            # A per-triangle assignment can split an original polygon. Core remaps every other placement accordingly.
            api().asset_collection(data, state["target"])["zs_force_geometry"] = True
            return bpy.ops.recoil.return_to_world()
        except Exception as error:
            self.report({"ERROR"}, str(error))
            return {"CANCELLED"}
