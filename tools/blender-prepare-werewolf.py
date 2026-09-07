r"""Prepares the purchased Werewolf FBXs for the game: renames the six bones the game looks up by
name, strips the rig junk, measures the mesh around every bone (for the colliders the Unity
build generates), and re-exports everything into art\export.

    blender --background --factory-startup --python mods\Werewolf\tools\blender-prepare-werewolf.py -- [--source <unitypackage Werewolf Character dir>] [--out <mod>\art\export]

Why Blender and not Unity for the rename: a Generic animator binds clip curves to bones by
transform PATH, so the model and every animation clip have to carry the same bone names. Renaming
in Unity would mean rewriting the curve paths of every clip after import and rebuilding the avatar
in the right order; renaming the source files once is deterministic and can be inspected with
blender-inspect-fbx.py.

Why the object transforms are NOT applied: Blender's FBX importer puts the file's centimetre unit
and Y-up axis on the armature OBJECT (scale 0.01, rot 90 deg X) and leaves the armature DATA and
the pose-bone location keyframes in file units. Applying the object transform would rescale the
rest bones but not the location F-curves, so a jump that lifts the pelvis 70 cm would lift it 70 m.
Leaving the object transform alone and exporting with the default axis/unit conversion undoes the
import conversion exactly, and the model and the clips go through the same path, so they agree.

Everything is measured in metres in Blender world space (Z up) unless said otherwise.
"""
import bpy
import json
import math
import os
import sys
from mathutils import Vector

RENAMES = {
    "pelvis": "Hips",
    "head": "Head",
    "thigh_l": "LeftUpLeg",
    "thigh_r": "RightUpLeg",
    "upperarm_l": "LeftArm",
    "upperarm_r": "RightArm",
}

ANIM_FILES = ["Werewolf_Anim.fbx", "Werewolf_Eating.fbx", "Werewolf_Eating_Out.fbx",
              "Werewolf_Run_4_Legs.fbx", "Werewolf_Walk_4_Legs.fbx"]

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []


def arg(name, default):
    return argv[argv.index(name) + 1] if name in argv else default


mod_dir = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
source = arg("--source", os.path.join(mod_dir, "art", "source", "unitypackage", "Assets", "Lil Pupinduy",
                                      "Character", "Werewolf Character"))
out_dir = arg("--out", os.path.join(mod_dir, "art", "export"))
anim_out = os.path.join(out_dir, "anim")
os.makedirs(anim_out, exist_ok=True)

report = []


def log(msg):
    print("[werewolf] " + msg)
    report.append(msg)


def fresh_import(path, anim):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=path, automatic_bone_orientation=False, ignore_leaf_bones=False,
                             use_anim=anim)
    arms = [o for o in bpy.data.objects if o.type == 'ARMATURE']
    if len(arms) != 1:
        raise RuntimeError(f"{path}: expected one armature, found {len(arms)}")
    return arms[0]


def all_fcurves(action):
    # Blender 4.4+ slotted actions; the legacy flat list is kept as a fallback.
    if hasattr(action, "layers"):
        for layer in action.layers:
            for strip in layer.strips:
                for bag in strip.channelbags:
                    yield from bag.fcurves
    elif hasattr(action, "fcurves"):
        yield from action.fcurves


def rename_bones(arm):
    missing = [old for old in RENAMES if old not in arm.data.bones]
    if missing:
        raise RuntimeError("rig has no bone(s) " + ", ".join(missing) + " - wrong file, or already renamed")
    for old, new in RENAMES.items():
        arm.data.bones[old].name = new  # also renames vertex groups and the active action's paths
    # Actions that are not assigned to the armature keep the old paths; fix every action by hand.
    fixed = 0
    for action in bpy.data.actions:
        for fc in all_fcurves(action):
            for old, new in RENAMES.items():
                token = f'pose.bones["{old}"]'
                if token in fc.data_path:
                    fc.data_path = fc.data_path.replace(token, f'pose.bones["{new}"]')
                    fixed += 1
    for old, new in RENAMES.items():
        if old in arm.data.bones or new not in arm.data.bones:
            raise RuntimeError(f"rename {old} -> {new} did not take")
    return fixed


def world_bone(arm, bone):
    return arm.matrix_world @ bone.head_local, arm.matrix_world @ bone.tail_local


def measure_bones(arm, mesh_obj):
    """Per bone: how far the skinned vertices sit from the bone axis, and how far they extend along
    it. The Unity build turns this into capsule colliders. Uses each vertex's dominant group."""
    me = mesh_obj.data
    groups = {g.index: g.name for g in mesh_obj.vertex_groups}
    samples = {}
    for v in me.vertices:
        if not v.groups:
            continue
        g = max(v.groups, key=lambda x: x.weight)
        name = groups.get(g.group)
        if name is None or name not in arm.data.bones:
            continue
        samples.setdefault(name, []).append(mesh_obj.matrix_world @ v.co)

    result = {}
    for bone in arm.data.bones:
        head, tail = world_bone(arm, bone)
        axis = tail - head
        length = axis.length
        entry = {
            "parent": bone.parent.name if bone.parent else None,
            "children": [c.name for c in bone.children],
            "head_blender_m": [round(v, 4) for v in head],
            "tail_blender_m": [round(v, 4) for v in tail],
            "length_m": round(length, 4),
            "vertices": 0,
        }
        pts = samples.get(bone.name, [])
        if pts and length > 1e-6:
            d = axis / length
            radial, along = [], []
            for p in pts:
                rel = p - head
                t = rel.dot(d)
                along.append(t)
                radial.append((rel - d * t).length)
            radial.sort()
            along.sort()
            n = len(radial)
            entry.update({
                "vertices": n,
                "radius_p50_m": round(radial[n // 2], 4),
                "radius_p90_m": round(radial[min(n - 1, int(n * 0.9))], 4),
                "radius_max_m": round(radial[-1], 4),
                # extent along the bone axis, relative to the head, as a fraction of bone length
                "along_min": round(along[0] / length, 3),
                "along_max": round(along[-1] / length, 3),
            })
        result[bone.name] = entry
    return result


def export_common():
    return dict(
        axis_forward='-Z', axis_up='Y', global_scale=1.0, apply_unit_scale=True,
        apply_scale_options='FBX_SCALE_NONE', use_space_transform=True, bake_space_transform=False,
        add_leaf_bones=False, primary_bone_axis='Y', secondary_bone_axis='X',
        use_armature_deform_only=False, armature_nodetype='NULL', path_mode='AUTO', embed_textures=False,
        use_custom_props=False, use_selection=False, use_active_collection=False,
    )


# ------------------------------------------------------------------ the model

model_src = os.path.join(source, "Models", "Werewolf.fbx")
log(f"model: {model_src}")
arm = fresh_import(model_src, anim=False)

# The seller's LOD placeholders: empties named LOD_0..3 under LOD_Group_1. Unity gets a real
# LODGroup from ModelSetup instead, built on the four Werewolf0..3 meshes.
junk = [o for o in bpy.data.objects if o.type not in ('ARMATURE', 'MESH')]
for o in junk:
    bpy.data.objects.remove(o, do_unlink=True)
log(f"removed {len(junk)} non-mesh objects (LOD placeholders)")

meshes = sorted([o for o in bpy.data.objects if o.type == 'MESH'], key=lambda o: o.name)
for m in meshes:
    m.data.calc_loop_triangles()
    log(f"mesh {m.name}: {len(m.data.loop_triangles)} tris, {len(m.vertex_groups)} groups, armature={m.find_armature().name if m.find_armature() else None}")

fixed = rename_bones(arm)
log(f"renamed bones: {', '.join(f'{o}->{n}' for o, n in RENAMES.items())} ({fixed} curve paths touched)")
for m in meshes:
    for new in RENAMES.values():
        if new not in m.vertex_groups and m.name != "Werewolf3":
            log(f"WARNING: {m.name} has no vertex group {new} after the rename")

lod0 = next(m for m in meshes if m.name == "Werewolf0")
bones = measure_bones(arm, lod0)

# The numbers the game and the colliders care about, for the report.
ws = [lod0.matrix_world @ v.co for v in lod0.data.vertices]
zmin, zmax = min(v.z for v in ws), max(v.z for v in ws)
ymin, ymax = min(v.y for v in ws), max(v.y for v in ws)
xmin, xmax = min(v.x for v in ws), max(v.x for v in ws)
log(f"LOD0 world bbox: height {zmax - zmin:.3f} m (z {zmin:.3f}..{zmax:.3f}), length {ymax - ymin:.3f} m, width {xmax - xmin:.3f} m")
for name in ["root", "Hips", "spine_03", "neck_01", "Head", "LeftArm", "lowerarm_l", "hand_l", "LeftUpLeg", "calf_l", "foot_l"]:
    b = bones[name]
    log(f"  {name:12s} head z={b['head_blender_m'][2]:.3f} len={b['length_m']:.3f} verts={b['vertices']:4d} r50={b.get('radius_p50_m', 0):.3f} r90={b.get('radius_p90_m', 0):.3f}")

with open(os.path.join(out_dir, "werewolf_bones.txt"), "w", encoding="utf-8") as f:
    f.write("# name parent length_m r50_m r90_m along_min along_max vertices\n")
    for name, b in bones.items():
        f.write(f"{name} {b['parent'] or '-'} {b['length_m']} {b.get('radius_p50_m', 0)} {b.get('radius_p90_m', 0)} "
                f"{b.get('along_min', 0)} {b.get('along_max', 0)} {b['vertices']}\n")
with open(os.path.join(out_dir, "werewolf_bones.json"), "w", encoding="utf-8") as f:
    json.dump({
        "source": model_src,
        "renames": RENAMES,
        "lod0_bbox_blender_m": {"min": [round(xmin, 4), round(ymin, 4), round(zmin, 4)],
                                "max": [round(xmax, 4), round(ymax, 4), round(zmax, 4)]},
        "lods": {m.name: len(m.data.loop_triangles) for m in meshes},
        "bones": bones,
    }, f, indent=1)
log("wrote werewolf_bones.json")

model_out = os.path.join(out_dir, "Werewolf.fbx")
bpy.ops.export_scene.fbx(filepath=model_out, object_types={'ARMATURE', 'MESH'}, bake_anim=False,
                         use_mesh_modifiers=True, mesh_smooth_type='OFF', use_tspace=False,
                         **export_common())
log(f"wrote {model_out} ({os.path.getsize(model_out) // 1024} KB)")

# ------------------------------------------------------------------ the animations

for fname in ANIM_FILES:
    src = os.path.join(source, "AnimationClips", fname)
    arm = fresh_import(src, anim=True)

    # The Advanced Skeleton control rig came along as empties (Group / MotionSystem / RootSystem /
    # FK*) with their own copies of every take. Only the deform armature's takes are wanted.
    junk = [o for o in bpy.data.objects if o.type != 'ARMATURE']
    for o in junk:
        bpy.data.objects.remove(o, do_unlink=True)
    for a in list(bpy.data.actions):
        if not a.name.startswith(arm.name + "|"):
            bpy.data.actions.remove(a)

    fixed = rename_bones(arm)

    takes = []
    for a in list(bpy.data.actions):
        # "Armature|Idle|BaseLayer" -> "Idle". The exporter prefixes the object name again, so the
        # take lands in Unity as "Armature|Idle"; ModelSetup strips that.
        parts = a.name.split("|")
        a.name = parts[1] if len(parts) >= 2 else a.name
        a.use_fake_user = True
        f0, f1 = a.frame_range
        takes.append((a.name, int(f0), int(f1)))
    if not takes:
        raise RuntimeError(f"{fname}: no takes left after filtering")
    arm.animation_data.action = bpy.data.actions[takes[0][0]]

    # Unity collapses a file whose ONLY root node is the armature: the node vanishes and the clip
    # paths come out as "root/Hips/..." while the model - whose Armature node has mesh children
    # and survives - is "Armature/root/Hips/...". A Generic animator binds by path, so nothing
    # would move. A second, empty root node keeps Unity from collapsing the first; it imports as
    # an inert GameObject next to the skeleton and the paths agree.
    keeper = bpy.data.objects.new("AnimRoot", None)
    bpy.context.scene.collection.objects.link(keeper)

    dst = os.path.join(anim_out, fname)
    bpy.ops.export_scene.fbx(filepath=dst, object_types={'ARMATURE', 'EMPTY'}, bake_anim=True,
                             bake_anim_use_all_bones=True, bake_anim_use_nla_strips=False,
                             bake_anim_use_all_actions=True, bake_anim_force_startend_keying=True,
                             bake_anim_step=1.0, bake_anim_simplify_factor=0.0,
                             **export_common())
    fps = bpy.context.scene.render.fps
    log(f"wrote anim/{fname} ({os.path.getsize(dst) // 1024} KB): " + ", ".join(
        f"{n} {f0}-{f1} ({(f1 - f0) / fps:.2f}s)" for n, f0, f1 in takes) + f"; {fixed} curve paths renamed")

with open(os.path.join(out_dir, "prepare-report.txt"), "w", encoding="utf-8") as f:
    f.write("\n".join(report) + "\n")
log("done")
