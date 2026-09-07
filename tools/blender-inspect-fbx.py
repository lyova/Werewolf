"""Prints what is inside an FBX: objects, meshes with world bbox, armature bones with world
positions, and every animation action with its frame range.

    blender --background --factory-startup --python mods/Werewolf/tools/blender-inspect-fbx.py -- <file.fbx> [--depth N]
"""
import bpy, sys
from mathutils import Vector

argv = sys.argv[sys.argv.index("--") + 1:]
fbx = argv[0]
depth_limit = int(argv[argv.index("--depth") + 1]) if "--depth" in argv else 99

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=fbx, automatic_bone_orientation=False, ignore_leaf_bones=("--keep-leaf" not in argv), use_anim=True)
print("=== FILE", fbx)
print("scene unit:", bpy.context.scene.unit_settings.system, "scale_length", bpy.context.scene.unit_settings.scale_length)
for o in bpy.data.objects:
    print(f"OBJ {o.type:10s} {o.name:30s} loc={tuple(round(v,3) for v in o.location)} "
          f"rot_deg={tuple(round(v*57.2958,1) for v in o.rotation_euler)} scale={tuple(round(v,4) for v in o.scale)} "
          f"parent={o.parent.name if o.parent else None}")
    if o.type == 'MESH':
        me = o.data
        me.calc_loop_triangles()
        ws = [o.matrix_world @ v.co for v in me.vertices]
        mn = Vector((min(v.x for v in ws), min(v.y for v in ws), min(v.z for v in ws)))
        mx = Vector((max(v.x for v in ws), max(v.y for v in ws), max(v.z for v in ws)))
        print(f"   tris={len(me.loop_triangles)} verts={len(me.vertices)} bbox_min={tuple(round(v,3) for v in mn)} "
              f"bbox_max={tuple(round(v,3) for v in mx)} size={tuple(round(v,3) for v in (mx-mn))}")
        print(f"   uv={[u.name for u in me.uv_layers]} materials={[m.name if m else None for m in me.materials]} "
              f"vgroups={len(o.vertex_groups)} armature={o.find_armature().name if o.find_armature() else None} "
              f"modifiers={[m.type for m in o.modifiers]}")
    if o.type == 'ARMATURE':
        arm = o.data
        print(f"   bones={len(arm.bones)}")
        def walk(b, d):
            hw = o.matrix_world @ b.head_local
            tw = o.matrix_world @ b.tail_local
            if d <= depth_limit:
                print(f"   {'  '*d}{b.name:24s} head={tuple(round(v,3) for v in hw)} len={round((tw-hw).length,3)}")
            for c in b.children:
                walk(c, d + 1)
        for b in arm.bones:
            if b.parent is None:
                walk(b, 0)
        if o.animation_data and o.animation_data.action:
            print("   active action:", o.animation_data.action.name)
print("=== ACTIONS")
fps = bpy.context.scene.render.fps
for a in bpy.data.actions:
    fr = a.frame_range
    print(f"ACTION {a.name:48s} frames {fr[0]:6.0f}-{fr[1]:6.0f}  {(fr[1]-fr[0])/fps:6.2f}s @{fps}  slots={len(a.slots)}")
