r"""Bakes travel into the locomotion clips, so the creature can run on root motion like every other
entity in the game.

    blender --background --factory-startup --python mods\Werewolf\tools\blender-bake-root-motion.py -- [--clips Walk_4_Legs,Run_4_Legs] [--in <dir>] [--out <dir>]

WHY. Every entity 7 Days to Die ships sets RootMotion="true" - all forty-odd of them, animals and
zombies alike. Their displacement comes from the animation: Animator.deltaPosition ->
AvatarController.NotifyAnimatorMove -> EntityAlive.accumulatedRootMotion, blended with the
move helper's steering. The purchased werewolf clips are animated in place, which forced
RootMotion="false" on this one entity - a code path nothing in the shipped game exercises - and
that is where its stuttering chase came from. Baking the travel in lets the werewolf go back on
the same road as the dire wolf.

HOW MUCH. Not a guess: `blender-measure-stride.py` reads it off the planted feet. In an in-place
clip the body is fixed, so whichever foot is on the ground slides backwards, and that backward
slide is exactly the forward travel the root should have had. Measured on this asset the
four-legged run depicts 6.10 m/s and the walk 1.75 m/s - which is very close to the 7 and 1.8 the
creature is tuned for, because the animator drew a creature of this size moving at a believable
speed.

WHAT IT DOES. Per frame it finds the planted foot the same way, then translates the `root` bone
forward by that amount, cumulatively. The planted foot then stays put in world space - no skating -
and the body advances. Only the root bone is touched; every other bone keeps its keys, so the gait
itself is untouched.

The result must be imported with `keepOriginalPositionXZ = false` so Unity extracts the travel as
root motion instead of leaving it in the pose. ModelSetup does that for the clips named here.
"""
import bpy
import os
import sys
from mathutils import Vector

FEET = ["foot_l", "foot_r", "hand_l", "hand_r"]
ROOT = "root"

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []


def arg(name, default):
    return argv[argv.index(name) + 1] if name in argv else default


MOD = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
in_dir = arg("--in", os.path.join(MOD, "art", "export", "anim"))
out_dir = arg("--out", in_dir)
# Locomotion only by default. The attack clips also lunge about a metre, which vanilla would
# extract too, but a bad read there would make the creature slide during a swing - so they stay
# opt-in until the locomotion is proven.
wanted = set(arg("--clips", "Walk_4_Legs,Run_4_Legs,Walk,Run").split(","))

export_settings = dict(
    axis_forward='-Z', axis_up='Y', global_scale=1.0, apply_unit_scale=True,
    apply_scale_options='FBX_SCALE_NONE', use_space_transform=True, bake_space_transform=False,
    add_leaf_bones=False, primary_bone_axis='Y', secondary_bone_axis='X',
    use_armature_deform_only=False, armature_nodetype='NULL', path_mode='AUTO',
    embed_textures=False, use_custom_props=False, use_selection=False, use_active_collection=False,
)


def bind_slot(arm, action):
    arm.animation_data.action = action
    if hasattr(action, "slots") and len(action.slots) and hasattr(arm.animation_data, "action_slot"):
        for s in action.slots:
            if s.target_id_type == 'OBJECT':
                arm.animation_data.action_slot = s
                return


for name in sorted(os.listdir(in_dir)):
    if not name.lower().endswith(".fbx"):
        continue
    path = os.path.join(in_dir, name)

    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=path, automatic_bone_orientation=False,
                             ignore_leaf_bones=False, use_anim=True)
    arm = next(o for o in bpy.data.objects if o.type == 'ARMATURE')
    if ROOT not in arm.pose.bones:
        print(f"[bake] {name}: no '{ROOT}' bone, skipped")
        continue
    feet = [f for f in FEET if f in arm.pose.bones]
    fps = bpy.context.scene.render.fps

    # World delta -> the root bone's own location space, so the keys we write mean what we think.
    root_bone = arm.pose.bones[ROOT]
    to_bone = (arm.matrix_world @ arm.data.bones[ROOT].matrix_local).inverted()

    baked_any = False
    for action in list(bpy.data.actions):
        clip = action.name.split("|")[-1]
        if clip not in wanted:
            continue
        bind_slot(arm, action)

        f0, f1 = int(action.frame_range[0]), int(action.frame_range[1])
        if f1 <= f0:
            continue

        def foot_world(bone, frame):
            bpy.context.scene.frame_set(frame)
            bpy.context.view_layer.update()
            return arm.matrix_world @ arm.pose.bones[bone].head

        # Pass one: measure the per-frame travel from the planted foot.
        previous = {f: foot_world(f, f0) for f in feet}
        per_frame = {f0: 0.0}
        total = 0.0
        for frame in range(f0 + 1, f1 + 1):
            current = {f: foot_world(f, frame) for f in feet}
            best, slide = None, 0.0
            for f in feet:
                d = (current[f] - previous[f]).y      # +y is backwards for this rig
                if d <= 0:
                    continue
                if best is None or current[f].z < current[best].z:
                    best, slide = f, d
            if best is not None:
                total += slide
            per_frame[frame] = total
            previous = current

        # Pass two: write it onto the root bone. Forward is -Y in Blender for this rig, because
        # the export maps Blender -Y onto Unity +Z.
        rest = root_bone.location.copy()
        for frame in range(f0, f1 + 1):
            bpy.context.scene.frame_set(frame)
            world_offset = Vector((0.0, -per_frame[frame], 0.0))
            root_bone.location = rest + (to_bone.to_3x3() @ world_offset)
            root_bone.keyframe_insert(data_path="location", frame=frame)

        seconds = (f1 - f0) / float(fps)
        print(f"[bake] {name}: {clip} {seconds:.2f}s  travel {total:.2f} m  "
              f"-> {total / seconds:.2f} m/s baked onto '{ROOT}'")
        baked_any = True

    if not baked_any:
        print(f"[bake] {name}: nothing to bake")
        continue

    # Keep every take in the file, baked or not, and leave the first one active as the exporter
    # expects.
    takes = [a for a in bpy.data.actions]
    for a in takes:
        a.use_fake_user = True
    bind_slot(arm, takes[0])

    # The empty second root node that stops Unity collapsing the armature node - see
    # blender-prepare-werewolf.py. Re-added because this file was re-imported.
    if "AnimRoot" not in bpy.data.objects:
        keeper = bpy.data.objects.new("AnimRoot", None)
        bpy.context.scene.collection.objects.link(keeper)

    dst = os.path.join(out_dir, name)
    bpy.ops.export_scene.fbx(filepath=dst, object_types={'ARMATURE', 'EMPTY'}, bake_anim=True,
                             bake_anim_use_all_bones=True, bake_anim_use_nla_strips=False,
                             bake_anim_use_all_actions=True, bake_anim_force_startend_keying=True,
                             bake_anim_step=1.0, bake_anim_simplify_factor=0.0,
                             **export_settings)
    print(f"[bake] wrote {dst} ({os.path.getsize(dst) // 1024} KB)")

print("[bake] done")
