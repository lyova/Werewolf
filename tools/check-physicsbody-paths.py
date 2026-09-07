r"""Resolves every <collider> path in a mod's physicsbodies.xml against the built prefab, the way
the game does, and reports what would be missing.

    python mods\Werewolf\tools\check-physicsbody-paths.py [--prefab <file>] [--bodies <file>] [--body AWerewolf]

Why this exists: PhysicsBodyInstance.bindCollider does modelRoot.Find(path) from the transform
tagged E_BP_BipedRoot and, when the path does not resolve, logs one line and carries on. The
creature then spawns and moves normally and simply cannot be hit on that body part - a failure
that looks like a damage bug rather than a typo. This catches it before the game runs.

It also checks the thing the game does NOT log: that the transform the path lands on actually
carries a Box/Capsule/Sphere collider. Without one the game wraps it in a PhysicsBodyNullCollider,
which registers no hits either.

Exit code 0 when every path resolves onto a collider, 1 otherwise.
"""
import argparse
import os
import re
import sys
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
MOD = os.path.abspath(os.path.join(HERE, ".."))

COLLIDER_CLASSES = {65: "BoxCollider", 135: "SphereCollider", 136: "CapsuleCollider", 64: "MeshCollider"}
BIPED_ROOT_INDEX = 21  # E_BP_BipedRoot in the game's TagManager -> tag id 20021


def parse_prefab(path):
    """-> (transforms by fileID, gameobjects by fileID, children map, name map, components map)."""
    text = open(path, encoding="utf-8", errors="replace").read()
    docs = {}
    for m in re.finditer(r"^--- !u!(\d+) &(-?\d+)(?: stripped)?\n(.*?)(?=^--- !u!|\Z)", text, re.S | re.M):
        docs[int(m.group(2))] = (int(m.group(1)), m.group(3))

    def field(body, name):
        m = re.search(r"^\s*" + re.escape(name) + r":\s*(.*)$", body, re.M)
        return m.group(1).strip() if m else None

    def fid(s):
        m = re.search(r"fileID:\s*(-?\d+)", s or "")
        return int(m.group(1)) if m else 0

    transforms, gos = {}, {}
    for f, (cls, body) in docs.items():
        if cls == 4:
            transforms[f] = body
        elif cls == 1:
            gos[f] = body

    info = {}
    for f, body in transforms.items():
        go = fid(field(body, "m_GameObject"))
        gb = gos.get(go, "")
        kids_block = re.search(r"m_Children:\n((?:\s+- .*\n)*)", body)
        kids = [int(x) for x in re.findall(r"- \{fileID:\s*(-?\d+)\}", kids_block.group(1))] if kids_block else []
        comps = []
        for cm in re.finditer(r"- component:\s*\{fileID:\s*(-?\d+)\}", gb):
            cid = int(cm.group(1))
            if cid in docs:
                comps.append(docs[cid][0])
        info[f] = {
            "name": field(gb, "m_Name"),
            "tag": field(gb, "m_TagString"),
            "children": kids,
            "components": comps,
        }
    return info


def find_tagged(info, name, index):
    """A prefab saved in this project spells the tag out; one ripped from a bundle has only the
    number, which AssetRipper writes as unknown_<20000 + index>."""
    wanted = {name, "unknown_%d" % (20000 + index), str(20000 + index)}
    for f, node in info.items():
        if node["tag"] in wanted:
            return f
    return None


def resolve(info, start, path):
    """Transform.Find semantics: each segment is a DIRECT child by name."""
    current = start
    for segment in path.split("/"):
        nxt = None
        for kid in info[current]["children"]:
            if kid in info and info[kid]["name"] == segment:
                nxt = kid
                break
        if nxt is None:
            return None
        current = nxt
    return current


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--prefab", default=os.path.join(MOD, "unity", "Assets", "Werewolf", "Prefabs", "Werewolf.prefab"))
    ap.add_argument("--bodies", default=os.path.join(MOD, "pack", "Config", "physicsbodies.xml"))
    ap.add_argument("--body", default="AWerewolf")
    a = ap.parse_args()

    info = parse_prefab(a.prefab)
    root = find_tagged(info, "E_BP_BipedRoot", BIPED_ROOT_INDEX)
    if root is None:
        print("FAIL: no transform tagged E_BP_BipedRoot in " + a.prefab)
        print("      the game would fall back to a transform named Bip001/Bip01 and find nothing")
        return 1
    print("biped root: '%s' in %s" % (info[root]["name"], os.path.basename(a.prefab)))

    tree = ET.parse(a.bodies)
    body = None
    for element in tree.getroot().iter("body"):
        if element.get("name") == a.body:
            body = element
            break
    if body is None:
        print("FAIL: no <body name=\"%s\"> in %s" % (a.body, a.bodies))
        return 1

    bad = 0
    for collider in body.findall("collider"):
        props = {p.get("name"): p.get("value") for p in collider.findall("property")}
        path, tag = props.get("path"), props.get("tag")
        target = resolve(info, root, path)
        if target is None:
            print("  MISSING  %-14s %s" % (tag, path))
            bad += 1
            continue
        shapes = [COLLIDER_CLASSES[c] for c in info[target]["components"] if c in COLLIDER_CLASSES]
        prefab_tag = info[target]["tag"]
        note = ""
        if not shapes:
            note = "  <-- NO COLLIDER, the game would wrap it in a null collider that never registers a hit"
            bad += 1
        print("  ok       %-14s %-72s %s (prefab tag %s)%s"
              % (tag, path, ",".join(shapes) or "-", prefab_tag, note))

    print()
    if bad:
        print("%d of %d colliders would not work" % (bad, len(body.findall("collider"))))
        return 1
    print("all %d collider paths resolve onto a collider" % len(body.findall("collider")))
    return 0


sys.exit(main())
