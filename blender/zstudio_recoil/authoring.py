# SPDX-License-Identifier: MIT
"""Explicit authoring of references in the current source graph."""
import json
import math
import sys
from mathutils import Matrix, Quaternion, Vector
import bpy
from bpy.props import StringProperty, IntProperty, EnumProperty


def api():
    return sys.modules[__package__]


def source_pose(text):
    value = json.loads(text)
    if not isinstance(value, dict) or set(value) - {'matrix', 'translation', 'rotation', 'scale'}:
        raise ValueError('Use a glTF matrix or translation/rotation/scale object')
    if 'matrix' in value and len(value) != 1:
        raise ValueError('A matrix cannot be combined with TRS')
    for key, numbers in value.items():
        count = {'matrix': 16, 'translation': 3, 'rotation': 4, 'scale': 3}[key]
        if not isinstance(numbers, list) or len(numbers) != count or any(type(n) not in (int, float) or not math.isfinite(n) for n in numbers):
            raise ValueError('Invalid source transform')
    if 'matrix' in value:
        numbers = value['matrix']
        pose = Matrix([numbers[i:i + 4] for i in range(0, 16, 4)]).transposed()
    else:
        q = value.get('rotation', [0, 0, 0, 1])
        pose = Matrix.LocRotScale(Vector(value.get('translation', [0, 0, 0])), Quaternion((q[3], *q[:3])), Vector(value.get('scale', [1, 1, 1])))
    axes = Matrix(((1, 0, 0, 0), (0, 0, -1, 0), (0, 1, 0, 0), (0, 0, 0, 1)))
    return value, axes @ pose @ axes.transposed()


class RECOIL_OT_add_model_reference(bpy.types.Operator):
    bl_idname = 'recoil.add_model_reference'
    bl_label = 'Add Model Reference'
    bl_options = {'REGISTER', 'UNDO'}
    model: StringProperty(name='Model source path')
    placement_name: StringProperty(name='Placement name', default='placement')
    reference_name: StringProperty(name='Reference child name', default='model')
    sibling_index: IntProperty(name='Sibling position', default=0, min=0)
    parent_mode: EnumProperty(name='Parent', items=[('ACTIVE', 'Active object', 'Parent beneath the active source object'), ('ROOT', 'Source root', 'Create a root placement')], default='ACTIVE')
    transform: StringProperty(name='Source transform (glTF JSON)', default='{}')
    properties: StringProperty(name='Placement properties (RECOIL JSON)', default='{"zone":"inherit"}')
    reference_properties: StringProperty(name='Reference properties (RECOIL JSON)', default='{"zone":"inherit"}')

    @classmethod
    def poll(cls, context):
        return context.mode == 'OBJECT' and 'zs_session' in api().state(context.scene) and 'zs_placement' not in api().state(context.scene)

    def invoke(self, context, event):
        return context.window_manager.invoke_props_dialog(self, width=650)

    def execute(self, context):
        created = []
        try:
            if not self.poll(context):
                raise ValueError('Use Object Mode in a source graph outside a zone workspace')
            if any(len(v) > 65536 for v in (self.transform, self.properties, self.reference_properties)):
                raise ValueError('Placement properties exceed 64 KiB')
            _, data = api().session(context)
            collection = api().asset_collection(data, api().state(context.scene)['zs_active_file'])
            target = api().asset_collection(data, self.model)
            parent = context.object if self.parent_mode == 'ACTIVE' else None
            if parent and (parent not in api().source_objects(collection) or api().state(parent).get('zs_reference_helper')):
                raise ValueError('Select a parent in the active source file, or choose Source root')
            values, pose = source_pose(self.transform)
            properties = json.loads(self.properties)
            reference = json.loads(self.reference_properties)
            if not isinstance(properties, dict) or not isinstance(reference, dict) or 'ref' in properties:
                raise ValueError('Placement and reference properties must be RECOIL objects; only the child may reference a model')
            siblings = [o for o in api().source_objects(collection) if o.parent == parent and not api().state(o).get('zs_reference_helper')]
            source = json.loads(api().state(collection)['zs_metadata'])['source']
            parent_origin = api().state(parent).get('zs_origin', -1) if parent else -1
            indices = source['nodes'][parent_origin].get('children', []) if parent_origin >= 0 else source['scenes'][source.get('scene', 0)].get('nodes', [])
            rank = {index: i for i, index in enumerate(indices)}
            pending = sorted((o for o in siblings if 'zs_insert_sequence' in api().state(o)), key=lambda o: api().state(o)['zs_insert_sequence'])
            siblings = sorted((o for o in siblings if o not in pending), key=lambda o: rank.get(api().state(o).get('zs_origin'), len(rank)))
            for obj in pending:
                if 'zs_insert_target' in api().state(obj):
                    target_object = api().state(obj).get('zs_insert_target')
                    if target_object is None or target_object not in siblings:
                        raise ValueError('The insertion sibling was removed or reparented. Undo that edit or recreate the placement')
                    position = siblings.index(target_object)
                else:
                    anchor = api().state(obj).get('zs_insert_before', '')
                    position = next((i for i, sibling in enumerate(siblings) if api().state(sibling).get('zs_uid') == anchor), len(siblings))
                siblings.insert(position, obj)
            if self.sibling_index > len(siblings):
                raise ValueError('Sibling position is outside the parent')
            if parent and self.sibling_index < api().source_values(parent).get('inlineSource', {}).get('count', 0):
                raise ValueError('Insert after the inline source section; its existing members must remain together')
            anchor = siblings[self.sibling_index] if self.sibling_index < len(siblings) else None
            sequence = 1 + max((api().state(o).get('zs_insert_sequence', 0) for o in siblings), default=0)
            holder = bpy.data.objects.new(self.placement_name, None); created.append(holder)
            from .exporter import pose_trs
            pose_trs(holder, pose)
            child = bpy.data.objects.new(self.reference_name, None); created.append(child)
            for obj, metadata, name in ((holder, properties, self.placement_name), (child, reference, self.reference_name)):
                collection.objects.link(obj)
                api().populate(obj, metadata)
                obj.recoil.engine_name = name
            holder.parent = parent
            holder.matrix_basis = pose
            actual = Matrix(api().local_matrix(holder))
            if any(abs(actual[r][c] - pose[r][c]) > 1e-6 * max(1, abs(pose[r][c])) for r in range(4) for c in range(4)):
                raise ValueError('Blender cannot represent this source transform without changing it')
            child.parent = holder
            child.instance_type = 'COLLECTION'; child.instance_collection = target
            api().bind_objects(collection)
            api().state(holder)['zs_engine_name'] = self.placement_name
            api().state(child)['zs_engine_name'] = self.reference_name
            api().state(holder)['zs_source_transform'] = json.dumps(values)
            api().state(holder)['zs_source_pose'] = json.dumps(api().local_matrix(holder))
            api().state(holder)['zs_source_parent'] = parent if parent else ''
            api().state(holder)['zs_insert_position'] = self.sibling_index
            # Binding gives new artist objects identities and splits copied ones.
            # Resolve the chosen object after that step, never from a stale UID.
            api().state(holder)['zs_insert_before'] = api().state(anchor)['zs_uid'] if anchor else ''
            if anchor:
                api().state(holder)['zs_insert_target'] = anchor
            api().state(holder)['zs_insert_sequence'] = sequence
            context.view_layer.objects.active = holder
            holder.select_set(True)
            return {'FINISHED'}
        except Exception as error:
            for obj in reversed(created): bpy.data.objects.remove(obj, do_unlink=True)
            self.report({'ERROR'}, str(error))
            return {'CANCELLED'}
