"""Keeps sounds.xml in step with art\\sound, and says what changed since the last bundle build.

    python tools\\sync-sounds.py            # sync the xml, report added/removed/changed clips
    python tools\\sync-sounds.py --check    # report only, exit 1 if anything is out of step

THE PROBLEM IT SOLVES. The build renames the clips in each role folder to ww_<role><n>, sorted by
filename, and sounds.xml lists those names one line per clip. Neither side knows about the other:
drop a fifth howl into the folder and it is silently never heard, delete one and the game logs a
warning and plays silence for it, replace one and nothing at all notices - the bundle is only
rebuilt from the Unity project's copy when build.ps1 -Bundle runs. The owner replaced the howl and
attack clips and heard the old ones through three rebuilds before that was found.

WHAT IT DOES.

  1. For every role folder under art\\sound (except `reference`, which is ripped game audio and
     never ships) it rewrites the <AudioClip> lines of the SoundDataNode named werewolf_<role> so
     there is exactly one line per file, in the order the build will number them. Everything else
     in the node - source, noise, priority, pitch - is left alone. A role folder with no files
     leaves its node untouched, so a role can borrow another role's clips by hand (roam used
     sense's until it had its own).

  2. It keeps a manifest of the files it last saw (art\\sound\\.clips.json: size and sha1 per
     file) and prints what was ADDED, REMOVED or CHANGED since. Any of those means the bundle in
     pack\\Resources is stale until build.ps1 -Bundle runs; build.ps1 calls this script and refuses
     a code-only build while that is true, so a stale bundle cannot be deployed by accident.

Works on the mod it lives in: tools\\sync-sounds.py -> the mod root is one level up.
"""
import hashlib
import json
import os
import re
import sys

MOD = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SOUND_DIR = os.path.join(MOD, "art", "sound")
SOUND_XML = os.path.join(MOD, "pack", "Config", "sounds.xml")
MANIFEST = os.path.join(SOUND_DIR, ".clips.json")
PREFIX = "ww_"
SKIP_ROLES = {"reference"}
EXTENSIONS = (".mp3", ".wav", ".ogg")
CLIP_LINE = '<AudioClip ClipName="#@modfolder(Werewolf):Resources/werewolf.unity3d?%s"/>'


def roles_on_disk():
    """role -> [filename, ...] in the order the build numbers them."""
    roles = {}
    if not os.path.isdir(SOUND_DIR):
        return roles
    for role in sorted(os.listdir(SOUND_DIR)):
        path = os.path.join(SOUND_DIR, role)
        if not os.path.isdir(path) or role in SKIP_ROLES:
            continue
        # The build script sorts with PowerShell's Sort-Object Name, which is case-insensitive.
        roles[role] = sorted((f for f in os.listdir(path) if f.lower().endswith(EXTENSIONS)),
                             key=str.lower)
    return roles


def sha1(path):
    h = hashlib.sha1()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 16), b""):
            h.update(chunk)
    return h.hexdigest()


def snapshot(roles):
    """relative path -> {size, sha1}"""
    snap = {}
    for role, files in roles.items():
        for f in files:
            p = os.path.join(SOUND_DIR, role, f)
            snap[role + "/" + f] = {"size": os.path.getsize(p), "sha1": sha1(p)}
    return snap


def diff_manifest(now):
    try:
        with open(MANIFEST, encoding="utf-8") as f:
            before = json.load(f)
    except (OSError, ValueError):
        before = None
    if before is None:
        return None
    added = sorted(k for k in now if k not in before)
    removed = sorted(k for k in before if k not in now)
    changed = sorted(k for k in now if k in before and now[k]["sha1"] != before[k]["sha1"])
    return added, removed, changed


def sync_xml(roles, write):
    """Rewrites the clip lines per node. Returns (changed_roles, problems)."""
    with open(SOUND_XML, encoding="utf-8") as f:
        xml = f.read()
    original = xml
    changed_roles = []
    problems = []

    for role, files in roles.items():
        node_name = "werewolf_" + role
        m = re.search(r'<SoundDataNode name="%s">' % re.escape(node_name), xml)
        if not m:
            problems.append("art\\sound\\%s has %d clip(s) but sounds.xml has no node %s - add the node by hand"
                            % (role, len(files), node_name))
            continue
        if not files:
            # Nothing to number: the node keeps whatever it lists (it may borrow another role's).
            continue

        start = m.end()
        end = xml.index("</SoundDataNode>", start)
        node = xml[start:end]

        lines = list(re.finditer(r'[ \t]*<AudioClip [^\n]*/>\n', node))
        wanted = ["%s%s%d" % (PREFIX, role, i) for i in range(1, len(files) + 1)]
        if lines:
            indent = re.match(r'[ \t]*', lines[0].group(0)).group(0)
            block_start, block_end = lines[0].start(), lines[-1].end()
        else:
            # No clip lines at all: put them right after the AudioSource line.
            src = re.search(r'[ \t]*<AudioSource [^\n]*/>[^\n]*\n', node)
            if not src:
                problems.append("%s has neither AudioClip nor AudioSource lines - fix by hand" % node_name)
                continue
            indent = re.match(r'[ \t]*', src.group(0)).group(0)
            block_start = block_end = src.end()

        have = re.findall(r'\?(\w+)"', node[block_start:block_end])
        if have == wanted:
            continue
        new_block = "".join(indent + CLIP_LINE % w + "\n" for w in wanted)
        node = node[:block_start] + new_block + node[block_end:]
        xml = xml[:start] + node + xml[end:]
        changed_roles.append((role, have, wanted))

    if write and xml != original:
        with open(SOUND_XML, "w", encoding="utf-8", newline="\n") as f:
            f.write(xml)
    return changed_roles, problems


def main():
    check_only = "--check" in sys.argv
    roles = roles_on_disk()
    now = snapshot(roles)

    print("art\\sound: " + ", ".join("%s %d" % (r, len(f)) for r, f in roles.items()))

    changed_roles, problems = sync_xml(roles, write=not check_only)
    for role, have, wanted in changed_roles:
        verb = "would list" if check_only else "now lists"
        print("xml   werewolf_%s %s %s (was %s)" % (role, verb, ", ".join(wanted), ", ".join(have) or "nothing"))
    for p in problems:
        print("FAIL  " + p)

    d = diff_manifest(now)
    stale = False
    if d is None:
        print("clips no manifest yet - every clip counts as new; the bundle needs a rebuild")
        stale = True
    else:
        added, removed, changed = d
        for k in added:
            print("clips ADDED    " + k)
        for k in removed:
            print("clips REMOVED  " + k)
        for k in changed:
            print("clips CHANGED  " + k)
        stale = bool(added or removed or changed)

    if stale:
        print("BUNDLE STALE: pack\\Resources\\werewolf.unity3d does not match art\\sound - run build.ps1 -Bundle")
    elif not changed_roles and not problems:
        print("ok    sounds.xml and the bundle match art\\sound")

    if not check_only and not problems:
        # Written only by the real run: build.ps1 calls that right before it rebuilds the bundle,
        # so the manifest always describes the clips the bundle was built from.
        if "--mark-built" in sys.argv:
            with open(MANIFEST, "w", encoding="utf-8", newline="\n") as f:
                json.dump(now, f, indent=1, sort_keys=True)
            print("manifest written - the bundle about to be built is the reference from now on")

    if problems:
        return 2
    if check_only and (changed_roles or stale):
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
