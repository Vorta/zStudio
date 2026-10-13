# SPDX-License-Identifier: MIT
"""RECOIL metadata adapter for Blender's bundled glTF importer/exporter."""
import json
import math
import uuid
from pathlib import Path

import bpy
import bmesh
from bpy.props import BoolProperty, EnumProperty, IntProperty, StringProperty, PointerProperty
from bpy_extras.io_utils import ImportHelper, ExportHelper

from . import bridge

FLAGS = ("altitudeSurface", "intersectSurface", "intersectBounds", "proximity",
         "landmark", "overwrite", "altitudeZoneCheck")
LABELS = {"altitudeSurface": "Altitude Surface", "intersectSurface": "Intersect Surface",
          "intersectBounds": "Intersect Bounds", "proximity": "Proximity", "landmark": "Landmark",
          "overwrite": "Overwrite", "altitudeZoneCheck": "Altitude Zone Check"}
_importing = None
_exporting = None


def package_id():
    return __package__


def executable(context):
    return context.preferences.addons[package_id()].preferences.zstudio_path


def source_values(owner):
    return json.loads(owner.get("zs_recoil", "{}"))


def populate(owner, values):
    owner["zs_recoil"] = json.dumps(values)
    settings = owner.recoil
    for key in FLAGS:
        setattr(settings, key, bool(values.get(key, False)))
    settings.declient = values.get("declient") or "UNSET"
    zone = values.get("zone", "inherit")
    settings.zone_kind = "ID" if isinstance(zone, int) else zone
    settings.zone_id = zone if isinstance(zone, int) else 0
    settings.engine_name = values.get("name", owner.get("zs_engine_name", ""))
    settings.reference = values.get("ref", "")
    if "appearance" in values:
        settings.appearance = json.dumps(values["appearance"])


def collect(owner):
    values = source_values(owner)
    settings = owner.recoil
    for key in FLAGS:
        if getattr(settings, key):
            values[key] = True
        else:
            values.pop(key, None)
    values.pop("declient", None)
    if settings.declient != "UNSET":
        values["declient"] = settings.declient
    values["zone"] = settings.zone_id if settings.zone_kind == "ID" else settings.zone_kind
    if settings.engine_name != owner.get("zs_engine_name", "") or "name" in values:
        values["name"] = settings.engine_name
    if settings.reference:
        values["ref"] = settings.reference
    else:
        values.pop("ref", None)
    if settings.appearance.strip():
        values["appearance"] = json.loads(settings.appearance)
    else:
        values.pop("appearance", None)
    return values


def session(context):
    key = context.scene.get("zs_session")
    if not key:
        raise ValueError("Import a RECOIL glTF first")
    text = bpy.data.texts[key]
    data = json.loads(text.as_string())
    if not (Path(data["folder"]) / "session.json").is_file():
        data["folder"] = str(bridge.restore_session(bpy.data.texts[data["baseline"]].as_string()))
        text.clear(); text.write(json.dumps(data))
    return text, data


def asset_collection(data, path):
    matches = [c for c in bpy.data.collections if c.get("zs_session") == data["id"] and c.get("zs_path", "").casefold() == path.casefold()]
    if len(matches) != 1:
        raise ValueError("The imported source collection is missing or ambiguous: " + path)
    return matches[0]


def bind_objects(collection):
    seen_objects, seen_meshes, mesh_ids = set(), {}, {}
    for obj in collection.objects:
        if obj.get("zs_reference_helper"):
            continue
        if "zs_file" not in obj:
            obj["zs_file"] = collection["zs_path"]
            obj["zs_uid"] = uuid.uuid4().hex
            obj["zs_engine_name"] = obj.name
            if not obj.recoil.engine_name:
                obj.recoil.engine_name = obj.name
            if obj.instance_type == "COLLECTION":
                import posixpath
                target = obj.instance_collection
                if target is None or target.get("zs_session") != collection["zs_session"]:
                    raise ValueError("A collection instance must refer to a source collection in this imported graph")
                obj.recoil.reference = posixpath.relpath(target["zs_path"], posixpath.dirname(collection["zs_path"]))
        elif obj["zs_file"] != collection["zs_path"]:
            raise ValueError("An object belongs to another physical source. Use a collection reference instead of linking it into two source files")
        if not obj.get("zs_uid") or obj["zs_uid"] in seen_objects:
            obj["zs_uid"] = uuid.uuid4().hex
        seen_objects.add(obj["zs_uid"])
        if obj.type == "MESH" and obj.data not in seen_meshes:
            mesh = obj.data
            if not mesh.get("zs_uid") or mesh["zs_uid"] in mesh_ids:
                mesh["zs_uid"] = uuid.uuid4().hex
            seen_meshes[mesh] = mesh["zs_uid"]; mesh_ids[mesh["zs_uid"]] = mesh
            if "zs_origin" not in mesh:
                mesh["zs_origin"] = -1
                mesh["zs_file"] = collection["zs_path"]


def effective_zone(obj, context):
    current = obj
    while current:
        if current.recoil.zone_kind != "inherit":
            return current.recoil.zone_id if current.recoil.zone_kind == "ID" else "any"
        current = current.parent
    if "zs_parent_zone" in context.scene:
        return context.scene["zs_parent_zone"]
    _, data = session(context)
    root = asset_collection(data, context.scene["zs_active_file"]).recoil
    return root.zone_id if root.zone_kind == "ID" else "any"


def mesh_signature(mesh):
    # Includes topology and all artist-visible geometric data. Provenance and zones are metadata.
    import hashlib
    h = hashlib.sha256()
    for vertex in mesh.vertices:
        h.update(repr(tuple(vertex.co)).encode())
    for polygon in mesh.polygons:
        h.update(repr((tuple(polygon.vertices), polygon.material_index, polygon.use_smooth)).encode())
    for uv in mesh.uv_layers:
        for corner in uv.data:
            h.update(repr(tuple(corner.uv)).encode())
    for normal in mesh.corner_normals:
        h.update(repr(tuple(normal.vector)).encode())
    for material in mesh.materials:
        h.update(repr((material.name_full, material.get("zs_origin"), material.get("zs_recoil")) if material else None).encode())
    if mesh.shape_keys:
        for block in mesh.shape_keys.key_blocks:
            h.update(repr((block.name, block.value)).encode())
            for vertex in block.data:
                h.update(repr(tuple(vertex.co)).encode())
    return h.hexdigest()


def material_signature(material):
    import hashlib
    h = hashlib.sha256()
    h.update(repr((tuple(material.diffuse_color), material.use_backface_culling, material.surface_render_method)).encode())
    if material.node_tree:
        for node in material.node_tree.nodes:
            h.update(node.bl_idname.encode())
            for socket in node.inputs:
                value = getattr(socket, "default_value", None)
                try:
                    value = tuple(value)
                except TypeError:
                    pass
                h.update(repr((socket.identifier, value, socket.is_linked)).encode())
            if getattr(node, "image", None):
                image = node.image
                h.update(repr((image.name, image.filepath, tuple(image.size), image.is_dirty)).encode())
        for link in material.node_tree.links:
            h.update(repr((link.from_node.name, link.from_socket.identifier, link.to_node.name, link.to_socket.identifier)).encode())
    return h.hexdigest()


def material_surface(material):
    shader = next((n for n in material.node_tree.nodes if n.type == "BSDF_PRINCIPLED"), None) if material.node_tree else None
    return {"color": list(shader.inputs["Base Color"].default_value) if shader else list(material.diffuse_color),
            "opacity": shader.inputs["Alpha"].default_value if shader else material.diffuse_color[3],
            "backface": not material.use_backface_culling}


def face_attribute(mesh, name, values):
    attr = mesh.attributes.get(name) or mesh.attributes.new(name=name, type="INT", domain="FACE")
    attr.data.foreach_set("value", values)


def ensure_face_ids(mesh):
    if mesh.get("zs_origin", -1) < 0:
        for name in ("_recoil_origin", "_recoil_count", "_recoil_zone0", "_recoil_zone1", "_recoil_zone2"):
            if name not in mesh.attributes:
                mesh.attributes.new(name=name, type="INT", domain="FACE")
    attr = mesh.attributes.get("_recoil_face") or mesh.attributes.new(name="_recoil_face", type="INT", domain="FACE")
    next_id = max((v.value for v in attr.data), default=0) + 1
    seen = set()
    for value in attr.data:
        if value.value <= 0 or value.value in seen:
            value.value = next_id
            next_id += 1
        seen.add(value.value)


class RecoilSettings(bpy.types.PropertyGroup):
    engine_name: StringProperty(name="Engine Name", maxlen=35)
    reference: StringProperty(name="Referenced glTF")
    declient: EnumProperty(name="Declient", items=[("UNSET", "Unset", "Neither crater mode"),
                        ("canModify", "Can Modify", "Surface can be modified by craters"),
                        ("clipTo", "Clip To", "Surface clips crater deformation")])
    zone_kind: EnumProperty(name="Zone", items=[("inherit", "Inherit", "Use the parent zone"),
                        ("any", "Any", "Any zone"), ("ID", "ID", "A specific zone")])
    zone_id: IntProperty(name="Zone ID", min=0, max=254)
    appearance: StringProperty(name="Appearance", description="Object3D appearance fields; transform bookkeeping bits are derived")


for _flag in FLAGS:
    RecoilSettings.__annotations__[_flag] = BoolProperty(name=LABELS[_flag], default=False)


class RecoilPreferences(bpy.types.AddonPreferences):
    bl_idname = package_id()
    zstudio_path: StringProperty(name="zStudio executable", subtype="FILE_PATH")

    def draw(self, context):
        self.layout.prop(self, "zstudio_path")
        self.layout.label(text="Select the portable zStudio.exe. No zStudio window is required.")


class glTF2ImportUserExtension:
    is_critical = True

    def gather_import_mesh_before_hook(self, pymesh, gltf):
        if _importing is None:
            return
        # Blender validates away repeated indexed triangles. Give each corner its own
        # vertex through the bundled reader so retail duplicate faces keep their zones.
        import copy
        import numpy as np
        from io_scene_gltf2.io.imp.gltf2_io_binary import BinaryData
        for primitive in pymesh.primitives:
            indices = BinaryData.decode_accessor(gltf, primitive.indices).reshape(-1)
            triangles = [tuple(sorted(int(v) for v in indices[i:i + 3])) for i in range(0, len(indices), 3)]
            if len(set(triangles)) == len(triangles) and all(len(set(t)) == 3 for t in triangles):
                continue
            def expand(index, values=None):
                accessor = copy.copy(gltf.data.accessors[index])
                decoded = BinaryData.decode_accessor(gltf, index)[indices] if values is None else values
                accessor.count = len(decoded)
                new = len(gltf.data.accessors)
                gltf.data.accessors.append(accessor)
                gltf.decode_accessor_cache[new] = decoded
                gltf.accessor_cache[new] = decoded
                return new
            primitive.attributes = {key: expand(value) for key, value in primitive.attributes.items()}
            for target in primitive.targets or []:
                for key in target:
                    target[key] = expand(target[key])
            primitive.indices = expand(primitive.indices, np.arange(len(indices), dtype=np.uint32).reshape(-1, 1))

    def gather_import_node_after_hook(self, vnode, gltf_node, obj, gltf):
        if _importing is None:
            return
        origin = (gltf_node.extras or {}).get("zstudio_blender")
        if origin is None:
            raise ValueError("The prepared glTF lost its node identity")
        obj["zs_origin"] = origin
        obj["zs_file"] = _importing["path"]
        obj["zs_engine_name"] = (gltf_node.extras or {}).get("recoil", {}).get("name", gltf_node.name or "")
        obj["zs_uid"] = uuid.uuid4().hex
        populate(obj, (gltf_node.extras or {}).get("recoil", {}))
        obj["zs_transform"] = json.dumps([list(row) for row in obj.matrix_local])

    def gather_import_mesh_after_hook(self, pymesh, mesh, gltf):
        if _importing is None:
            return
        index = (pymesh.extras or {}).get("zstudio_blender")
        mesh["zs_origin"] = index
        mesh["zs_file"] = _importing["path"]
        mesh["zs_uid"] = uuid.uuid4().hex
        mesh["zs_recoil"] = json.dumps((pymesh.extras or {}).get("recoil", {}))
        descriptor = next((m for m in _importing["meshes"] if m["index"] == index), None)
        if descriptor is None:
            raise ValueError("No polygon identities for imported mesh")
        words = descriptor["zones"].get("polygonZones", [])
        origins, counts, zones = [], [], [[], [], []]
        ordinal = 0
        for primitive in descriptor["primitives"]:
            for triangles in primitive["triangles"]:
                assigned = words[ordinal] or []
                origins.extend([ordinal + 1] * triangles)
                counts.extend([len(assigned)] * triangles)
                for i in range(3):
                    zones[i].extend([assigned[i] if i < len(assigned) else 0] * triangles)
                ordinal += 1
        if len(mesh.polygons) != len(origins):
            raise ValueError(f"Blender changed polygon count for mesh {index}: {len(mesh.polygons)} of {len(origins)}; assignments cannot be attached safely")
        face_attribute(mesh, "_recoil_origin", origins)
        face_attribute(mesh, "_recoil_face", list(range(1, len(origins) + 1)))
        face_attribute(mesh, "_recoil_count", counts)
        for i in range(3):
            face_attribute(mesh, "_recoil_zone" + str(i), zones[i])
        mesh["zs_signature"] = mesh_signature(mesh)

    def gather_import_material_after_hook(self, pymaterial, vertex_color, material, gltf):
        if _importing is not None:
            material["zs_recoil"] = json.dumps((pymaterial.extras or {}).get("recoil", {}))
            material["zs_origin"] = (pymaterial.extras or {}).get("zstudio_blender", -1)
            material["zs_file"] = _importing["path"]

    def gather_import_image_after_hook(self, img, image, gltf):
        if _importing is not None and image is not None:
            import posixpath
            from urllib.parse import unquote
            image["zs_source_path"] = posixpath.normpath(posixpath.join(posixpath.dirname(_importing["path"]), unquote(img.uri)))
            if (img.extras or {}).get("zstudio_blender") == "missing-image":
                image["zs_missing_pixels"] = json.dumps(list(image.pixels))
                image["zs_missing_file"] = image.filepath


class RECOIL_OT_import(bpy.types.Operator, ImportHelper):
    bl_idname = "recoil.import_gltf"
    bl_label = "Import RECOIL glTF"
    bl_options = {"REGISTER", "UNDO"}
    filename_ext = ".gltf"
    filter_glob: StringProperty(default="*.gltf", options={"HIDDEN"})

    def execute(self, context):
        global _importing
        previous_scene = context.window.scene
        imported_scene = None
        created = []
        before_objects = set(bpy.data.objects)
        before_data = {kind: set(getattr(bpy.data, kind)) for kind in ("collections", "meshes", "materials", "images", "texts")}
        try:
            session_path, outbox = bridge.prepare(executable(context), self.filepath)
            manifest = bridge.read_json(session_path)
            key = "RECOIL " + uuid.uuid4().hex
            data = dict(id=key, folder=str(session_path.parent), root=manifest["root"], outbox=outbox,
                        models=manifest["models"], return_scene=previous_scene.name, baseline=key + " baseline")
            for model in manifest["models"]:
                descriptor = bridge.read_json(session_path.parent / model["metadata"])
                collection = bpy.data.collections.new(Path(model["path"]).stem)
                collection.use_fake_user = True
                collection["zs_session"] = key
                collection["zs_path"] = model["path"]
                collection["zs_metadata"] = json.dumps(descriptor)
                source = descriptor["source"]
                source_scene = source.get("scenes", [{}])[source.get("scene", 0)]
                collection["zs_scene_extras"] = json.dumps(source_scene.get("extras", {}))
                populate(collection, source_scene.get("extras", {}).get("recoil", {}).get("loadRoot", {"zone": "any"}))
                created.append(collection)
                old_objects = set(bpy.data.objects)
                _importing = descriptor
                result = bpy.ops.import_scene.gltf(filepath=str(session_path.parent / "input" / model["path"]),
                                                    merge_vertices=False, import_shading="NORMALS")
                if "FINISHED" not in result:
                    raise ValueError("Blender could not import " + model["path"])
                for obj in set(bpy.data.objects) - old_objects:
                    for old in list(obj.users_collection):
                        old.objects.unlink(obj)
                    collection.objects.link(obj)
            _importing = None
            # Reference helpers are presentation only; source holders retain their own children and properties.
            by_path = {c["zs_path"].lower(): c for c in created}
            for collection in created:
                for obj in list(collection.objects):
                    reference = obj.recoil.reference
                    if not reference:
                        continue
                    full = (session_path.parent / "baseline" / collection["zs_path"]).parent / reference
                    relative = full.resolve().relative_to((session_path.parent / "baseline").resolve()).as_posix()
                    target = by_path[relative.lower()]
                    if obj.type == "EMPTY":
                        obj.instance_type = "COLLECTION"
                        obj.instance_collection = target
                        continue
                    instance = bpy.data.objects.new("↳ " + Path(reference).stem, None)
                    instance.instance_type = "COLLECTION"
                    instance.instance_collection = target
                    instance.parent = obj
                    instance["zs_reference_helper"] = True
                    collection.objects.link(instance)
            text = bpy.data.texts.new(key)
            text.use_fake_user = True
            text.write(json.dumps(data))
            baseline = bpy.data.texts.new(data["baseline"])
            baseline.use_fake_user = True
            baseline.write(bridge.pack_session(session_path.parent))
            for image in bpy.data.images:
                if image.filepath and Path(bpy.path.abspath(image.filepath)).is_relative_to(session_path.parent):
                    image.pack()
            for collection in created:
                for obj in collection.objects:
                    if obj.type == "MESH":
                        obj.data["zs_signature"] = mesh_signature(obj.data)
                        for material in obj.data.materials:
                            if material:
                                material["zs_signature"] = material_signature(material)
                                material["zs_surface"] = json.dumps(material_surface(material))
                                material["zs_original_recoil"] = material.get("zs_recoil", "{}")
            root = asset_collection(data, data["root"])
            imported_scene = bpy.data.scenes.new("RECOIL — " + Path(data["root"]).stem)
            imported_scene.collection.children.link(root)
            imported_scene["zs_session"] = key
            imported_scene["zs_active_file"] = data["root"]
            context.window.scene = imported_scene
            context.view_layer.active_layer_collection = context.view_layer.layer_collection.children[root.name]
            self.report({"INFO"}, "Imported RECOIL graph: " + str(len(created)) + " source files")
            if any(i.get("zs_missing_file") for i in bpy.data.images):
                self.report({"WARNING"}, "Missing source images use a magenta checker in Blender; unchanged checkers are never exported as game textures")
            return {"FINISHED"}
        except Exception as error:
            _importing = None
            context.window.scene = previous_scene
            if imported_scene:
                bpy.data.scenes.remove(imported_scene)
            for obj in set(bpy.data.objects) - before_objects:
                bpy.data.objects.remove(obj, do_unlink=True)
            for kind, previous in before_data.items():
                blocks = getattr(bpy.data, kind)
                for block in set(blocks) - previous:
                    blocks.remove(block, do_unlink=True)
            self.report({"ERROR"}, str(error))
            return {"CANCELLED"}


class RECOIL_OT_shared_model(bpy.types.Operator):
    bl_idname = "recoil.edit_shared_model"
    bl_label = "Edit Shared Model"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        try:
            _, data = session(context)
            obj = context.object
            if obj.get("zs_reference_helper"):
                obj = obj.parent
            reference = obj.recoil.reference
            if not reference:
                raise ValueError("Select a glTF reference holder")
            root = Path(data["folder"]) / "baseline"
            path = ((root / obj["zs_file"]).parent / reference).resolve().relative_to(root.resolve()).as_posix()
            collection = asset_collection(data, path)
            scene = bpy.data.scenes.new("RECOIL Shared — " + Path(path).stem)
            scene.collection.children.link(collection)
            scene["zs_session"] = data["id"]
            scene["zs_active_file"] = path
            scene["zs_return_scene"] = context.scene.name
            context.window.scene = scene
            context.view_layer.active_layer_collection = context.view_layer.layer_collection.children[collection.name]
            self.report({"INFO"}, "Editing shared file " + path + "; all its references use these changes")
            return {"FINISHED"}
        except Exception as error:
            self.report({"ERROR"}, str(error))
            return {"CANCELLED"}


class RECOIL_OT_return(bpy.types.Operator):
    bl_idname = "recoil.return_to_world"
    bl_label = "Return to World"
    bl_options = {"REGISTER", "UNDO"}

    def execute(self, context):
        target = bpy.data.scenes.get(context.scene.get("zs_return_scene", ""))
        if target is None:
            return {"CANCELLED"}
        current = context.scene
        context.window.scene = target
        if "zs_placement" in current:
            for obj in list(current.objects):
                mesh = obj.data if obj.type == "MESH" else None
                bpy.data.objects.remove(obj, do_unlink=True)
                if mesh and mesh.users == 0:
                    bpy.data.meshes.remove(mesh)
        bpy.data.scenes.remove(current)
        return {"FINISHED"}


class RECOIL_OT_polygon_zones(bpy.types.Operator):
    bl_idname = "recoil.polygon_zones"
    bl_label = "Assign Polygon Zones"
    bl_options = {"REGISTER", "UNDO"}
    zones: StringProperty(name="Ordered Zones", description="Up to three IDs 0–254 or any; blank clears explicit zones")
    action: EnumProperty(items=[("ASSIGN", "Assign", "Assign selected faces"), ("SELECT", "Select", "Select matching faces")])

    def execute(self, context):
        obj = context.object
        if obj is None or obj.type != "MESH" or obj.mode != "EDIT":
            self.report({"ERROR"}, "Select faces in Edit Mode")
            return {"CANCELLED"}
        try:
            words = [255 if s.strip().lower() == "any" else int(s) for s in self.zones.split(",") if s.strip()]
            if len(words) > 3 or any(v < 0 or v > 255 for v in words):
                raise ValueError("Use at most three zone IDs, from 0 to 254, or any")
            bm = bmesh.from_edit_mesh(obj.data)
            layers = [bm.faces.layers.int.get(name) or bm.faces.layers.int.new(name)
                      for name in ("_recoil_count", "_recoil_zone0", "_recoil_zone1", "_recoil_zone2")]
            for face in bm.faces:
                if self.action == "SELECT":
                    face.select_set([face[layers[i + 1]] for i in range(face[layers[0]])] == words)
                elif face.select:
                    face[layers[0]] = len(words)
                    for i in range(3):
                        face[layers[i + 1]] = words[i] if i < len(words) else 0
            bmesh.update_edit_mesh(obj.data)
            return {"FINISHED"}
        except Exception as error:
            self.report({"ERROR"}, str(error))
            return {"CANCELLED"}

    def invoke(self, context, event):
        obj = context.object
        if obj and obj.type == "MESH" and obj.mode == "EDIT":
            bm = bmesh.from_edit_mesh(obj.data)
            face = bm.faces.active
            count = bm.faces.layers.int.get("_recoil_count")
            zones = [bm.faces.layers.int.get("_recoil_zone" + str(i)) for i in range(3)]
            if face is not None and count and all(zones):
                self.zones = ", ".join("any" if face[zones[i]] == 255 else str(face[zones[i]]) for i in range(min(3, max(0, face[count]))))
        return context.window_manager.invoke_props_dialog(self)


class RECOIL_OT_advanced(bpy.types.Operator):
    bl_idname = "recoil.advanced_properties"
    bl_label = "Edit Advanced RECOIL Properties"
    bl_options = {"REGISTER", "UNDO"}
    target: EnumProperty(items=[("MESH", "Mesh", "Mesh display, flags, scrolling and morph values"),
                               ("MATERIAL", "Material", "Material flags and surface properties"),
                               ("APPEARANCE", "Object Appearance", "Alpha override, visibility and software colour")])
    value: StringProperty(name="Other values (JSON)")

    def owner(self, context):
        obj = context.object
        if obj and obj.get("zs_reference_helper"):
            obj = obj.parent
        return obj if self.target == "APPEARANCE" else obj.data if self.target == "MESH" else obj.active_material

    def bits(self):
        return range(9, 16) if self.target == "MATERIAL" else [i for i in range(32) if self.target != "APPEARANCE" or not (1 << i) & 0x39]

    def execute(self, context):
        try:
            values = json.loads(self.value)
            if not isinstance(values, dict):
                raise ValueError("Properties must be a JSON object")
            flags = sum(1 << i for i in self.bits() if getattr(self, "bit" + str(i)))
            values["flags"] = flags if self.target == "MESH" else f"0x{flags:08X}"
            owner = self.owner(context)
            encoded = json.dumps(values, allow_nan=False)
            if len(encoded) > 65536:
                raise ValueError("Advanced properties exceed 64 KiB")
            if self.target == "APPEARANCE":
                owner.recoil.appearance = encoded
            else:
                owner["zs_recoil"] = encoded
            return {"FINISHED"}
        except Exception as error:
            self.report({"ERROR"}, str(error))
            return {"CANCELLED"}

    def invoke(self, context, event):
        owner = self.owner(context)
        if owner is None:
            return {"CANCELLED"}
        values = json.loads(owner.recoil.appearance or "{}") if self.target == "APPEARANCE" else source_values(owner)
        flags = values.pop("flags", 3 if self.target == "MESH" else 0)
        flags = int(flags, 0) if isinstance(flags, str) else flags
        for i in self.bits():
            setattr(self, "bit" + str(i), bool(flags & (1 << i)))
        self.value = json.dumps(values)
        return context.window_manager.invoke_props_dialog(self, width=650)

    def draw(self, context):
        names = {0: "Lighting", 3: "Morphing"} if self.target == "MESH" else {1: "Alpha Override", 2: "Visibility"} if self.target == "APPEARANCE" else {}
        grid = self.layout.grid_flow(columns=2, align=True)
        for i in self.bits():
            grid.prop(self, "bit" + str(i), text=names.get(i, f"Bit {i}") + f" (0x{1 << i:X})")
        self.layout.prop(self, "value")


for _bit in range(32):
    RECOIL_OT_advanced.__annotations__["bit" + str(_bit)] = BoolProperty(default=False)


def draw_settings(layout, owner, load_root=False):
    settings = owner.recoil
    if not load_root:
        layout.prop(settings, "engine_name")
    for flag in FLAGS:
        layout.prop(settings, flag)
    layout.prop(settings, "declient")
    layout.prop(settings, "zone_kind")
    if settings.zone_kind == "ID":
        layout.prop(settings, "zone_id")
    elif load_root and settings.zone_kind == "inherit":
        layout.label(text="Load roots cannot inherit a zone", icon="ERROR")
    if not load_root:
        op = layout.operator("recoil.advanced_properties", text="Object Appearance…")
        op.target = "APPEARANCE"


class RECOIL_PT_properties(bpy.types.Panel):
    bl_label = "RECOIL"
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "RECOIL"

    def draw(self, context):
        layout = self.layout
        if "zs_session" not in context.scene:
            layout.operator("recoil.import_gltf")
            return
        layout.label(text=context.scene.get("zs_active_file", ""))
        if "zs_return_scene" in context.scene:
            layout.operator("recoil.return_to_world", text="Cancel Placement Zones" if "zs_placement" in context.scene else "Return to World")
        placement = "zs_placement" in context.scene
        if placement:
            layout.label(text="Zone edits affect this placement only")
            layout.operator("recoil.accept_placement")
        layout.operator("recoil.export_gltf")
        obj = context.object
        if obj and obj.get("zs_reference_helper"):
            obj = obj.parent
        if obj and any(c.get("zs_session") == context.scene.get("zs_session") for c in obj.users_collection) or obj and obj.get("zs_zone_proxy"):
            layout.label(text="Shared file: " + obj.get("zs_file", context.scene["zs_active_file"]))
            if placement:
                layout.prop(obj.recoil, "zone_kind")
                if obj.recoil.zone_kind == "ID":
                    layout.prop(obj.recoil, "zone_id")
                layout.prop(obj.recoil, "altitudeZoneCheck")
            else:
                draw_settings(layout, obj)
            layout.label(text="Effective zone: " + str(effective_zone(obj, context)))
            if obj.recoil.reference:
                layout.label(text="Reference: " + obj.recoil.reference)
                if not placement:
                    layout.operator("recoil.edit_shared_model")
                layout.operator("recoil.edit_placement")
            if obj.type == "MESH":
                if obj.mode == "EDIT":
                    bm = bmesh.from_edit_mesh(obj.data)
                    face = bm.faces.active
                    count = bm.faces.layers.int.get("_recoil_count")
                    zones = [bm.faces.layers.int.get("_recoil_zone" + str(i)) for i in range(3)]
                    if face is not None and count and all(zones):
                        text = ", ".join("any" if face[zones[i]] == 255 else str(face[zones[i]]) for i in range(min(3, max(0, face[count]))))
                        layout.label(text="Active face zones: " + (text or "(none)"))
                layout.operator("recoil.polygon_zones")
                op = layout.operator("recoil.polygon_zones", text="Select Faces by Zones")
                op.action = "SELECT"
                if not placement:
                    op = layout.operator("recoil.advanced_properties", text="Mesh Properties")
                    op.target = "MESH"
                    if obj.active_material:
                        op = layout.operator("recoil.advanced_properties", text="Material Properties")
                        op.target = "MATERIAL"


class RECOIL_PT_load_root(bpy.types.Panel):
    bl_label = "File Load Root"
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "RECOIL"
    bl_options = {"DEFAULT_CLOSED"}

    @classmethod
    def poll(cls, context):
        return "zs_session" in context.scene and "zs_placement" not in context.scene

    def draw(self, context):
        _, data = session(context)
        draw_settings(self.layout, asset_collection(data, context.scene["zs_active_file"]), True)


from .exporter import RECOIL_OT_export, glTF2ExportUserExtension
from .placement import RECOIL_OT_edit_placement, RECOIL_OT_accept_placement

CLASSES = (RecoilSettings, RecoilPreferences, RECOIL_OT_import, RECOIL_OT_shared_model, RECOIL_OT_return,
           RECOIL_OT_polygon_zones, RECOIL_OT_advanced, RECOIL_OT_export, RECOIL_OT_edit_placement,
           RECOIL_OT_accept_placement, RECOIL_PT_properties, RECOIL_PT_load_root)


def import_menu(self, context):
    self.layout.operator(RECOIL_OT_import.bl_idname, text="RECOIL glTF (.gltf)")


def export_menu(self, context):
    self.layout.operator(RECOIL_OT_export.bl_idname, text="RECOIL glTF graph")


def register():
    for cls in CLASSES:
        bpy.utils.register_class(cls)
    bpy.types.Object.recoil = PointerProperty(type=RecoilSettings)
    bpy.types.Collection.recoil = PointerProperty(type=RecoilSettings)
    bpy.types.TOPBAR_MT_file_import.append(import_menu)
    bpy.types.TOPBAR_MT_file_export.append(export_menu)


def unregister():
    bpy.types.TOPBAR_MT_file_export.remove(export_menu)
    bpy.types.TOPBAR_MT_file_import.remove(import_menu)
    del bpy.types.Collection.recoil
    del bpy.types.Object.recoil
    for cls in reversed(CLASSES):
        bpy.utils.unregister_class(cls)
