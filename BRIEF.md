# Werewolf — implementation brief

A new hostile animal for **7 Days to Die V3.2.0**, built around a purchased character asset. This
file is everything established before implementation started, so the work can begin without
rediscovering it. Every game fact below was read out of the shipped game (`tools\dump-cecil.ps1`,
`tools\scan-cecil.ps1`, the addressables catalog) rather than assumed.

Sibling mod `mods\ZTwinBrothers` is the reference implementation for the **zombie** path. This one
takes the **animal** path, which is a genuinely different code route — see "Why the animal path".

## The asset

Purchased on Fab: **Werewolf** by Lilpupinduy, Fab **Standard License, Personal tier**.
Unity package extracted to `art\source\unitypackage\` (gitignored).

`Models\Werewolf.fbx` — verified contents:

| | |
|---|---|
| armature | **96 bones**, root bone named `root` |
| meshes | 4 LODs as separate objects: `Werewolf0` **22,724** tris / `Werewolf1` 15,148 / `Werewolf2` 10,080 / `Werewolf3` 6,726 |
| skinning | all four skinned to the same armature, 96 vertex groups |
| material | one, `lambert4`; one UV set, `map1` |
| scale | authored in **centimetres** — bbox is 243 units tall, i.e. **2.43 m** |
| origin | **not at the feet** — z spans −144.3 to +99.0 |

Textures, all **4096×4096** TGA: four albedo variants (`Brown`, `Dark`, `Gray`, `Ice`), four
matching **emission** maps, one shared `T_Werewolf_Normal`, one shared
`T_Werewolf_MetallicSmoothness`. Animation FBXs: `Werewolf_Anim` (the bulk),
`Werewolf_Eating`, `Werewolf_Eating_Out`, `Werewolf_Run_4_Legs`, `Werewolf_Walk_4_Legs`, plus the
seller's own `Werewolf Animator Controller.controller` — useful to read for how its states are
laid out, not usable as-is.

Three things about the asset that shape the whole plan:

- **It moves on four legs.** `Werewolf_Run_4_Legs` and `Werewolf_Walk_4_Legs` are its locomotion.
  Rigging it to the game's humanoid zombie skeleton would throw away exactly the animations that
  make it what it is.
- **The materials are URP** (`UniversalRenderPipelineAsset`, a `.shadergraph`). 7DTD is built-in
  pipeline, so the shipped materials and shader cannot be used. The textures can.
- **Four colour variants with their own emission maps** map naturally onto the game's tier system.

### Licence — read `LICENSE` before shipping anything

The Fab Standard License permits incorporating the art into a project and distributing that
project, free or commercially. It forbids redistributing the art **on a standalone basis**, and
Section 4(c) obliges you to *restrict end users from extracting or otherwise using* it outside the
project. `LICENSE` in this folder already splits the mod in two accordingly: MIT for `Config\` and
`src\`, an explicit carve-out for `Resources\`. **Do not ship a blanket MIT notice next to the
bundle** — that would purport to grant rights over the purchased art that we do not have, which is
exactly what the Fab EULA's Section 6(a) prohibits.

The listing also carries the seller's flag **"Allows usage with AI: No"**. Read as covering AI
training and generative use rather than scripted edits, but it is their stated position.

## Why the animal path, and how it differs

Animals do **not** load like zombies. Verified in `EntityClass.Init` and `EModelBase.createModel`:

| | zombie (ZTwinBrothers) | animal (this mod) |
|---|---|---|
| model property | `Mesh` — a model instantiated into the entity's `Graphics/Model` | `Prefab` + `PrefabCombined="true"` — a **whole prefab**, taken as `modelTransformParent.GetChild(0)` |
| controller | the game's `ZController`, loaded at runtime by our DLL | **ours**, shipped inside the prefab |
| avatar | Humanoid, mapped to vanilla's 55-bone human description | none needed — no humanoid retargeting |
| bone names | **62 exact names required** | **6 names required** (below) |

`Prefab` is mandatory on every `entity_class` (`EntityClass.Init` throws
`Mandatory property 'prefab' missing` without it) and is loaded through
`EntityInstanceAssets.Load` → `LoadManager.LoadAsset<GameObject>`. **That path understands mod
bundles**: `LoadManager` resolves through `DataLoader/DataPathIdentifier` and reads its `IsBundle`,
`BundlePath` and `FromMod` fields. So this works:

```xml
<property name="Prefab" value="#@modfolder(Werewolf):Resources/werewolf.unity3d?Werewolf"/>
<property name="PrefabCombined" value="true"/>
```

The leading `#` is what marks it a bundle rather than an Addressable; there is no loose-file case.

## What the game requires of the rig — six bone names

`AvatarAnimalController.assignBodyParts` finds body parts by name with a recursive
`FindInChilds`, and it looks for exactly six:

```
Hips   Head   LeftUpLeg   RightUpLeg   LeftArm   RightArm
```

**None of them exist in the purchased rig.** It uses Unreal naming, so the renames are:

| purchased | rename to |
|---|---|
| `pelvis` | `Hips` |
| `head` | `Head` |
| `thigh_l` | `LeftUpLeg` |
| `thigh_r` | `RightUpLeg` |
| `upperarm_l` | `LeftArm` |
| `upperarm_r` | `RightArm` |

The other 90 bones — `spine_01..05`, `neck_01`, `clavicle_*`, `lowerarm_*`, `hand_*`, fingers,
`calf_*`, `foot_*`, `ball_*`, toes, `Tail0..5`, `Ear_l/r`, `Jaw`, the twist bones — can keep their
names. Nothing in the game looks for them.

**Rename the real bones; do not fake it with empty transforms named `Head` and so on.** The game
hides and detaches those transforms for dismemberment, so a decoy would satisfy the lookup and
then misbehave the moment a limb comes off.

## What the game requires of the animator controller

This is the largest piece of new work, and the reason it is new: for the zombie we borrowed the
game's `ZController` and got all of its logic free. Here the controller is ours to author, in the
Unity project, wired to the seller's clips.

`AvatarAnimalController` writes **22 parameters**. Names and types are exact:

| type | parameters |
|---|---|
| Float | `Forward`, `Strafe`, `IdleTime` |
| Int | `Attack`, `MovementState`, `Random`, `BodyPartHit`, `HitDamage`, `HitDirection` |
| Bool | `IsAlive`, `IsDead`, `IsMoving`, `IsSwim`, `CriticalHit` |
| Trigger | `AttackTrigger`, `DeathTrigger`, `PainTrigger`, `ElectrocuteTrigger`, `JumpStart`, `JumpLand`, `BeginCorpseEat`, `EndCorpseEat`, `TriggerAlive` |

**And it reads STATE TAGS, which is the non-obvious part.** `AttackStart`, `Death`, `Hit` and
`Attack` are compared against `AnimatorStateInfo.tagHash` — so states must carry those values in
their **Tag** field (not their name; in Unity those are separate). `Attack` serves as both an int
parameter and a state tag. `AttackReady` is referenced too; confirm its use when authoring.

Alongside the tags the controller reads **`normalizedTime`**, so the game expects meaningful
timing *within* a tagged state — when the attack lands, when death has finished. Getting that
right is the real cost here, not the wiring. Without correct tags the game never learns that the
animal is attacking, dying or being hit, and damage timing, death handling and hit reactions all
misfire silently.

## Textures — the conversion needed

The purchased maps are Unity Standard convention; the game wants its own packing. Vanilla animals
use **RMO** plus a separate **DLM** (`dire_wolf_RMO.tga`, `dire_wolf_DLM.tga`), not the zombies'
RMOE — so verify the animal material's property names before packing, rather than reusing the
zombie layout wholesale.

Two traps, both paid for already on the sibling mod:

- **Smoothness is not roughness.** `T_Werewolf_MetallicSmoothness` follows Unity's convention
  (metallic in R, smoothness in A). Roughness is `1 − smoothness`; ship it un-inverted and the
  creature comes out looking like wet plastic.
- **There is no AO map in the package**, despite the listing mentioning AORM. Bake one
  (`tools\model\blender-bake-normal.py --type ao`, on the low-poly alone) or leave occlusion at
  1.0. Do not leave it at 0 — occlusion 0 kills all ambient light.

And the rule that catches both: **measure every channel of a packed map against the vanilla
equivalent before shipping it.** The sibling mod shipped an RMOE with R, G and B all exactly zero
— roughness 0 is a perfect mirror — and it was caught only by that comparison. Three lines of
histogram.

## Reference points in the game

Existing animals to copy from and to balance against. All resolved through their `extends` chains:

| class | Prefab | PhysicsBody | health |
|---|---|---|---|
| `animalWolf` | `@:Entities/Animals/Wolf/animalWolf.prefab` | `AWolf` | 200 |
| `animalDireWolf` | `@:Entities/Animals/DireWolf/animalDireWolfPrefab.prefab` | `AWolf` | 3000, SizeScale 1.4 |
| `animalZombieDog` | `@:Entities/Animals/Wolf/animalStandardDogZombieRagdoll.prefab` | `zombieDog` | 200 |

24 animal classes exist; most run `Class=EntityEnemyAnimal` with
`AvatarController=AvatarAnimalController`. `animalWolf` also sets `HasRagdoll=true` and
`RagdollOnDeathChance=.5`.

**`PhysicsBody` is an open question.** It names a collider set attached to bones, and `AWolf` is
built for the wolf's skeleton and its bone names. Our rig is a different skeleton, so check what
`AWolf` actually expects before assuming it fits; `zombieDog` and `zombieStandard` are the other
candidates. Get this wrong and hit detection lands in the wrong places.

To read any of it directly: `automatic_assets_entities\animals.bundle` is 387 MB and
`tools\rip-game-assets.ps1` already takes `-BundlePath` and `-PrimaryOnly`.

## Reusable tooling

`tools\` (repo root) — all of it already written and used on the sibling mod:

| | |
|---|---|
| `build-asset-bundle.ps1 -Mod Werewolf -Prefab` | copies art in, configures importers, builds the prefab and the bundle headlessly |
| `rip-game-assets.ps1 -BundlePath … -PrimaryOnly` | rip any game bundle for reference |
| `dump-cecil.ps1 -Types … -IL` | read game types and IL; reflection does not work on `Assembly-CSharp` |
| `scan-cecil.ps1 -Pattern …` | "who in the whole assembly touches this" |
| `check-toolchain.ps1` | run at the start of a session |
| `model\blender-*.py`, `pack-rmoe.py`, `make-emission-mask.py` | mesh prep, binding, baking, map packing |

`mods\ZTwinBrothers\unity\Assets\Editor\` holds `ModelSetup.cs` and `BundleBuilder.cs` — the
prefab builder and bundle packer. Copy them as the starting point for this mod's Unity project;
the audio and texture handling in them is directly reusable, the humanoid-avatar half is not.

## Things to settle before or while building

1. **Scale and origin.** 2.43 m and an origin 1.44 m below the mesh. Decide the intended in-game
   size (a direwolf is `SizeScale 1.4`) and put the origin at the feet.
2. **Which LOD ships, and whether all four do.** The sibling mod ships a single LOD and no
   `LODGroup`; vanilla Boe ships three. Four are available here for free — worth using, since a
   horde puts many on screen.
3. **`PhysicsBody`** — see above.
4. **Tiers from the colour variants.** Brown / Dark / Gray / Ice with their own emission maps is a
   ready-made ladder. Note vanilla's `MatColor` tint is a separate mechanism and feral carries no
   tint at all.
5. **Whether a DLL is needed.** Probably yes, for the same reason as the sibling mod: the game
   talks to a creature material by property name, and the purchased URP material cannot answer.
   `ZTwinBrothers\src\ModelFixup.cs` is the pattern — instantiate a vanilla material, swap our
   textures onto it, and nothing of The Fun Pimps' is redistributed.

## House rules

From `CLAUDE.md` at the repo root and in the sibling mod:

- Headless Blender is **always** `--background --factory-startup`. The owner's Blender runs
  add-ons that grab fixed ports at start-up (the MCP bridge on 9876 among them); a background run
  that takes a port first breaks the GUI session until restart.
- Every file in `pack\Config` is an xpath patch: `<configs>` wrapping `<append>` / `<set>`, never
  restating vanilla content. A wrong xpath does not crash — it logs `XML patch … did not apply`
  and silently does nothing. Check the log after any XML change.
- `art\` is gitignored wholesale, including the extracted package. The purchased asset must never
  be committed.
- Do not open the Unity editor GUI when `build-asset-bundle.ps1` would do.
- The game is at `D:\SteamLibrary\steamapps\common\7 Days To Die`; launch **without** EAC
  (`7DaysToDie.exe`, not `7DaysToDie_EAC.exe`) or `SkipWithAntiCheat` drops the DLL.

Spawning, for testing: `dm` in the console, then `se 0 <entityClassName>` (0 is the local player;
the second argument takes the class name as well as an index). `ai fp` freezes movement,
`ai anim attack` triggers an attack on every zombie, `ai rage 1.5 10` forces rage. `sm` is
spectator mode.
