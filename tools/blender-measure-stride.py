r"""Measures how fast a locomotion clip LOOKS like it is moving, by watching the planted feet.

    blender --background --factory-startup --python mods\Werewolf\tools\blender-measure-stride.py -- <anim.fbx> [<anim.fbx> ...]

Why this is needed. Every entity in 7 Days to Die runs with RootMotion="true": the creature's
displacement comes from the animation, through Animator.deltaPosition ->
AvatarController.NotifyAnimatorMove -> EntityAlive.accumulatedRootMotion. The purchased werewolf
clips are animated in place, which forced RootMotion="false" - a path no vanilla entity uses - and
that is where the stuttering chase comes from. The fix is to bake the travel back into the clips,
and the first question is how much travel: a clip must move the body at the speed its legs depict,
or the feet skate.

The measurement. In an in-place clip the body stays put, so a foot that is planted on the ground
slides backwards under it. That backward slide per frame IS the forward travel the root should
have had. So: for each frame, take the foot that is lowest and moving backwards - the planted one -
and accumulate how far it went. Averaged over the cycle that gives metres per second.

Reported per clip:
  planted speed   what the animation depicts, and therefore what to bake
  duty factor     the fraction of the cycle where a foot was found planted; a quadruped gait
                  should be well above half, and a low number means the read is unreliable
"""
import bpy
import sys
from mathutils import Vector

FEET = ["foot_l", "foot_r", "hand_l", "hand_r"]

argv = sys.argv[sys.argv.index("--") + 1:]


def world_head(arm, bone_name, frame):
    bpy.context.scene.frame_set(frame)
    bpy.context.view_layer.update()
    return arm.matrix_world @ arm.pose.bones[bone_name].head


for fbx in argv:
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=fbx, automatic_bone_orientation=False,
                             ignore_leaf_bones=False, use_anim=True)
    arm = next(o for o in bpy.data.objects if o.type == 'ARMATURE')
    fps = bpy.context.scene.render.fps
    present = [f for f in FEET if f in arm.pose.bones]
    print("=== FILE", fbx.split("/")[-1], "| feet:", ", ".join(present))

    for action in bpy.data.actions:
        if not action.name.startswith(arm.name) and "|" in action.name:
            continue
        arm.animation_data.action = action
        if hasattr(action, "slots") and len(action.slots) and hasattr(arm.animation_data, "action_slot"):
            for s in action.slots:
                if s.target_id_type == 'OBJECT':
                    arm.animation_data.action_slot = s
                    break

        f0, f1 = int(action.frame_range[0]), int(action.frame_range[1])
        if f1 <= f0:
            continue

        # Per frame, per foot: position now and one frame earlier.
        previous = {f: world_head(arm, f, f0) for f in present}
        travel = 0.0
        planted_frames = 0

        for frame in range(f0 + 1, f1 + 1):
            current = {f: world_head(arm, f, frame) for f in present}

            # The planted foot: lowest of those moving backwards. "Backwards" is -Y in Blender for
            # this rig, because the export maps Blender -Y to Unity +Z, the creature's forward.
            best_name, best_slide = None, 0.0
            for f in present:
                delta = current[f] - previous[f]
                slide = delta.y            # positive y = backwards relative to facing
                if slide <= 0:
                    continue
                if best_name is None or current[f].z < current[best_name].z:
                    best_name, best_slide = f, slide

            if best_name is not None:
                travel += best_slide
                planted_frames += 1
            previous = current

        seconds = (f1 - f0) / float(fps)
        duty = planted_frames / float(f1 - f0)
        speed = travel / seconds if seconds > 0 else 0.0
        name = action.name.split("|")[-1]
        print(f"  {name:14s} {seconds:5.2f}s   planted speed {speed:6.2f} m/s   "
              f"travel {travel:5.2f} m   duty {duty * 100:4.0f}%")
