r"""Pulls one creature's voice out of the ripped game sound bundle and sorts it into per-role
folders, as reference material for generating this mod's own clips.

    python mods\Werewolf\tools\extract-reference-sounds.py
    python mods\Werewolf\tools\extract-reference-sounds.py --prefix bear --roles roam,alert,attack,pain,death

The role -> clip mapping is read from the GAME's own sounds.xml rather than guessed from file
names, so it is exactly what the entity would play. The clips themselves come from the rip:

    .\tools\rip-game-assets.ps1 -Rip -PrimaryOnly -Name animal-sounds ^
        -BundlePath "<game>\Data\Addressables\Standalone\automatic_assets_sounds\animals.bundle" ^
        -OutDir mods\Werewolf\art\reference

The game's XML names its clips .wav; AssetRipper decodes them to .ogg, which is the same audio.

NOTHING FROM HERE SHIPS. These are The Fun Pimps' assets - they are reference for a generator to
listen to, and they live under art\reference and art\work\sound\reference, both gitignored. The
mod's own clips go in art\work\sound\<role>\ and are the only ones the bundle picks up.
"""
import argparse
import os
import re
import shutil
import struct
import sys
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
MOD = os.path.abspath(os.path.join(HERE, ".."))
DEFAULT_GAME = r"D:\SteamLibrary\steamapps\common\7 Days To Die"


def ogg_duration(path):
    """Seconds, from the Vorbis identification header and the last page's granule position."""
    with open(path, "rb") as f:
        data = f.read()
    head = data.find(b"\x01vorbis")
    if head < 0:
        return None, None, None
    channels = data[head + 11]
    rate = struct.unpack("<I", data[head + 12:head + 16])[0]
    last = data.rfind(b"OggS")
    if last < 0 or rate == 0:
        return None, channels, rate
    granule = struct.unpack("<q", data[last + 6:last + 14])[0]
    return granule / float(rate), channels, rate


def roles_from_sounds_xml(sounds_xml, prefix, roles):
    """-> {role: [clip base name, ...]} straight out of the game's SoundDataNodes."""
    text = open(sounds_xml, encoding="utf-8", errors="replace").read()
    found = {}
    for role in roles:
        node = prefix + role
        m = re.search(r'<SoundDataNode name="%s">(.*?)</SoundDataNode>' % re.escape(node), text, re.S)
        if not m:
            print("  no <SoundDataNode name=\"%s\"> in sounds.xml" % node)
            continue
        clips = re.findall(r'ClipName="[^"]*/([^/"]+)\.wav"', m.group(1))
        if clips:
            found[role] = clips
    return found


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--prefix", default="wolfdire", help="sound node prefix, e.g. wolfdire, bear, wolf")
    ap.add_argument("--roles", default="roam,alert,attack,pain,death,sense,giveup")
    ap.add_argument("--game", default=DEFAULT_GAME)
    ap.add_argument("--rip", default=os.path.join(MOD, "art", "reference", "animal-sounds-models"))
    ap.add_argument("--out", default=os.path.join(MOD, "art", "work", "sound", "reference"))
    a = ap.parse_args()

    sounds_xml = os.path.join(a.game, "Data", "Config", "sounds.xml")
    if not os.path.isfile(sounds_xml):
        print("no sounds.xml at " + sounds_xml)
        return 1
    if not os.path.isdir(a.rip):
        print("no rip at " + a.rip + " - see the header of this file for the rip-game-assets command")
        return 1

    # Where the decoded clips ended up, by base name.
    pool = {}
    for root, _, files in os.walk(a.rip):
        for name in files:
            if name.lower().endswith((".ogg", ".wav", ".mp3")):
                pool.setdefault(os.path.splitext(name)[0].lower(), os.path.join(root, name))

    roles = [r.strip() for r in a.roles.split(",") if r.strip()]
    mapping = roles_from_sounds_xml(sounds_xml, a.prefix, roles)
    if not mapping:
        print("nothing matched the prefix '%s'" % a.prefix)
        return 1

    if os.path.isdir(a.out):
        shutil.rmtree(a.out)
    total, missing = 0, 0
    print("%s -> %s" % (a.prefix, a.out))
    for role in roles:
        clips = mapping.get(role, [])
        if not clips:
            continue
        dest = os.path.join(a.out, role)
        os.makedirs(dest, exist_ok=True)
        lengths = []
        for clip in clips:
            src = pool.get(clip.lower())
            if src is None:
                print("  %-8s %s: NOT IN THE RIP" % (role, clip))
                missing += 1
                continue
            shutil.copy2(src, os.path.join(dest, os.path.basename(src)))
            seconds, channels, rate = ogg_duration(src)
            if seconds:
                lengths.append(seconds)
            total += 1
        if lengths:
            print("  %-8s %2d clips, %.2f-%.2f s (mean %.2f), %d Hz %s"
                  % (role, len(lengths), min(lengths), max(lengths), sum(lengths) / len(lengths),
                     rate, "mono" if channels == 1 else "%dch" % channels))
        else:
            print("  %-8s %2d clips" % (role, len(clips)))

    print()
    print("%d clips copied%s" % (total, ", %d missing" % missing if missing else ""))
    print("reference only - nothing here ships; the mod's own clips go in "
          + os.path.join(MOD, "art", "work", "sound", "<role>"))
    return 1 if missing else 0


sys.exit(main())
