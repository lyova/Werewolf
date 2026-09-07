r"""Resolves entity classes out of the game's config - following every `extends` - and prints the
stats that matter for balancing a new hostile animal, as a table and as JSON.

    python mods\Werewolf\tools\dump-entity-stats.py
    python mods\Werewolf\tools\dump-entity-stats.py --classes animalBear,animalDireWolf --json out.json

Damage comes from the class's HandItem resolved through items.xml, which also follows `Extends`,
because an entity_class carries no damage of its own.

The mod's own classes are picked up too when --mod-config points at a folder of xpath patches:
only <append> blocks are read, which is all this mod uses.
"""
import argparse
import json
import os
import re
import sys
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
MOD = os.path.abspath(os.path.join(HERE, ".."))
DEFAULT_GAME = r"D:\SteamLibrary\steamapps\common\7 Days To Die"

DEFAULT_CLASSES = "animalBear,animalZombieBear,animalDireWolf,animalZombieDog,animalWolf,animalWerewolf"


def load_classes(config_dir, mod_config_dirs):
    """-> {name: element}. Mod appends override/add on top of vanilla."""
    out = {}
    tree = ET.parse(os.path.join(config_dir, "entityclasses.xml"))
    for e in tree.getroot().findall("entity_class"):
        out[e.get("name")] = e
    for d in mod_config_dirs:
        path = os.path.join(d, "entityclasses.xml")
        if not os.path.isfile(path):
            continue
        for append in ET.parse(path).getroot().findall("append"):
            for e in append.findall(".//entity_class"):
                out[e.get("name")] = e
    return out


def load_items(config_dir, mod_config_dirs):
    """Same shape as load_classes: vanilla first, then the mod's <append> blocks on top."""
    out = {}
    for e in ET.parse(os.path.join(config_dir, "items.xml")).getroot().findall("item"):
        out[e.get("name")] = e
    for d in mod_config_dirs:
        path = os.path.join(d, "items.xml")
        if not os.path.isfile(path):
            continue
        for append in ET.parse(path).getroot().findall("append"):
            for e in append.findall(".//item"):
                out[e.get("name")] = e
    return out


def resolve(name, table, extends_attr, extends_prop):
    """Walk the inheritance chain from the root down, so a child overrides its parent."""
    chain, seen = [], set()
    while name and name in table and name not in seen:
        seen.add(name)
        element = table[name]
        chain.append(element)
        parent = element.get(extends_attr)
        if parent is None and extends_prop:
            for p in element.findall("property"):
                if p.get("name") == extends_prop:
                    parent = p.get("value")
                    break
        name = parent
    return list(reversed(chain))


def props_of(chain):
    props = {}
    for element in chain:
        for p in element.findall("property"):
            if p.get("name") and p.get("value") is not None:
                props[p.get("name")] = p.get("value")
        # Action0 and friends are nested property blocks
        for p in element.findall("property"):
            cls = p.get("class")
            if not cls:
                continue
            for q in p.findall("property"):
                props[cls + "." + q.get("name")] = q.get("value")
    return props


def health_of(chain):
    """Last base_set HealthMax down the chain wins."""
    value = None
    for element in chain:
        for group in element.findall("effect_group"):
            for eff in group.findall("passive_effect"):
                if eff.get("name") == "HealthMax" and eff.get("operation") == "base_set":
                    value = eff.get("value")
    return value


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--game", default=DEFAULT_GAME)
    ap.add_argument("--classes", default=DEFAULT_CLASSES)
    ap.add_argument("--mod-config", default=os.path.join(MOD, "pack", "Config"))
    ap.add_argument("--json", default=os.path.join(MOD, "art", "work", "entity-stats.json"))
    a = ap.parse_args()

    config = os.path.join(a.game, "Data", "Config")
    classes = load_classes(config, [a.mod_config])
    items = load_items(config, [a.mod_config])

    wanted = [c.strip() for c in a.classes.split(",") if c.strip()]
    rows = []
    for name in wanted:
        if name not in classes:
            print("  no entity_class '%s'" % name)
            continue
        chain = resolve(name, classes, "extends", None)
        p = props_of(chain)
        hand = p.get("HandItem")
        item = {}
        if hand and hand in items:
            item = props_of(resolve(hand, items, None, "Extends"))
        rows.append({
            "name": name,
            "extends": " < ".join(e.get("name") for e in reversed(chain)),
            "class": p.get("Class"),
            "health": health_of(chain),
            "mass": p.get("Mass"),
            "sizeScale": p.get("SizeScale", "1"),
            "moveSpeed": p.get("MoveSpeed"),
            "moveSpeedAggro": p.get("MoveSpeedAggro"),
            "moveSpeedPanic": p.get("MoveSpeedPanic"),
            "jumpMaxDistance": p.get("JumpMaxDistance"),
            "sightRange": p.get("SightRange"),
            "painResist": p.get("PainResistPerHit"),
            "deadBodyHp": p.get("DeadBodyHitPoints"),
            "xp": p.get("ExperienceGain"),
            "lootProb": p.get("LootDropProb"),
            "lootClass": p.get("LootDropEntityClass"),
            "physicsBody": p.get("PhysicsBody"),
            "ragdoll": p.get("HasRagdoll"),
            "flags": p.get("EntityFlags"),
            "handItem": hand,
            "dmgEntity": item.get("Action0.DamageEntity"),
            "dmgBlock": item.get("Action0.DamageBlock"),
            "range": item.get("Action0.Range"),
            "delay": item.get("Action0.Delay"),
        })

    fields = [("health", "Health"), ("dmgEntity", "Dmg entity"), ("dmgBlock", "Dmg block"),
              ("range", "Range"), ("delay", "Attack delay"), ("moveSpeed", "Move"),
              ("moveSpeedAggro", "Move aggro"), ("mass", "Mass"), ("sizeScale", "Size"),
              ("xp", "XP"), ("painResist", "Pain resist"), ("deadBodyHp", "Corpse HP"),
              ("sightRange", "Sight"), ("lootProb", "Loot prob")]
    width = max(len(r["name"]) for r in rows) + 2
    print("%-14s" % "" + "".join(("%-" + str(width) + "s") % r["name"] for r in rows))
    for key, label in fields:
        print("%-14s" % label + "".join(("%-" + str(width) + "s") % (r.get(key) or "-") for r in rows))

    os.makedirs(os.path.dirname(a.json), exist_ok=True)
    with open(a.json, "w", encoding="utf-8") as f:
        json.dump(rows, f, indent=1, ensure_ascii=False)
    print("\nwrote " + a.json)
    return 0


sys.exit(main())
