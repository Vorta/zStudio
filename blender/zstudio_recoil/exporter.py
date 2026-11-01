# SPDX-License-Identifier: MIT
import json
import math
import shutil
import sys
import tempfile
from pathlib import Path

import bpy
from bpy.props import StringProperty

from . import bridge

_export_poses = {}


def api():
    return sys.modules[__package__]


class glTF2ExportUserExtension:
    is_critical = True

    def gather_node_hook(self, node, obj, settings):
        if api()._exporting is None:
            return
        if api().state(obj).get("zs_reference_helper"):
            raise ValueError("A reference display helper reached the source exporter")
        # The stock exporter derives locals by inverting parent world matrices.
        # Keep the authoritative local pose instead of reintroducing precision
        # loss below a distant/scaled parent after it has already been baked.
        location, rotation, scale = pose_trs(obj, _export_poses[obj])
        if settings['gltf_yup']:
            location = (location.x, location.z, -location.y)
            rotation = (rotation.x, rotation.z, -rotation.y, rotation.w)
            scale = (scale.x, scale.z, scale.y)
        else:
            location = tuple(location)
            rotation = (rotation.x, rotation.y, rotation.z, rotation.w)
            scale = tuple(scale)
        node.matrix = None
        node.translation = list(location) if any(value != 0 for value in location) else None
        node.rotation = list(rotation) if rotation != (0, 0, 0, 1) else None
        node.scale = list(scale) if scale != (1, 1, 1) else None
        node.extras = {"recoil": api().collect(obj), "zstudio_blender": int(api().state(obj).get("zs_origin", -1)),
                       "zstudio_editor_id": api().state(obj)["zs_uid"],
                       "zstudio_transform_changed": api().transform_changed(obj, _export_poses[obj])}
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


def evaluation_dependencies(objects):
    """Include artist targets without turning them into physical-file contents."""
    result, pending = set(objects), list(objects)
    while pending:
        obj = pending.pop()
        targets = {obj.parent} if obj.parent else set()
        visited = set()

        def visit(value):
            if value is None or not hasattr(value, "bl_rna"):
                return
            if isinstance(value, bpy.types.Object):
                targets.add(value)
                return
            if isinstance(value, bpy.types.Collection):
                targets.update(value.all_objects)
                return
            if value.as_pointer() in visited:
                return
            visited.add(value.as_pointer())
            if isinstance(value, bpy.types.NodeTree):
                for node in value.nodes:
                    visit(node)
                return
            if isinstance(value, bpy.types.ID):
                return
            for prop in value.bl_rna.properties:
                if prop.identifier in {"rna_type", "id_data"}:
                    continue
                if prop.type == 'POINTER':
                    visit(getattr(value, prop.identifier))
                elif prop.type == 'COLLECTION':
                    for child in getattr(value, prop.identifier):
                        visit(child)

        for resource in [*obj.constraints, *obj.modifiers]:
            visit(resource)
        if obj.animation_data:
            for driver in obj.animation_data.drivers:
                for variable in driver.driver.variables:
                    for target in variable.targets:
                        if isinstance(target.id, bpy.types.Object):
                            targets.add(target.id)
        for target in targets - result:
            result.add(target)
            pending.append(target)
    return result


def freeze_modifier_values(evaluated, copied, visited=None):
    """Freeze evaluated modifier inputs before discarding the copy's drivers."""
    visited = set() if visited is None else visited
    if evaluated.as_pointer() in visited:
        return
    visited.add(evaluated.as_pointer())
    for prop in evaluated.bl_rna.properties:
        key = prop.identifier
        if key in {"rna_type", "id_data"}:
            continue
        value, target = getattr(evaluated, key), getattr(copied, key)
        if not prop.is_readonly and prop.is_animatable and prop.type in {'BOOLEAN', 'INT', 'FLOAT', 'ENUM'}:
            setattr(copied, key, tuple(value) if getattr(prop, "is_array", False) else value)
        elif prop.type == 'POINTER' and value is not None and target is not None and not isinstance(value, bpy.types.ID):
            freeze_modifier_values(value, target, visited)
        elif prop.type == 'COLLECTION':
            for child, destination in zip(value, target, strict=True):
                if not isinstance(child, bpy.types.ID):
                    freeze_modifier_values(child, destination, visited)
    # Blender's older Geometry Nodes input API uses numeric IDProperties.
    if isinstance(evaluated, bpy.types.Modifier):
        try:
            keys = list(evaluated.keys())
        except TypeError:
            keys = []
        for key in keys:
            value = evaluated[key]
            if isinstance(value, (bool, int, float)):
                copied[key] = value
            elif hasattr(value, "to_list"):
                copied[key] = value.to_list()


def local_location_limit_pose(evaluated):
    """Solve parented object-local location limits without world-space cancellation."""
    # Blender defines LOCAL differently for parentless objects and bone/vertex
    # parenting. Keep those and mixed constraint stacks on its evaluated path.
    if evaluated.parent is None or evaluated.parent_type != 'OBJECT':
        return None
    basis = evaluated.matrix_basis.copy()
    for constraint in evaluated.constraints:
        if constraint.mute or constraint.influence == 0 or not constraint.is_valid:
            continue
        if constraint.type != 'LIMIT_LOCATION' or constraint.owner_space != 'LOCAL':
            return None
        for index, axis in enumerate('xyz'):
            original = basis[index][3]
            limited = original
            if getattr(constraint, 'use_min_' + axis):
                limited = max(limited, getattr(constraint, 'min_' + axis))
            if getattr(constraint, 'use_max_' + axis):
                limited = min(limited, getattr(constraint, 'max_' + axis))
            basis[index][3] = original + (limited - original) * constraint.influence
    # LOCAL removes (parent.world @ parent_inverse), leaving matrix_basis.
    # The exported transform is relative to parent.world, retaining parent_inverse.
    return evaluated.matrix_parent_inverse @ basis


def pose_trs(obj, pose):
    """Preserve observable axes, then verify the actual quaternion/TRS reconstruction."""
    from mathutils import Matrix, Vector
    if any(not math.isfinite(value) for row in pose for value in row) or \
            any(abs(pose[3][i] - (i == 3)) > 1e-6 for i in range(4)):
        raise ValueError(f"{obj.name} has a non-finite or non-affine parent-relative transform. Reset its transform before exporting")

    def dot(a, b):
        return sum(x * y for x, y in zip(a, b))

    def shear():
        raise ValueError(f"{obj.name} has a parent-relative transform with shear. Apply nonuniform scale before parenting, or adjust its parenting or constraints before exporting")

    def unit(a):
        length = math.sqrt(dot(a, a))
        if length == 0 or not math.isfinite(length):
            shear()
        return tuple(value / length for value in a)

    def cross(a, b):
        return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])

    columns = [tuple(pose[row][column] for row in range(3)) for column in range(3)]
    scales = [math.sqrt(dot(column, column)) for column in columns]
    # A short column can amplify inverse-parent roundoff when normalized. Keep
    # the longest, best-conditioned observable axes and reconstruct the rest.
    present = sorted((i for i in range(3) if scales[i] != 0), key=lambda i: (-scales[i], i))
    axes = [tuple(value / scales[i] for value in columns[i]) if scales[i] else None for i in range(3)]

    if not present:
        axes = [(1, 0, 0), (0, 1, 0), (0, 0, 1)]
    else:
        first = present[0]
        second = present[1] if len(present) > 1 else (first + 1) % 3
        if len(present) == 1:
            # Choose the coordinate axis least parallel to the observable axis.
            seed = min(range(3), key=lambda i: abs(axes[first][i]))
            axes[second] = tuple(int(i == seed) for i in range(3))
        projection = dot(axes[first], axes[second])
        axes[second] = unit(tuple(b - projection * a for a, b in zip(axes[first], axes[second])))
        third = 3 - first - second
        completed = unit(cross(axes[(third + 1) % 3], axes[(third + 2) % 3]))
        # With three nonzero axes, put a reflection in the last scale while
        # keeping the quaternion basis right-handed. A missing axis has no sign.
        if len(present) == 3 and dot(completed, axes[third]) < 0:
            if third != 2:
                completed = tuple(-value for value in completed)
                axes[2] = tuple(-value for value in axes[2])
            scales[2] = -scales[2]
        axes[third] = completed
    rotation = Matrix(tuple(tuple(axes[column][row] for column in range(3)) for row in range(3))).to_quaternion()
    rotation.normalize()
    location, scale = Vector(tuple(pose[row][3] for row in range(3))), Vector(scales)
    represented = Matrix.LocRotScale(location, rotation, scale)
    for column in range(3):
        residual = math.sqrt(sum((pose[row][column] - represented[row][column]) ** 2 for row in range(3)))
        if not math.isfinite(residual) or residual > 1e-6 * max(1, abs(scales[column])):
            shear()
    return location, rotation, scale


def require_trs_pose(obj, pose):
    """Validate every sampled pose with the same conversion used to bake it."""
    pose_trs(obj, pose)
    return pose


def constrained_local_pose(obj, evaluated):
    from mathutils import Matrix, Vector
    local_limits = local_location_limit_pose(evaluated)
    pose = local_limits
    if pose is None:
        channels = Matrix(api().local_matrix(evaluated))
        parent = evaluated.parent
        expected = parent.matrix_world @ evaluated.matrix_parent_inverse @ evaluated.matrix_basis if parent else evaluated.matrix_basis
        difference = evaluated.matrix_world - expected
        # Apply the constraint's world-space delta to the exact local channels.
        # Inverting a complete parent/child world pose would introduce cancellation
        # noise for an unchanged child below a translated/scaled/rotated parent.
        pose = channels.copy()
        if any(value != 0 for row in difference for value in row):
            try:
                inverse = parent.matrix_world.to_3x3().inverted() if parent else Matrix.Identity(3)
            except ValueError as error:
                raise ValueError(f"{obj.name} has a constraint below a zero-scale parent. Give its parent nonzero scale before exporting") from error
            for column in range(4):
                delta = inverse @ Vector([difference[row][column] for row in range(3)])
                for row in range(3):
                    pose[row][column] += delta[row]
    return pose


def evaluated_parent_local_pose(obj, evaluated):
    """Include bone/vertex parent offsets in the transform relative to its object."""
    if evaluated.parent is not None and evaluated.parent_type != 'OBJECT':
        try:
            pose = evaluated.parent.matrix_world.inverted() @ evaluated.matrix_world
        except ValueError as error:
            raise ValueError(f"{obj.name} has a non-object parent with zero scale. Give its parent nonzero scale before exporting") from error
        return pose
    return constrained_local_pose(obj, evaluated) if obj.constraints else None


def empty_scene_copy(previous):
    """Keep active-scene driver settings without retaining scene membership."""
    scene = previous.copy()
    scene.name = "RECOIL export evaluation"
    try:
        for collection in list(scene.collection.children):
            scene.collection.children.unlink(collection)
        for obj in list(scene.collection.objects):
            scene.collection.objects.unlink(obj)
        return scene
    except Exception:
        bpy.data.scenes.remove(scene)
        raise


def release_evaluation(context, previous, evaluation, visibility):
    try:
        failures = []
        for obj, hidden in visibility:
            try:
                if obj.hide_viewport != hidden:
                    obj.hide_viewport = hidden
            except Exception as error:
                failures.append(str(error))
        if failures:
            raise ValueError("Could not restore target visibility: " + "; ".join(failures))
    finally:
        try:
            context.window.scene = previous
        finally:
            if evaluation is not None:
                bpy.data.scenes.remove(evaluation)


def evaluated_local_poses(context, objects, copies):
    """Sample original objects, independent of reference occurrences and export copies."""
    from mathutils import Matrix
    poses = {obj: Matrix(api().local_matrix(obj)) for obj in objects}
    if not any(obj.constraints or obj.modifiers or obj.animation_data or (obj.parent and obj.parent_type != 'OBJECT') for obj in objects):
        for obj, pose in poses.items():
            require_trs_pose(obj, pose)
        return poses, None, []
    previous = context.window.scene
    dependencies = evaluation_dependencies(objects)
    visibility = []
    evaluation = empty_scene_copy(previous)
    try:
        # A physical file may only be instanced, hidden, or no longer referenced.
        # Link its originals directly so every parent and target is evaluated in
        # the file's coordinates, without applying a placement a second time.
        for obj in dependencies:
            evaluation.collection.objects.link(obj)
            if obj.hide_viewport:
                if not obj.is_editable or obj.is_property_readonly("hide_viewport"):
                    raise ValueError(f"Constraint or modifier target {obj.name} is hidden and linked read-only. Make the target visible in its library or use a local visible target before exporting")
                visibility.append((obj, True))
                obj.hide_viewport = False
        context.window.scene = evaluation
        evaluation.frame_set(previous.frame_current, subframe=previous.frame_subframe)
        context.view_layer.update()
        depsgraph = context.evaluated_depsgraph_get()
        for obj in objects:
            evaluated = obj.evaluated_get(depsgraph)
            pose = evaluated_parent_local_pose(obj, evaluated)
            poses[obj] = require_trs_pose(obj, pose if pose is not None else Matrix(api().local_matrix(evaluated)))
            if obj in copies:
                for modifier, copied in zip(evaluated.modifiers, copies[obj].modifiers, strict=True):
                    freeze_modifier_values(modifier, copied)
        # Keep these original targets alive through export_apply: modifiers can
        # read their evaluated geometry/transforms after this pose sample.
        return poses, evaluation, visibility
    except Exception:
        release_evaluation(context, previous, evaluation, visibility)
        raise


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
        evaluation_scene, evaluation_visibility = None, []
        staged_objects, staged_meshes = set(), []
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
            prepared = {}
            for obj in owned:
                if obj.modifiers and not api().state(obj).get("zs_reference_helper"):
                    copied = obj.copy()
                    staged_objects.add(copied)
                    prepared[obj] = copied
            poses, evaluation_scene, evaluation_visibility = evaluated_local_poses(context, owned, prepared)
            signatures = {}
            for item in data["models"]:
                collection = api().asset_collection(data, item["path"])
                staging_scene = evaluation_scene if evaluation_scene is not None else empty_scene_copy(previous)
                context.window.scene = staging_scene
                staging_scene.frame_set(previous.frame_current, subframe=previous.frame_subframe)
                for obj in staging_scene.objects:
                    obj.select_set(False)
                geometry_changed = bool(api().state(collection).get("zs_force_geometry"))
                copies, mesh_copies = {}, {}
                for obj in api().source_objects(collection):
                    if api().state(obj).get("zs_reference_helper"):
                        continue
                    copied = prepared.get(obj)
                    if copied is None:
                        copied = obj.copy()
                        staged_objects.add(copied)
                    copies[obj] = copied
                    copied.instance_type = "NONE"
                    copied.hide_viewport = copied.hide_render = copied.hide_select = False
                    staging_scene.collection.objects.link(copied)
                    copied.select_set(True)
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
                            raise ValueError("Blender cannot apply modifiers to a mesh with shape keys: remove its modifiers or its shape keys before exporting")
                for original, copied in copies.items():
                    if original.parent and original.parent not in copies:
                        raise ValueError("An object is parented outside its physical source file")
                    copied.parent = copies.get(original.parent)
                    copied.parent_type = 'OBJECT'
                    # Bake transform constraints once; retained originals still
                    # provide evaluated dependencies for geometry modifiers.
                    copied.constraints.clear()
                    copied.animation_data_clear()
                    copied.delta_location = (0, 0, 0)
                    copied.delta_rotation_euler = (0, 0, 0)
                    copied.delta_rotation_quaternion = (1, 0, 0, 0)
                    copied.delta_scale = (1, 1, 1)
                    copied.matrix_parent_inverse.identity()
                    location, rotation, scale = pose_trs(original, poses[original])
                    copied.rotation_mode = 'QUATERNION'
                    copied.location = location
                    copied.rotation_quaternion = rotation
                    copied.scale = scale
                    _export_poses[copied] = poses[original]
                context.view_layer.update()
                path = bridge.contained(scratch, item["path"])
                path.parent.mkdir(parents=True, exist_ok=True)
                api()._exporting = collection
                result = bpy.ops.export_scene.gltf(filepath=str(path), export_format="GLTF_SEPARATE", use_selection=True,
                    use_active_scene=True, export_extras=False, export_attributes=True, export_yup=True,
                    export_animations=False, export_current_frame=True, export_skins=False, export_cameras=False, export_lights=False,
                    export_morph_normal=False, export_morph_tangent=False,
                    export_apply=True, export_image_format="AUTO", export_gpu_instances=False)
                api()._exporting = None
                if "FINISHED" not in result:
                    raise ValueError("Blender could not export " + item["path"])
                annotate_polygons(path)
                for obj in copies.values():
                    staged_objects.remove(obj)
                    bpy.data.objects.remove(obj, do_unlink=True)
                _export_poses.clear()
                for mesh in staged_meshes:
                    bpy.data.meshes.remove(mesh)
                staged_meshes.clear()
                payload["models"].append(dict(path=item["path"], geometryChanged=geometry_changed))
                if evaluation_scene is None:
                    context.window.scene = previous
                    bpy.data.scenes.remove(staging_scene)
                staging_scene = None
            completed_evaluation, evaluation_scene = evaluation_scene, None
            completed_visibility, evaluation_visibility = evaluation_visibility, []
            release_evaluation(context, previous, completed_evaluation, completed_visibility)
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
            _export_poses.clear()
            # The helper copied the export into its generation folder; the staging files are not needed again.
            if scratch is not None:
                shutil.rmtree(scratch, ignore_errors=True)
            for obj in staged_objects:
                bpy.data.objects.remove(obj, do_unlink=True)
            for mesh in staged_meshes:
                bpy.data.meshes.remove(mesh)
            try:
                if staging_scene is not None and staging_scene != evaluation_scene:
                    context.window.scene = previous
                    bpy.data.scenes.remove(staging_scene)
            finally:
                release_evaluation(context, previous, evaluation_scene, evaluation_visibility)
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
