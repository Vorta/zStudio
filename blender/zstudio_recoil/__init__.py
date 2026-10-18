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
DESCRIPTIONS = {
    "altitudeSurface": "Include this surface in vertical ground-height queries, such as vehicle height probes",
    "intersectSurface": "Include this surface in intersection queries used for collision and obstruction checks",
    "intersectBounds": "Include this node's bounding box in bounds-intersection queries",
    "proximity": "Include this node in proximity queries that use its bounds",
    "landmark": "Treat this node as a landmark outside ordinary spatial-grid culling",
    "overwrite": "Set the engine's overwrite-node attribute; its detailed runtime effect is not established. This does not overwrite files",
    "altitudeZoneCheck": "Restrict altitude queries by this node's zone. Turning this off does not disable rendering or other zone checks",
}
_importing = None
_exporting = None


def package_id():
    return __package__


def executable(context):
    return context.preferences.addons[package_id()].preferences.zstudio_path


# Saved RNA storage is omitted by Blender's normal Custom Properties panels.
# Keep fields independent: reading one identity must not decode the entire session.
INTERNAL_TYPES = (bpy.types.Object, bpy.types.Collection, bpy.types.Scene, bpy.types.Mesh,
                  bpy.types.Material, bpy.types.Image, bpy.types.Texture, bpy.types.NodeTree)
LEGACY_KEYS = frozenset("""zs_active_file zs_engine_name zs_export_uid zs_file zs_force_geometry
    zs_metadata zs_missing_file zs_missing_pixels zs_origin zs_original_recoil zs_parent_zone
    zs_path zs_pending_baseline zs_pending_placements zs_placement zs_placements zs_proxy_behavior
    zs_proxy_evaluated zs_proxy_ids zs_proxy_materials zs_proxy_non_zone_metadata zs_proxy_parent
    zs_proxy_signature zs_proxy_transform zs_proxy_uid zs_recoil zs_reference_helper zs_return_scene
    zs_scene_extras zs_session zs_signature zs_source_path zs_surface zs_transform zs_uid
    zs_zone_proxy zs_zone_workspace zs_workspace_role""".split())
FRESH_IMPORT = "RECOIL internal state is missing or obsolete. Make a fresh import with this extension before editing or exporting"


def local_matrix(obj):
    """An object's transform relative to its parent, from its own channels (no scene evaluation needed)."""
    return [list(row) for row in (obj.matrix_parent_inverse @ obj.matrix_basis)]


def transform_changed(obj):
    """Whether the object moved since it was imported; float noise of the round trip is not a change."""
    stored = state(obj).get("zs_transform")
    if not stored:
        return True
    return any(abs(a - b) > 1e-6 * max(1.0, abs(a))
               for old, new in zip(json.loads(stored), local_matrix(obj)) for a, b in zip(old, new))


def state(owner):
    """Internal saved fields; never read legacy top-level properties as a fallback."""
    return owner.recoil_internal


def require_current(owner):
    # Only inspect extension-owned names, preserving arbitrary artist/add-on properties.
    if any(key in owner for key in LEGACY_KEYS):
        raise ValueError(FRESH_IMPORT)
    values = state(owner)
    required = {
        "object": ("zs_session", "zs_origin", "zs_file", "zs_uid", "zs_engine_name", "zs_transform", "source_metadata"),
        "mesh": ("zs_session", "zs_origin", "zs_file", "zs_uid", "zs_recoil"),
        "material": ("zs_session", "zs_origin", "zs_file", "zs_recoil", "zs_surface"),
        "collection": ("zs_session", "zs_path", "zs_metadata", "zs_scene_extras", "source_metadata"),
        "scene": ("zs_session", "zs_active_file"),
        "image": ("zs_session", "zs_source_path",),
    }.get(values.get("kind"), ())
    if any(key not in values for key in required):
        raise ValueError(FRESH_IMPORT)
    if values.get("kind") == "material" and json.loads(values["zs_surface"]).get("format") != 2:
        raise ValueError(FRESH_IMPORT)


def source_values(owner):
    require_current(owner)
    if isinstance(owner, (bpy.types.Object, bpy.types.Collection)):
        encoded = state(owner).get("source_metadata", "")
        if not encoded and any(key in state(owner) for key in ("zs_origin", "zs_file", "zs_path")):
            raise ValueError("RECOIL source metadata is missing. Make a fresh import with this extension before editing or exporting")
        return json.loads(encoded or "{}")
    return json.loads(state(owner).get("zs_recoil", "{}"))


def remove_transport_properties(owner, metadata=False):
    # The bundled glTF importer copies extras to Custom Properties before our hook.
    # These fields have already been captured in internal state, not artist extras.
    for key in (("zstudio_blender", "recoil") if metadata else ("zstudio_blender",)):
        if key in owner:
            del owner[key]


def populate(owner, values):
    settings = owner.recoil
    state(owner)["source_metadata"] = json.dumps(values)
    for key in FLAGS:
        setattr(settings, key, bool(values.get(key, False)))
    settings.declient = values.get("declient") or "UNSET"
    zone = values.get("zone", "inherit")
    settings.zone_kind = "ID" if isinstance(zone, int) else zone
    settings.zone_id = zone if isinstance(zone, int) else 0
    settings.engine_name = values.get("name", state(owner).get("zs_engine_name", ""))
    settings.reference = values.get("ref", "")
    settings.appearance = json.dumps(values["appearance"]) if "appearance" in values else ""


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
    if settings.engine_name != state(owner).get("zs_engine_name", "") or "name" in values:
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
    require_current(context.scene)
    key = state(context.scene).get("zs_session")
    if not key:
        raise ValueError("Import a RECOIL glTF first")
    text = bpy.data.texts[key]
    data = json.loads(text.as_string())
    if not (Path(data["folder"]) / "session.json").is_file():
        data["folder"] = str(bridge.restore_session(bpy.data.texts[data["baseline"]].as_string()))
        text.clear(); text.write(json.dumps(data))
    return text, data


def asset_collection(data, path):
    matches = [c for c in bpy.data.collections if state(c).get("zs_session") == data["id"] and state(c).get("zs_path", "").casefold() == path.casefold()]
    if len(matches) != 1:
        raise ValueError("The imported source collection is missing or ambiguous: " + path)
    return matches[0]


def require_graph(owner, session_id, visited=None):
    """Reject imported identities from another baseline, including indirect resources."""
    visited = set() if visited is None else visited

    def visit(value):
        if value is None or not hasattr(value, "bl_rna"):
            return
        identity = (value.bl_rna.identifier, value.as_pointer())
        if identity in visited:
            return
        visited.add(identity)
        is_id = isinstance(value, bpy.types.ID)
        if isinstance(value, INTERNAL_TYPES):
            require_current(value)
            provenance = state(value)
            if any(key in provenance for key in ("zs_file", "zs_source_path")) and not provenance.get("zs_session"):
                raise ValueError(FRESH_IMPORT)
            if provenance.get("zs_session") and provenance["zs_session"] != session_id:
                raise ValueError("RECOIL data '" + value.name + "' belongs to another imported graph. Undo the cross-graph copy and import or create the replacement within this graph")
        # Collections are reference boundaries, checked here for identity. Their
        # objects are checked once through each physical collection's preflight.
        if isinstance(value, (bpy.types.Collection, bpy.types.Scene)):
            return
        if is_id and not isinstance(value, INTERNAL_TYPES):
            return
        for prop in value.bl_rna.properties:
            key = prop.identifier
            if key in {"rna_type", "id_data", "recoil", "recoil_internal", "original", "library", "override_library", "library_weak_reference"}:
                continue
            if prop.type == 'POINTER':
                visit(getattr(value, key))
            elif prop.type == 'COLLECTION' and (not is_id or key in {"materials", "material_slots", "modifiers", "constraints", "nodes"}):
                for child in getattr(value, key):
                    visit(child)
        # Older Blender Geometry Nodes inputs are modifier IDProperties; newer
        # versions expose the same dependencies through their RNA interface.
        if isinstance(value, bpy.types.NodesModifier):
            try:
                keys = list(value.keys())
            except TypeError:
                keys = []
            for key in keys:
                if isinstance(value[key], bpy.types.ID):
                    visit(value[key])

    visit(owner)


def bind_objects(collection):
    require_current(collection)
    session_id, path = state(collection)["zs_session"], state(collection)["zs_path"]
    objects = source_objects(collection)
    visited = set()
    pending_objects, pending_meshes = [], []
    seen_objects, seen_meshes, mesh_ids = set(), set(), set()
    # Validate and stage the entire collection before assigning any identity. A
    # rejected new instance or resource must remain new so a corrected retry can
    # infer its reference and bind normally.
    for obj in objects:
        require_graph(obj, session_id, visited)
        if state(obj).get("zs_reference_helper"):
            continue
        new = "zs_file" not in state(obj)
        reference = None
        metadata = None
        if new:
            metadata = json.dumps(source_values(obj))
            if obj.instance_type == "COLLECTION":
                import posixpath
                target = obj.instance_collection
                if target is None or state(target).get("zs_session") != session_id or not state(target).get("zs_path"):
                    raise ValueError("A collection instance must refer to a source collection in this imported graph")
                require_current(target)
                reference = posixpath.relpath(state(target)["zs_path"], posixpath.dirname(path))
        elif state(obj)["zs_file"] != path:
            raise ValueError("An object belongs to another physical source. Use a collection reference instead of linking it into two source files")
        uid = state(obj).get("zs_uid")
        if not uid or uid in seen_objects:
            uid = uuid.uuid4().hex
        seen_objects.add(uid)
        pending_objects.append((obj, new, metadata, reference, uid))
        if obj.type == "MESH" and obj.data not in seen_meshes:
            mesh = obj.data
            uid = state(mesh).get("zs_uid")
            if not uid or uid in mesh_ids:
                uid = uuid.uuid4().hex
            seen_meshes.add(mesh); mesh_ids.add(uid)
            pending_meshes.append((mesh, uid))
    # A copied datablock carries its old identity. Accepted placement assignments
    # must split with it before either identity becomes independently editable.
    node_copies, mesh_copies = {}, {}
    for obj, _, _, _, uid in pending_objects:
        previous = state(obj).get("zs_uid")
        if previous and previous != uid:
            node_copies.setdefault(previous, []).append(uid)
            # The object it was copied from, which an earlier export knows: an update maps the copy through it.
            state(obj)["zs_copy_of"] = previous
    for mesh, uid in pending_meshes:
        previous = state(mesh).get("zs_uid")
        if previous and previous != uid:
            mesh_copies.setdefault(previous, []).append(uid)
    from .placement import remap_accepted_identities
    placements = remap_accepted_identities(session_id, node_copies, mesh_copies)
    for obj, new, metadata, reference, uid in pending_objects:
        if new:
            state(obj)["source_metadata"] = metadata
            state(obj)["zs_file"] = path
            state(obj)["zs_session"] = session_id
            state(obj)["zs_engine_name"] = obj.name
            if not obj.recoil.engine_name:
                obj.recoil.engine_name = obj.name
            if reference is not None:
                obj.recoil.reference = reference
        state(obj)["zs_uid"] = uid
    for mesh, uid in pending_meshes:
        state(mesh)["zs_uid"] = uid
        if "zs_origin" not in state(mesh):
            state(mesh)["zs_origin"] = -1
            state(mesh)["zs_file"] = path
            state(mesh)["zs_session"] = session_id
    for owner, encoded in placements:
        state(owner)["zs_placements"] = encoded


_BINDING_KEYS = ("source_metadata", "zs_file", "zs_session", "zs_uid", "zs_engine_name", "zs_origin", "zs_placements")


def binding_snapshot(collections):
    owners = {obj for collection in collections for obj in source_objects(collection)}
    owners.update(obj.data for obj in list(owners) if obj.type == 'MESH')
    return [(owner, {key: state(owner)[key] for key in _BINDING_KEYS if key in state(owner)},
             (owner.recoil.engine_name, owner.recoil.reference) if isinstance(owner, bpy.types.Object) else None)
            for owner in owners]


def restore_bindings(snapshot):
    for owner, values, settings in reversed(snapshot):
        for key in _BINDING_KEYS:
            if key in state(owner):
                del state(owner)[key]
        for key, value in values.items():
            state(owner)[key] = value
        if settings is not None:
            owner.recoil.engine_name, owner.recoil.reference = settings


def source_objects(collection):
    """Outliner folders organize one file; collection instances remain references."""
    if any(state(c).get("zs_path") for c in collection.children_recursive):
        raise ValueError("Use a collection reference instead of nesting a physical source collection")
    return list(collection.all_objects)


def validate_reference_displays(data, collection):
    """Every displayed reference must be represented by its source holder exactly once."""
    from .placement import target_path
    from mathutils import Matrix
    objects = source_objects(collection)
    object_set = set(objects)
    helpers = {}
    for obj in objects:
        if state(obj).get("zs_reference_helper"):
            helpers.setdefault(obj.parent, []).append(obj)
    def fail():
        raise ValueError("A RECOIL reference display was changed. Restore its collection instancing and target; mesh references need exactly one unmodified helper parented to their holder. Undo helper edits (or reimport), and move the reference holder instead")
    for parent, children in helpers.items():
        if parent not in object_set or state(parent).get("zs_reference_helper") or parent.type != 'MESH' or not parent.recoil.reference:
            fail()
    identity = Matrix.Identity(4)
    for obj in objects:
        if state(obj).get("zs_reference_helper"):
            continue
        if not obj.recoil.reference:
            if obj.instance_type != 'NONE':
                fail()
            continue
        target = asset_collection(data, target_path(data, obj))
        if obj.type == 'EMPTY':
            if obj.instance_type != 'COLLECTION' or obj.instance_collection != target or obj in helpers:
                fail()
        elif obj.type == 'MESH':
            children = helpers.get(obj, [])
            if obj.instance_type != 'NONE' or len(children) != 1:
                fail()
            helper = children[0]
            if helper.type != 'EMPTY' or helper.instance_type != 'COLLECTION' or helper.instance_collection != target \
                    or helper.parent_type != 'OBJECT' or helper.matrix_basis != identity or helper.matrix_parent_inverse != identity \
                    or helper.constraints or helper.modifiers or helper.animation_data or helper.children:
                fail()
        else:
            fail()


def effective_zone(obj, context):
    current = obj
    while current:
        if current.recoil.zone_kind != "inherit":
            return current.recoil.zone_id if current.recoil.zone_kind == "ID" else "any"
        current = current.parent
    if "zs_parent_zone" in state(context.scene):
        return state(context.scene)["zs_parent_zone"]
    _, data = session(context)
    path = state(obj).get("zs_file")
    if not path:
        target = property_target(context, obj)
        path = state(target[1])["zs_path"] if target else state(context.scene)["zs_active_file"]
    root = asset_collection(data, path).recoil
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
        h.update(repr((uv.name, uv.active_render)).encode())
        for corner in uv.data:
            h.update(repr(tuple(corner.uv)).encode())
    for normal in mesh.corner_normals:
        h.update(repr(tuple(normal.vector)).encode())
    for material in mesh.materials:
        h.update(repr((material.name_full, state(material).get("zs_origin"), state(material).get("zs_recoil")) if material else None).encode())
    if mesh.shape_keys:
        h.update(repr((mesh.shape_keys.use_relative, mesh.shape_keys.eval_time)).encode())
        key_indices = {key: i for i, key in enumerate(mesh.shape_keys.key_blocks)}
        for block in mesh.shape_keys.key_blocks:
            h.update(repr((block.name, block.value, block.mute, block.vertex_group, block.interpolation,
                           block.frame, key_indices.get(block.relative_key))).encode())
            for vertex in block.data:
                h.update(repr(tuple(vertex.co)).encode())
    return h.hexdigest()


def material_signature(material, cache=None):
    import hashlib
    from array import array
    cache = {} if cache is None else cache
    if material in cache:
        return cache[material]
    h = hashlib.sha256()
    # Inspect authored shading state rather than a list of particular node types.
    # Node position, selection and other Shader Editor presentation are not edits.
    node_ui = {'name', 'label', 'location', 'location_absolute', 'width', 'height', 'dimensions', 'select', 'hide',
               'show_options', 'show_preview', 'show_texture', 'use_custom_color', 'color', 'parent'}
    def authored_state(value, ignored=(), ancestors=()):
        identity = value.as_pointer()
        if identity in ancestors:
            return
        for prop in value.bl_rna.properties:
            key = prop.identifier
            if key in ignored or key in {'rna_type', 'recoil_internal'} or key.startswith('bl_'):
                continue
            if prop.type in {'POINTER', 'COLLECTION'}:
                current = getattr(value, key)
                if prop.type == 'COLLECTION':
                    h.update(repr((key, len(current))).encode())
                    for item in current:
                        authored_state(item, ancestors=ancestors + (identity,))
                elif isinstance(current, bpy.types.ID):
                    h.update(repr((key, current.bl_rna.identifier, current.name_full,
                                   current.library.filepath if current.library else '')).encode())
                elif current is not None:
                    h.update(key.encode())
                    authored_state(current, ancestors=ancestors + (identity,))
            elif not prop.is_readonly:
                current = getattr(value, key)
                if getattr(prop, 'is_array', False):
                    current = tuple(current)
                elif prop.type == 'ENUM' and prop.is_enum_flag:
                    current = sorted(current)
                h.update(repr((key, current)).encode())
    authored_state(material, {'name', 'use_fake_user', 'use_extra_user', 'tag', 'node_tree',
                              'preview', 'asset_data', 'animation_data', 'library', 'library_weak_reference',
                              'override_library', 'original', 'texture_paint_images', 'texture_paint_slots',
                              'paint_active_slot', 'paint_clone_slot', 'preview_render_type', 'use_preview_world'})
    h.update(state(material).get("zs_recoil", "{}").encode())
    visited = set()
    def tree_signature(tree):
        if tree in visited:
            return
        visited.add(tree)
        for node in tree.nodes:
            h.update(node.bl_idname.encode())
            authored_state(node, node_ui | {'inputs', 'outputs', 'internal_links', 'image', 'node_tree'})
            for socket in list(node.inputs) + list(node.outputs):
                value = getattr(socket, "default_value", None)
                try:
                    value = tuple(value)
                except TypeError:
                    pass
                h.update(repr((socket.identifier, value, socket.is_linked)).encode())
            if getattr(node, "image", None):
                image = node.image
                if image not in cache:
                    pixels = array('f', [0]) * len(image.pixels)
                    image.pixels.foreach_get(pixels)
                    cache[image] = hashlib.sha256(memoryview(pixels)).hexdigest()
                h.update(repr((tuple(image.size), image.colorspace_settings.name, image.alpha_mode,
                               image.source, str(Path(bpy.path.abspath(image.filepath)).resolve()), state(image).get("zs_source_path"), cache[image])).encode())
            if getattr(node, "node_tree", None):
                tree_signature(node.node_tree)
        node_indices = {node: i for i, node in enumerate(tree.nodes)}
        for link in tree.links:
            h.update(repr((node_indices[link.from_node], link.from_socket.identifier, node_indices[link.to_node],
                           link.to_socket.identifier, link.is_muted)).encode())
    if material.node_tree:
        tree_signature(material.node_tree)
    cache[material] = h.hexdigest()
    return cache[material]


def material_surface(material):
    """Appearance resolved by Blender's glTF codec, shared by import and export hooks."""
    pbr = material.pbr_metallic_roughness
    color = list(pbr.base_color_factor or [1, 1, 1, 1]) if pbr else [1, 1, 1, 1]
    mode = material.alpha_mode or 'OPAQUE'
    opacity = color[3] if mode == 'BLEND' else (
        float(color[3] >= (material.alpha_cutoff if material.alpha_cutoff is not None else .5)) if mode == 'MASK' else 1)
    return {"format": 2, "color": color[:3], "opacity": opacity, "backface": bool(material.double_sided)}


def same_surface_value(a, b):
    # Imported JSON factors pass through Blender float properties. Their harmless
    # rounding must not erase deliberately retained source material attributes.
    import math
    if isinstance(a, list) and isinstance(b, list):
        return len(a) == len(b) and all(same_surface_value(x, y) for x, y in zip(a, b))
    if isinstance(a, (int, float)) and isinstance(b, (int, float)):
        return math.isclose(a, b, rel_tol=1e-6, abs_tol=1e-6)
    return a == b


def face_attribute(mesh, name, values):
    attr = mesh.attributes.get(name) or mesh.attributes.new(name=name, type="INT", domain="FACE")
    attr.data.foreach_set("value", values)


def ensure_face_ids(mesh):
    if state(mesh).get("zs_origin", -1) < 0:
        for name in ("_recoil_origin", "_recoil_count", "_recoil_zone0", "_recoil_zone1", "_recoil_zone2"):
            if name not in mesh.attributes:
                mesh.attributes.new(name=name, type="INT", domain="FACE")
    attr = mesh.attributes.get("_recoil_face") or mesh.attributes.new(name="_recoil_face", type="INT", domain="FACE")
    # A face split from another (subdivide, knife, extrude) repeats its identity: each further piece gets one never used
    # before in this mesh, and the mesh remembers which face it was split from, so an update maps it (and its placement
    # zones) to the nearest ancestor that update knows.
    # Only a piece of the same face (the same mesh and source polygon) counts as split from it: a face joined in from
    # another mesh that happens to repeat an identity gets a fresh one and keeps its own zones.
    values = state(mesh)
    if "zs_mesh_tag" not in values:
        values["zs_mesh_tag"] = uuid.uuid4().int & 0x7FFFFF or 1
    tag = values["zs_mesh_tag"]
    marks = mesh.attributes.get("_recoil_mesh") or mesh.attributes.new(name="_recoil_mesh", type="INT", domain="FACE")
    origins = mesh.attributes.get("_recoil_origin")
    tree = json.loads(values.get("zs_face_tree", "{}"))
    next_id = max(int(values.get("zs_face_next", 0)), max((v.value for v in attr.data), default=0) + 1)
    kept = {}
    for index, value in enumerate(attr.data):
        source = (marks.data[index].value, origins.data[index].value if origins else 0)
        if value.value <= 0 or value.value in kept:
            if value.value > 0 and kept[value.value] == source:
                tree[str(next_id)] = value.value
            value.value = next_id
            next_id += 1
        kept[value.value] = source
    # Faces made in this mesh belong to it from now on.
    for mark in marks.data:
        if mark.value == 0:
            mark.value = tag
    values["zs_face_next"] = next_id
    values["zs_face_tree"] = json.dumps(tree, separators=(",", ":"))


JOINED_FACES = ("faces joined from another RECOIL mesh (Ctrl+J, or a Boolean modifier's operand), whose polygon identities "
                "and placement zones belong to that mesh. Undo the join, delete those faces, or apply the modifier and "
                "delete them, before exporting")


def require_own_faces(obj):
    """Refuses faces joined in from another RECOIL mesh: their polygon identities belong to that mesh."""
    mesh = obj.data
    tag = state(mesh).get("zs_mesh_tag")
    marks, origins = mesh.attributes.get("_recoil_mesh"), mesh.attributes.get("_recoil_origin")
    if tag is None or marks is None or origins is None:
        return
    if any(o.value > 0 and m.value != tag for m, o in zip(marks.data, origins.data)):
        raise ValueError(f"{obj.name} holds {JOINED_FACES}")


def face_tree(mesh):
    return json.loads(state(mesh).get("zs_face_tree", "{}"))


def assigned_word(assigned, tree, face):
    """A face's placement assignment, or that of the nearest face it was split from that has one."""
    for identity in [face] + face_ancestors(tree, face):
        word = assigned.get(str(identity))
        if word is not None:
            return word
    return None


def face_ancestors(tree, face):
    """The faces a face was split from, nearest first."""
    result, seen = [], {face}
    while str(face) in tree and len(result) < 64:
        face = int(tree[str(face)])
        if face in seen:
            break
        seen.add(face)
        result.append(face)
    return result


class RecoilInternalState(bpy.types.PropertyGroup):
    """Private per-datablock bookkeeping, saved and copied by Blender."""
    parent_scene: PointerProperty(type=bpy.types.Scene, options={"HIDDEN"})


def return_workspace(scene):
    values = state(scene)
    return values.get("zs_workspace_role") == "shared" or "zs_placement" in values


class RecoilSettings(bpy.types.PropertyGroup):
    engine_name: StringProperty(name="Engine Name", maxlen=35,
        description="Name used by RECOIL scripts and animation bindings; separate from Blender's object name")
    reference: StringProperty(name="Referenced glTF", description="Relative path of the shared source model used by this reference")
    declient: EnumProperty(name="Crater Mode", description="Choose one crater behavior; neither mode controls collision",
        items=[("UNSET", "Unset", "Neither crater mode; omit the declient attribute"),
               ("canModify", "Can Modify", "Allow this surface to be deformed by craters; does not control collision"),
               ("clipTo", "Clip To", "Prevent crater patches that overlap this surface in plan view; does not control collision")])
    zone_kind: EnumProperty(name="Zone", description="Use the inherited zone, match any zone, or assign an explicit zone ID",
        items=[("inherit", "Inherit", "Use the nearest explicit parent zone, or the file load-root zone"),
               ("any", "Any", "Match any zone"), ("ID", "ID", "Assign a specific zone from 0 to 254; zero is an ordinary zone")])
    zone_id: IntProperty(name="Zone ID", min=0, max=254,
        description="Explicit zone from 0 to 254. Zone 0 is an ordinary zone, not inheritance")
    appearance: StringProperty(name="Appearance", description="Object3D appearance fields; transform bookkeeping bits are derived")


for _flag in FLAGS:
    RecoilSettings.__annotations__[_flag] = BoolProperty(name=LABELS[_flag], description=DESCRIPTIONS[_flag], default=False)


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
        state(obj)["kind"] = "object"
        state(obj)["zs_origin"] = origin
        state(obj)["zs_file"] = _importing["path"]
        state(obj)["zs_session"] = _importing["session"]
        state(obj)["zs_engine_name"] = (gltf_node.extras or {}).get("recoil", {}).get("name", gltf_node.name or "")
        state(obj)["zs_uid"] = uuid.uuid4().hex
        remove_transport_properties(obj, metadata=True)
        populate(obj, (gltf_node.extras or {}).get("recoil", {}))

    def gather_import_mesh_after_hook(self, pymesh, mesh, gltf):
        if _importing is None:
            return
        index = (pymesh.extras or {}).get("zstudio_blender")
        state(mesh)["kind"] = "mesh"
        state(mesh)["zs_origin"] = index
        state(mesh)["zs_file"] = _importing["path"]
        state(mesh)["zs_session"] = _importing["session"]
        state(mesh)["zs_uid"] = uuid.uuid4().hex
        state(mesh)["zs_recoil"] = json.dumps((pymesh.extras or {}).get("recoil", {}))
        remove_transport_properties(mesh, metadata=True)
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
        state(mesh)["zs_face_next"] = len(origins) + 1
        # Which imported mesh each face came from, so faces joined in from another one are recognised.
        state(mesh)["zs_mesh_tag"] = uuid.uuid4().int & 0x7FFFFF or 1
        face_attribute(mesh, "_recoil_mesh", [state(mesh)["zs_mesh_tag"]] * len(origins))
        face_attribute(mesh, "_recoil_count", counts)
        for i in range(3):
            face_attribute(mesh, "_recoil_zone" + str(i), zones[i])
        state(mesh)["zs_signature"] = mesh_signature(mesh)

    def gather_import_material_after_hook(self, pymaterial, vertex_color, material, gltf):
        if _importing is not None:
            state(material)["kind"] = "material"
            state(material)["zs_recoil"] = json.dumps((pymaterial.extras or {}).get("recoil", {}))
            state(material)["zs_origin"] = (pymaterial.extras or {}).get("zstudio_blender", -1)
            state(material)["zs_file"] = _importing["path"]
            state(material)["zs_session"] = _importing["session"]
            state(material)["zs_surface"] = json.dumps(material_surface(pymaterial))
            remove_transport_properties(material, metadata=True)

    def gather_import_image_after_hook(self, img, image, gltf):
        if _importing is not None and image is not None:
            import posixpath
            from urllib.parse import unquote
            state(image)["kind"] = "image"
            state(image)["zs_session"] = _importing["session"]
            state(image)["zs_source_path"] = posixpath.normpath(posixpath.join(posixpath.dirname(_importing["path"]), unquote(img.uri)))
            if (img.extras or {}).get("zstudio_blender") == "missing-image":
                state(image)["zs_missing_pixels"] = json.dumps(list(image.pixels))
                state(image)["zs_missing_file"] = image.filepath
            remove_transport_properties(image)


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
                state(collection)["kind"] = "collection"
                state(collection)["zs_session"] = key
                state(collection)["zs_path"] = model["path"]
                state(collection)["zs_metadata"] = json.dumps(descriptor)
                source = descriptor["source"]
                source_scene = source.get("scenes", [{}])[source.get("scene", 0)]
                state(collection)["zs_scene_extras"] = json.dumps(source_scene.get("extras", {}))
                populate(collection, source_scene.get("extras", {}).get("recoil", {}).get("loadRoot", {"zone": "any"}))
                created.append(collection)
                old_objects = set(bpy.data.objects)
                _importing = dict(descriptor, session=key)
                result = bpy.ops.import_scene.gltf(filepath=str(session_path.parent / "input" / model["path"]),
                                                    merge_vertices=False, import_shading="NORMALS", import_scene_extras=False)
                if "FINISHED" not in result:
                    raise ValueError("Blender could not import " + model["path"])
                for obj in set(bpy.data.objects) - old_objects:
                    for old in list(obj.users_collection):
                        old.objects.unlink(obj)
                    collection.objects.link(obj)
                    # The importer sets an object's transform after its node hook: the transform as imported is read here.
                    if "zs_origin" in state(obj):
                        state(obj)["zs_transform"] = json.dumps(local_matrix(obj))
            _importing = None
            # Reference helpers are presentation only; source holders retain their own children and properties.
            by_path = {state(c)["zs_path"].lower(): c for c in created}
            for collection in created:
                for obj in list(collection.objects):
                    reference = obj.recoil.reference
                    if not reference:
                        continue
                    full = (session_path.parent / "baseline" / state(collection)["zs_path"]).parent / reference
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
                    state(instance)["zs_reference_helper"] = True
                    state(instance)["zs_session"] = key
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
            signatures = {}
            for collection in created:
                for obj in collection.objects:
                    if obj.type == "MESH":
                        state(obj.data)["zs_signature"] = mesh_signature(obj.data)
                        for material in obj.data.materials:
                            if material:
                                state(material)["zs_signature"] = material_signature(material, signatures)
                                state(material)["zs_original_recoil"] = state(material).get("zs_recoil", "{}")
            root = asset_collection(data, data["root"])
            imported_scene = bpy.data.scenes.new("RECOIL — " + Path(data["root"]).stem)
            imported_scene.collection.children.link(root)
            state(imported_scene)["kind"] = "scene"
            state(imported_scene)["zs_session"] = key
            state(imported_scene)["zs_active_file"] = data["root"]
            context.window.scene = imported_scene
            context.view_layer.active_layer_collection = context.view_layer.layer_collection.children[root.name]
            self.report({"INFO"}, "Imported RECOIL graph: " + str(len(created)) + " source files")
            if any(state(i).get("zs_missing_file") for i in bpy.data.images):
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
            if state(obj).get("zs_reference_helper"):
                obj = obj.parent
            require_graph(obj, data["id"])
            reference = obj.recoil.reference
            if not reference:
                raise ValueError("Select a glTF reference holder")
            root = Path(data["folder"]) / "baseline"
            path = ((root / state(obj)["zs_file"]).parent / reference).resolve().relative_to(root.resolve()).as_posix()
            collection = asset_collection(data, path)
            scene = bpy.data.scenes.new("RECOIL Shared — " + Path(path).stem)
            scene.collection.children.link(collection)
            state(scene)["kind"] = "scene"
            state(scene)["zs_session"] = data["id"]
            state(scene)["zs_active_file"] = path
            state(scene)["zs_workspace_role"] = "shared"
            state(scene).parent_scene = context.scene
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
        current = context.scene
        if not return_workspace(current):
            return {"CANCELLED"}
        from .placement import discard_workspace, workspace_scenes
        closing = workspace_scenes(current)
        target = state(current).parent_scene
        if target is None or target in closing or state(target).get("zs_session") != state(current).get("zs_session"):
            # Deleting a parent must not trap its draft. Prefer another workspace
            # from this graph, otherwise any surviving non-draft scene or a new one.
            available = [scene for scene in bpy.data.scenes if scene not in closing]
            preferred = [scene for scene in available if "zs_placement" not in state(scene)]
            same_graph = [scene for scene in preferred if state(scene).get("zs_session") == state(current).get("zs_session")]
            roots = [scene for scene in same_graph if not return_workspace(scene)]
            target = next(iter(roots or same_graph or preferred or available), None) or bpy.data.scenes.new("Scene")
        context.window.scene = target
        discard_workspace(current)
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

    @classmethod
    def poll(cls, context):
        if 'zs_placement' in state(context.scene) or context.object and state(context.object).get('zs_zone_proxy'):
            cls.poll_message_set("Advanced RECOIL properties require Edit Shared Model; placement workspaces edit only zones")
            return False
        return context.object is not None

    def check_context(self, context):
        if not self.poll(context):
            self.report({'ERROR'}, "Advanced RECOIL properties require Edit Shared Model; placement workspaces edit only zones")
            return False
        return True

    def owner(self, context):
        obj = context.object
        if obj and state(obj).get("zs_reference_helper"):
            obj = obj.parent
        return obj if self.target == "APPEARANCE" else obj.data if self.target == "MESH" else obj.active_material

    def bits(self):
        return range(9, 16) if self.target == "MATERIAL" else [i for i in range(32) if self.target != "APPEARANCE" or not (1 << i) & 0x39]

    def execute(self, context):
        if not self.check_context(context):
            return {'CANCELLED'}
        try:
            values = json.loads(self.value)
            if not isinstance(values, dict):
                raise ValueError("Properties must be a JSON object")
            flags = sum(1 << i for i in self.bits() if getattr(self, "bit" + str(i)))
            values["flags"] = flags if self.target == "MESH" else f"0x{flags:08X}"
            owner = self.owner(context)
            require_current(owner)
            encoded = json.dumps(values, allow_nan=False)
            if len(encoded) > 65536:
                raise ValueError("Advanced properties exceed 64 KiB")
            if self.target == "APPEARANCE":
                owner.recoil.appearance = encoded
            else:
                state(owner)["zs_recoil"] = encoded
            return {"FINISHED"}
        except Exception as error:
            self.report({"ERROR"}, str(error))
            return {"CANCELLED"}

    def invoke(self, context, event):
        if not self.check_context(context):
            return {'CANCELLED'}
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


def property_target(context, obj=None):
    """Resolve the Properties editor's (possibly pinned) object within this graph."""
    obj = context.object if obj is None else obj
    if obj and state(obj).get("zs_reference_helper"):
        obj = obj.parent
    key = state(context.scene).get("zs_session")
    if obj is None or not key:
        return None
    if "zs_placement" in state(context.scene):
        if state(obj).get("zs_zone_proxy") and context.scene.objects.get(obj.name) == obj:
            return obj, None, True
        return None
    if state(obj).get("zs_zone_proxy"):
        return None
    collections = [c for c in bpy.data.collections if state(c).get("zs_session") == key
                   and state(c).get("zs_path") and c.all_objects.get(obj.name) == obj]
    if len(collections) != 1 or state(obj).get("zs_file", state(collections[0])["zs_path"]) != state(collections[0])["zs_path"]:
        return None
    return obj, collections[0], False


def draw_settings(layout, owner, load_root=False, advanced=True):
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
    if not load_root and advanced:
        op = layout.operator("recoil.advanced_properties", text="Object Appearance…")
        op.target = "APPEARANCE"


def draw_object_settings(layout, context, target, advanced=False):
    obj, collection, placement = target
    try:
        source_values(obj)
        if collection is not None:
            source_values(collection)
    except ValueError as error:
        layout.label(text=str(error), icon="ERROR")
        return False
    if placement:
        layout.label(text="Zone edits affect this placement only")
        layout.prop(obj.recoil, "zone_kind")
        if obj.recoil.zone_kind == "ID":
            layout.prop(obj.recoil, "zone_id")
        layout.prop(obj.recoil, "altitudeZoneCheck")
    else:
        draw_settings(layout, obj, advanced=advanced)
    layout.label(text="Effective zone: " + str(effective_zone(obj, context)))
    if obj.recoil.reference:
        layout.label(text="Reference: " + obj.recoil.reference)
    return True


class RECOIL_PT_object_properties(bpy.types.Panel):
    bl_label = "RECOIL"
    bl_space_type = "PROPERTIES"
    bl_region_type = "WINDOW"
    bl_context = "object"

    @classmethod
    def poll(cls, context):
        return property_target(context) is not None

    def draw(self, context):
        target = property_target(context)
        if target:
            draw_object_settings(self.layout, context, target)


class RECOIL_PT_properties(bpy.types.Panel):
    bl_label = "RECOIL"
    bl_space_type = "VIEW_3D"
    bl_region_type = "UI"
    bl_category = "RECOIL"

    def draw(self, context):
        layout = self.layout
        try:
            require_current(context.scene)
        except ValueError as error:
            layout.label(text=str(error), icon="ERROR")
            layout.operator("recoil.import_gltf")
            return
        if "zs_session" not in state(context.scene):
            layout.operator("recoil.import_gltf")
            return
        layout.label(text=state(context.scene).get("zs_active_file", ""))
        if return_workspace(context.scene):
            layout.operator("recoil.return_to_world", text="Cancel Placement Zones" if "zs_placement" in state(context.scene) else "Return to World")
        placement = "zs_placement" in state(context.scene)
        if placement:
            layout.label(text="Zone edits affect this placement only")
            layout.operator("recoil.accept_placement")
        layout.operator("recoil.export_gltf")
        target = property_target(context)
        if target:
            obj, _, placement = target
            layout.label(text="Shared file: " + state(obj).get("zs_file", state(context.scene)["zs_active_file"]))
            if not draw_object_settings(layout, context, target, advanced=True):
                return
            if obj.recoil.reference:
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
        return "zs_session" in state(context.scene) and "zs_placement" not in state(context.scene)

    def draw(self, context):
        _, data = session(context)
        owner = asset_collection(data, state(context.scene)["zs_active_file"])
        try:
            source_values(owner)
        except ValueError as error:
            self.layout.label(text=str(error), icon="ERROR")
            return
        draw_settings(self.layout, owner, True)


from .exporter import RECOIL_OT_export, glTF2ExportUserExtension
from .placement import RECOIL_OT_edit_placement, RECOIL_OT_accept_placement

CLASSES = (RecoilInternalState, RecoilSettings, RecoilPreferences, RECOIL_OT_import, RECOIL_OT_shared_model, RECOIL_OT_return,
           RECOIL_OT_polygon_zones, RECOIL_OT_advanced, RECOIL_OT_export, RECOIL_OT_edit_placement,
           RECOIL_OT_accept_placement, RECOIL_PT_properties, RECOIL_PT_object_properties, RECOIL_PT_load_root)


def import_menu(self, context):
    self.layout.operator(RECOIL_OT_import.bl_idname, text="RECOIL glTF (.gltf)")


def export_menu(self, context):
    self.layout.operator(RECOIL_OT_export.bl_idname, text="RECOIL glTF graph")


def register():
    for cls in CLASSES:
        bpy.utils.register_class(cls)
    for kind in INTERNAL_TYPES:
        kind.recoil_internal = PointerProperty(type=RecoilInternalState, options={"HIDDEN"})
    bpy.types.Object.recoil = PointerProperty(type=RecoilSettings)
    bpy.types.Collection.recoil = PointerProperty(type=RecoilSettings)
    bpy.types.TOPBAR_MT_file_import.append(import_menu)
    bpy.types.TOPBAR_MT_file_export.append(export_menu)


def unregister():
    bpy.types.TOPBAR_MT_file_export.remove(export_menu)
    bpy.types.TOPBAR_MT_file_import.remove(import_menu)
    for kind in INTERNAL_TYPES:
        del kind.recoil_internal
    del bpy.types.Collection.recoil
    del bpy.types.Object.recoil
    for cls in reversed(CLASSES):
        bpy.utils.unregister_class(cls)
