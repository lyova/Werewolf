r"""Checks this mod's configuration against the game's, offline, for the two mistakes that are
INVISIBLE in play.

    python mods\Werewolf\tools\check-config-patches.py [--game "<7 Days To Die dir>"]

1. Every xpath in pack\Config has to match something in the game's own config. A modlet patch that
   misses is silent - the creature still spawns, so nothing looks broken, and the missing piece is
   only found by wondering why it never happens. That is exactly how a spawn group or a damage
   bonus goes missing.

2. sounds.xml has to agree with art\sound. The clip names in that file are assigned by the build
   script counting files: role folder "alert" with six files becomes ww_alert1..6. A line pointing
   at a clip that does not exist logs one warning at load and then plays silence; a clip with no
   line is never heard, and looks exactly like a clip nobody happened to roll.

Anything wrong is reported and the exit code says so.
"""
import io
import os
import re
import sys
import xml.etree.ElementTree as ET

MOD = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
GAME = r"D:\SteamLibrary\steamapps\common\7 Days To Die"
if "--game" in sys.argv:
    GAME = sys.argv[sys.argv.index("--game") + 1]

VANILLA = os.path.join(GAME, "Data", "Config")
PATCH_OPS = {"append", "set", "setattribute", "remove", "removeattribute",
             "insertAfter", "insertBefore", "csv"}

failures = 0
checked = 0


# ------------------------------------------------------------------ xpaths
# ElementTree understands a useful subset of xpath. These are the shapes this mod uses; anything
# fancier would need lxml, and the check reports a skip rather than passing something it did not
# actually test.
def to_etree_xpath(xpath):
    x = xpath.strip()
    if not x.startswith("/"):
        return None
    parts = [p for p in x.split("/") if p]
    # The first segment is the document root, which findall() is already relative to.
    return "./" + "/".join(parts[1:]) if len(parts) > 1 else "."


for name in sorted(os.listdir(os.path.join(MOD, "pack", "Config"))):
    if not name.lower().endswith(".xml"):
        continue
    mod_path = os.path.join(MOD, "pack", "Config", name)
    vanilla_path = os.path.join(VANILLA, name)

    if not os.path.exists(vanilla_path):
        print("SKIP  %s - the game has no file by that name (a new file, not a patch)" % name)
        continue

    target = ET.parse(vanilla_path).getroot()
    patch = ET.parse(mod_path).getroot()

    for op in patch:
        if not isinstance(op.tag, str) or op.tag not in PATCH_OPS:
            continue
        xpath = op.get("xpath")
        if xpath is None:
            print("FAIL  %s: <%s> with no xpath attribute" % (name, op.tag))
            failures += 1
            continue

        et = to_etree_xpath(xpath)
        if et is None:
            print("SKIP  %s: %s <%s> - not an absolute path, not checked" % (name, xpath, op.tag))
            continue

        try:
            hits = target.findall(et)
        except SyntaxError as e:
            print("SKIP  %s: %s - ElementTree cannot parse this xpath (%s)" % (name, xpath, e))
            continue

        checked += 1
        if hits:
            added = [c.tag for c in op if isinstance(c.tag, str)]
            print("ok    %s: %-70s %d hit(s)%s"
                  % (name, xpath, len(hits), "  adds " + ", ".join(added) if added else ""))
        else:
            print("FAIL  %s: %s matched NOTHING in the game's %s" % (name, xpath, name))
            failures += 1


# ------------------------------------------------------------------ sounds
SOUND_DIR = os.path.join(MOD, "art", "sound")
SOUND_XML = os.path.join(MOD, "pack", "Config", "sounds.xml")
# Kept out of the bundle by the build script: ripped game audio, held for comparison only.
SKIP_ROLES = {"reference"}
PREFIX = "ww_"

if os.path.isdir(SOUND_DIR) and os.path.exists(SOUND_XML):
    print()

    # What the build script will name the clips inside the bundle.
    on_disk = {}
    for role in sorted(os.listdir(SOUND_DIR)):
        role_dir = os.path.join(SOUND_DIR, role)
        if not os.path.isdir(role_dir) or role in SKIP_ROLES:
            continue
        clips = sorted(f for f in os.listdir(role_dir)
                       if os.path.splitext(f)[1].lower() in (".mp3", ".wav", ".ogg"))
        for i, clip in enumerate(clips, start=1):
            on_disk["%s%s%d" % (PREFIX, role, i)] = os.path.join(role, clip)

    # What sounds.xml asks for.
    xml = io.open(SOUND_XML, encoding="utf-8").read()
    referenced = set(re.findall(r'ClipName="[^"]*\?(' + re.escape(PREFIX) + r'[A-Za-z0-9_]+)"', xml))

    missing = sorted(referenced - set(on_disk))
    unused = sorted(set(on_disk) - referenced)

    for clip in missing:
        print("FAIL  sounds.xml asks for %s, which no file under art\\sound produces" % clip)
        failures += 1
    for clip in unused:
        print("FAIL  %s exists (art\\sound\\%s) but sounds.xml never lists it - never heard"
              % (clip, on_disk[clip]))
        failures += 1
    if not missing and not unused:
        roles = sorted({c[len(PREFIX):].rstrip("0123456789") for c in on_disk})
        print("ok    sounds.xml matches art\\sound: %d clips across %s"
              % (len(on_disk), ", ".join(roles)))

print()
print("%d xpath(s) checked, %d failure(s) in total" % (checked, failures))
sys.exit(1 if failures else 0)
