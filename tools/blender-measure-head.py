r"""Prints where the Head bone points, per clip, relative to the body's facing.

    blender --background --factory-startup --python mods\Werewolf\tools\blender-measure-head.py -- <anim.fbx> [clip ...]

Why: in game the creature's head was turned to one side while it attacked, as though the target were
off to the right. The game does NOT aim an animal's head - EModelBase.SetLookAt is only ever called
for the trader - so whatever the head does comes out of the clip itself and can be measured here
instead of argued about from screenshots.

Yaw is reported in the ROOT's frame, so it is "how far off straight ahead the muzzle is", not a
world angle: positive is to the creature's left, negative to its right. A swipe that crosses the
body will always turn the head somewhat; what matters is whether one clip sits off-centre for its
whole length, because a clip like that played on every single attack reads as a permanent squint.
"""
import bpy
import sys
import math
from mathutils import Vector

HEAD = "Head"
ROOT = "root"

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
if not argv:
    print("[head] give me an fbx")
    sys.exit(1)

path = argv[0]
wanted = set(argv[1:])

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=path, automatic_bone_orientation=False,
                         ignore_leaf_bones=False, use_anim=True)
arm = next(o for o in bpy.data.objects if o.type == 'ARMATURE')
fps = bpy.context.scene.render.fps

if HEAD not in arm.pose.bones or ROOT not in arm.pose.bones:
    print("[head] rig has no %s or %s bone" % (HEAD, ROOT))
    sys.exit(1)


def bind_slot(action):
    arm.animation_data.action = action
    if hasattr(action, "slots") and len(action.slots) and hasattr(arm.animation_data, "action_slot"):
        for s in action.slots:
            if s.target_id_type == 'OBJECT':
                arm.animation_data.action_slot = s
                return


def head_yaw(frame):
    bpy.context.scene.frame_set(frame)
    bpy.context.view_layer.update()

    head = arm.pose.bones[HEAD]
    root = arm.pose.bones[ROOT]

    # The muzzle direction: a bone points along its own +Y in Blender, so the head's world facing is
    # its matrix's Y column. The body's facing is the root's, the same way. Comparing the two in the
    # ground plane gives the turn that is actually visible.
    head_dir = (arm.matrix_world.to_3x3() @ head.matrix.to_3x3() @ Vector((0, 1, 0)))
    body_dir = (arm.matrix_world.to_3x3() @ root.matrix.to_3x3() @ Vector((0, 1, 0)))
    head_dir.z = 0
    body_dir.z = 0
    if head_dir.length < 1e-6 or body_dir.length < 1e-6:
        return 0.0
    head_dir.normalize()
    body_dir.normalize()

    angle = math.degrees(math.atan2(
        body_dir.x * head_dir.y - body_dir.y * head_dir.x,
        body_dir.x * head_dir.x + body_dir.y * head_dir.y))
    return angle


print("[head] %s" % path.replace("\\", "/").split("/")[-1])
print("[head] %-14s %8s %8s %8s %8s %8s   %s" % ("clip", "t=0.0", "t=0.25", "t=0.5", "t=0.75", "t=1.0", "mean"))
for action in bpy.data.actions:
    clip = action.name.split("|")[-1]
    if wanted and clip not in wanted:
        continue
    f0, f1 = int(action.frame_range[0]), int(action.frame_range[1])
    if f1 <= f0:
        continue
    bind_slot(action)

    samples = [head_yaw(int(f0 + (f1 - f0) * t)) for t in (0.0, 0.25, 0.5, 0.75, 1.0)]
    every = [head_yaw(f) for f in range(f0, f1 + 1)]
    mean = sum(every) / len(every)
    print("[head] %-14s %7.1f%s %7.1f%s %7.1f%s %7.1f%s %7.1f%s   %6.1f deg"
          % ((clip,) + tuple(x for s in samples for x in (s, "d"))[:10] + (mean,)))

print("[head] positive = turned to the creature's LEFT, negative = to its RIGHT")
