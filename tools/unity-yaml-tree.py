r"""Prints the GameObject hierarchy of a Unity prefab (.prefab YAML) with the components on each
node, tags and layers. No PyYAML needed - the few fields it reads are matched by regex.

    python mods\Werewolf\tools\unity-yaml-tree.py <file.prefab> [--depth N] [--full]

--full also prints collider shapes, LODGroup screen heights, Animator settings and the scripts
(MonoBehaviour guids) so a reference prefab can be copied exactly.
"""
import re, sys

CLASS = {1: "GameObject", 4: "Transform", 23: "MeshRenderer", 33: "MeshFilter", 54: "Rigidbody",
         64: "MeshCollider", 65: "BoxCollider", 82: "AudioSource", 95: "Animator", 114: "MonoBehaviour",
         135: "SphereCollider", 136: "CapsuleCollider", 137: "SkinnedMeshRenderer", 144: "CharacterJoint",
         205: "LODGroup", 198: "ParticleSystem", 199: "ParticleSystemRenderer", 108: "Light",
         224: "RectTransform", 1001: "PrefabInstance", 143: "CharacterController", 153: "ConfigurableJoint",
         50: "Rigidbody2D", 111: "Animation", 137137: "?"}

path = sys.argv[1]
depth_limit = int(sys.argv[sys.argv.index("--depth") + 1]) if "--depth" in sys.argv else 99
full = "--full" in sys.argv

text = open(path, encoding="utf-8", errors="replace").read()
docs = {}
for m in re.finditer(r"^--- !u!(\d+) &(-?\d+)(?: stripped)?\n(.*?)(?=^--- !u!|\Z)", text, re.S | re.M):
    cls, fid, body = int(m.group(1)), int(m.group(2)), m.group(3)
    docs[fid] = (cls, body)

def field(body, name):
    m = re.search(r"^\s*" + re.escape(name) + r":\s*(.*)$", body, re.M)
    return m.group(1).strip() if m else None

def fid_of(s):
    m = re.search(r"fileID:\s*(-?\d+)", s or "")
    return int(m.group(1)) if m else 0

def guid_of(s):
    m = re.search(r"guid:\s*([0-9a-f]+)", s or "")
    return m.group(1) if m else None

gos = {f: b for f, (c, b) in docs.items() if c == 1}
transforms = {f: b for f, (c, b) in docs.items() if c == 4}
go_to_tr = {}
for f, b in transforms.items():
    go_to_tr[fid_of(field(b, "m_GameObject"))] = f

def components(go_body):
    out = []
    for m in re.finditer(r"- component:\s*\{fileID:\s*(-?\d+)\}", go_body):
        cid = int(m.group(1))
        if cid in docs:
            cls, body = docs[cid]
            out.append((cls, body))
    return out

def describe(cls, body):
    name = CLASS.get(cls, str(cls))
    if not full:
        if cls == 114:
            return name + "(" + (guid_of(field(body, "m_Script")) or "?")[:8] + ")"
        return name
    extra = ""
    if cls == 136:
        extra = f" r={field(body,'m_Radius')} h={field(body,'m_Height')} dir={field(body,'m_Direction')} center={field(body,'m_Center')} trigger={field(body,'m_IsTrigger')}"
    elif cls == 65:
        extra = f" size={field(body,'m_Size')} center={field(body,'m_Center')} trigger={field(body,'m_IsTrigger')}"
    elif cls == 135:
        extra = f" r={field(body,'m_Radius')} center={field(body,'m_Center')} trigger={field(body,'m_IsTrigger')}"
    elif cls == 54:
        extra = f" mass={field(body,'m_Mass')} drag={field(body,'m_Drag')} kinematic={field(body,'m_IsKinematic')} gravity={field(body,'m_UseGravity')}"
    elif cls == 95:
        extra = f" controller={field(body,'m_Controller')} avatar={field(body,'m_Avatar')} rootMotion={field(body,'m_ApplyRootMotion')} culling={field(body,'m_CullingMode')} updateMode={field(body,'m_UpdateMode')}"
    elif cls == 137:
        mats = re.findall(r"guid:\s*([0-9a-f]+)", field(body, "m_Materials") or "")
        mats_block = re.search(r"m_Materials:\n((?:\s+- .*\n)+)", body)
        n = len(mats_block.group(1).strip().splitlines()) if mats_block else 0
        extra = f" mesh={field(body,'m_Mesh')} materials={n} quality={field(body,'m_Quality')} updateOffscreen={field(body,'m_UpdateWhenOffscreen')} bones={len(re.findall(r'- \{fileID', re.search(r'm_Bones:\n((?:\s+- .*\n)+)', body).group(1))) if re.search(r'm_Bones:\n((?:\s+- .*\n)+)', body) else 0} root={field(body,'m_RootBone')}"
    elif cls == 205:
        heights = re.findall(r"screenRelativeHeight:\s*([0-9.]+)", body)
        extra = f" lods={heights} fadeMode={field(body,'m_FadeMode')} animateCrossFading={field(body,'m_AnimateCrossFading')}"
    elif cls == 114:
        extra = f" script={field(body,'m_Script')}"
    elif cls == 144:
        extra = f" connected={field(body,'m_ConnectedBody')} axis={field(body,'m_Axis')} swingAxis={field(body,'m_SwingAxis')} lowTwist={field(body,'m_LowTwistLimit')} highTwist={field(body,'m_HighTwistLimit')} swing1={field(body,'m_Swing1Limit')} swing2={field(body,'m_Swing2Limit')}"
    elif cls == 82:
        extra = f" clip={field(body,'m_audioClip')} playOnAwake={field(body,'m_PlayOnAwake')}"
    return name + extra

def walk(tr_fid, depth):
    cls, tb = docs[tr_fid]
    go_fid = fid_of(field(tb, "m_GameObject"))
    gb = gos.get(go_fid, "")
    name = field(gb, "m_Name")
    tag = field(gb, "m_TagString")
    layer = field(gb, "m_Layer")
    active = field(gb, "m_IsActive")
    comps = [describe(c, b) for c, b in components(gb) if c != 4]
    bits = []
    if tag and tag != "Untagged": bits.append("tag=" + tag)
    if layer and layer != "0": bits.append("layer=" + layer)
    if active == "0": bits.append("INACTIVE")
    if full:
        bits.append(f"pos={field(tb,'m_LocalPosition')} rot={field(tb,'m_LocalRotation')} scale={field(tb,'m_LocalScale')}")
    if depth <= depth_limit:
        print("  " * depth + f"{name}" + ("  [" + ", ".join(comps) + "]" if comps else "") + ("  " + " ".join(bits) if bits else ""))
    children = re.findall(r"- \{fileID:\s*(-?\d+)\}", re.search(r"m_Children:\n((?:\s+- .*\n)*)", tb).group(1)) if re.search(r"m_Children:\n((?:\s+- .*\n)*)", tb) else []
    if depth == depth_limit and children:
        print("  " * (depth + 1) + f"... {len(children)} children")
    if depth < depth_limit:
        for c in children:
            c = int(c)
            if c in docs:
                walk(c, depth + 1)

roots = [f for f, b in transforms.items() if fid_of(field(b, "m_Father")) == 0]
print(f"# {path}: {len(gos)} GameObjects, {len(transforms)} Transforms, {len(roots)} root(s)")
for r in roots:
    walk(r, 0)
