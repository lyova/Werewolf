# Werewolf

**[Download on Nexus Mods](https://www.nexusmods.com/7daystodie/mods/12493)**

A new apex predator for **7 Days to Die V 3.2.0**, built around
[this character asset from Fab](https://www.fab.com/listings/e6d3f2fc-f64d-418b-b41d-1f8808be3f21).

---

Nobody agrees on where they came from.

Some say the wolves were the first to drink from the rivers after the bombs, and that whatever was
in the water finished what the radiation started. Others swear they were people once — the ones who
kept running north when everyone else dug in, until the winter and the hunger made them into
something that never needed a base at all.

The traders don't care either way. They just quote the going rate for the pelt, glance at the
door, and remind you they close at dusk.

What everyone does agree on is this: the horde is loud, slow and predictable. **This thing hunts.**

---

## Beware the night!

The werewolf only hunts after dark (22:00–04:00) — but if you've run into one, you'll remember it.
It's furious, it's strong, and it's *fast*. It hates zombies every bit as much as it hates you, and
it will happily tear them apart if they're stupid enough to touch it. Outrunning it is close to
impossible — but if you put it down, the payout and the XP are worth every bullet.

**Shore up your base — it goes through walls about as well as it goes through faces.**

On top of all that it has a howl, which calls another werewolf to its side (never more than two
extra at once).

## What you're dealing with

- **It runs you down.** Sprinting is not an escape plan. It's faster than you are, day or night,
  and it's faster still after dark. Break line of sight, get behind something solid, or fight.
- **It hits like a zombie bear.** Roughly that league for health and damage — but a bear doesn't
  chase you across a field at full tilt.
- **It hears you.** Shoot near one from behind a wall and it will come to the noise and commit to
  it. A wall you're shooting over is no longer a safe spot.
- **It goes through walls.** Not "eventually" — it is a brutal block breaker, and it will pick the
  wall over the long way round. Wood is a formality. Concrete buys you time, not safety.
- **It hunts zombies too.** Anything that scratches it gets torn up first — and it does not stop at
  one: it clears out everything crowding around it before it comes back for you. Bring it into a
  horde and watch what happens.
- **The howl.** A summon, not decoration: it rears up, roars, and another one comes in out of the
  dark to join the fight. The pack tops out at three, then the howl goes quiet for a while.
- **Pack animals don't turn on each other.** Their claws pass straight through their own kind, so
  don't count on them fighting it out.
- **Worth the fight.** A serious pile of XP, and a loot bag **every single time** — the boss kind,
  not the scraps a regular zombie leaves. On top of that a full harvest: meat, leather, fat and
  bone, because the corpse butchers like an animal rather than like a rotting zombie.
- **Three coats.** Snow-white in the snow, sand-brown in the desert, near-black everywhere else.

## When it shows up

Not on day one, and that's deliberate. Spawns are gated behind **game stage 125** — call it
somewhere around **day 50** for a survivor levelling at a normal pace — and only inside the night
window. Before that the world spawns none at all, in any biome.

The idea is that it turns up right when you've stopped being afraid of the dark. It's roughly a
notch under the demolisher's gate, and well above the zombie bear's, because unlike a horde night
this one comes on an ordinary Tuesday while you're out looting.

Every biome is fair game once you're past the gate. There's a world-wide cap on how many of them
wander around at once, so you get to *meet* one rather than trip over a stream of them.

## Requirements

- 7 Days to Die **V 3.2.0** (built and tested against it; should work on any 3.x).
- Launch **without EasyAntiCheat** — the mod ships a DLL and EAC blocks those. (The creature itself
  doesn't need it; see below.)
- Works on an existing save.

## Installation

1. Extract the archive into `<game folder>\Mods\` so you end up with
   `<game folder>\Mods\Werewolf\ModInfo.xml`.
2. Start the game without EAC.

To uninstall, delete the folder. Any werewolf alive in your save disappears with it.

## Multiplayer

Not client side — the mod adds an entity class and its assets, and both ends read those from their
own files, so the server and every client need the same version. Tested in single player so far.

## About the DLL

The creature itself is XML, a prefab and an asset bundle — no code required for it to exist. The
DLL adds the parts the game has no concept of: the howl and its summon, the pack rules, the spawn
gate, and a `werewolf` console command for tuning and tracing. It's also why the mod has to be
skipped under anti-cheat.

## Building

```powershell
.\build.ps1 -Deploy
```

Sound clips, the model or the textures changed? Rebuild the bundle too — this also re-syncs
`sounds.xml` with `art\sound\` and refuses to deploy a stale bundle:

```powershell
.\build.ps1 -Bundle -Deploy
```

From the purchased source asset (only needed when that asset changes):

```powershell
python tools\prepare-textures.py
& 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe' --background --factory-startup --python tools\blender-prepare-werewolf.py
.\build.ps1 -Bundle -Deploy
```

Testing in game: `dm`, then `se 0 animalWerewolf` (`animalWerewolfIce`, `animalWerewolfBrown` for
the other coats). `werewolf gate off` keeps one alive in daylight, and `werewolf trace` prints what
its AI is actually doing.

## Documentation

- [NOTES.md](NOTES.md) — how it's built and why: the rig, the bundle, the animator, the XML, and
  the game internals each decision rests on.
- [CHANGELOG.md](CHANGELOG.md).

## Credits and license

The creature model, its textures and its animations are a character asset licensed from
**[Fab](https://www.fab.com/listings/e6d3f2fc-f64d-418b-b41d-1f8808be3f21)** — all credit for the
artwork goes to its author. Everything else here is the work of fitting it into 7 Days to Die: the
rig, the animator, the prefab, the entity, its AI and its voice.

MIT for the code and configuration — see [LICENSE](LICENSE).

The art in `Resources/` ships as part of this mod under that licence and **may not be extracted or
reused**.
