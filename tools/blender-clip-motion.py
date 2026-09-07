r"""For every animation take in an FBX, reports how far the root bone travels and how fast, plus the
pelvis height range - to decide whether the clips carry root motion and to set blend thresholds.

    blender --background --factory-startup --python mods\Werewolf\tools\blender-clip-motion.py -- <anim.fbx> [<anim.fbx> ...]
"""
import bpy, sys
from mathutils import Vector

argv = sys.argv[sys.argv.index("--") + 1:]
for fbx in argv:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=fbx, automatic_bone_orientation=False, ignore_leaf_bones=True, use_anim=True)
    arm = next(o for o in bpy.data.objects if o.type == 'ARMATURE')
    fps = bpy.context.scene.render.fps
    print("=== FILE", fbx.split("/")[-1])
    for a in bpy.data.actions:
        if not a.name.startswith("Armature|"):
            continue
        arm.animation_data.action = a
        # Blender 4.4+ slotted actions: bind the armature's slot
        if hasattr(a, "slots") and len(a.slots) and hasattr(arm.animation_data, "action_slot"):
            for s in a.slots:
                if s.target_id_type == 'OBJECT':
                    arm.animation_data.action_slot = s
                    break
        f0, f1 = int(a.frame_range[0]), int(a.frame_range[1])
        def world(bone, frame):
            bpy.context.scene.frame_set(frame)
            bpy.context.view_layer.update()
            return arm.matrix_world @ arm.pose.bones[bone].head
        r0, r1 = world("root", f0), world("root", f1)
        pelvis = "pelvis" if "pelvis" in arm.pose.bones else "Hips"
        p0, p1 = world(pelvis, f0), world(pelvis, f1)
        zs = []
        for f in range(f0, f1 + 1, max(1, (f1 - f0) // 12)):
            zs.append(world(pelvis, f).z)
        d = r1 - r0
        secs = (f1 - f0) / fps
        print(f"  {a.name.split('|')[1]:12s} {secs:5.2f}s  root travel dx={d.x:6.3f} dy={d.y:6.3f} dz={d.z:6.3f}  |xy|={Vector((d.x,d.y)).length:5.2f} m  -> {Vector((d.x,d.y)).length/secs if secs else 0:5.2f} m/s   pelvis z {min(zs):.2f}..{max(zs):.2f}  root0=({r0.x:.2f},{r0.y:.2f},{r0.z:.2f})")

# --- root ROTATION per take, appended: translation alone does not tell whether Unity's built-in
# root motion would steer the model. A Generic rig applies the motion node's rotation delta to the
# GameObject, so a clip whose root yaws will turn the creature every cycle when applyRootMotion is
# left on.
print()
print("=== ROOT ROTATION (yaw about world Z in Blender = the turn Unity would apply)")
for fbx in argv:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=fbx, automatic_bone_orientation=False, ignore_leaf_bones=False, use_anim=True)
    arm = next(o for o in bpy.data.objects if o.type == 'ARMATURE')
    fps = bpy.context.scene.render.fps
    print("=== FILE", fbx.split("/")[-1])
    for a in bpy.data.actions:
        if not a.name.startswith("Armature|") and "|" in a.name:
            continue
        arm.animation_data.action = a
        if hasattr(a, "slots") and len(a.slots) and hasattr(arm.animation_data, "action_slot"):
            for s in a.slots:
                if s.target_id_type == 'OBJECT':
                    arm.animation_data.action_slot = s
                    break
        f0, f1 = int(a.frame_range[0]), int(a.frame_range[1])
        yaws = []
        for f in range(f0, f1 + 1, max(1, (f1 - f0) // 12)):
            bpy.context.scene.frame_set(f)
            bpy.context.view_layer.update()
            q = arm.pose.bones["root"].matrix_basis.to_quaternion()
            yaws.append(q.to_euler().z * 57.2958)
        name = a.name.split("|")[-1]
        print(f"  {name:14s} root yaw {min(yaws):8.2f}..{max(yaws):8.2f} deg   drift over clip {yaws[-1]-yaws[0]:8.2f}")
