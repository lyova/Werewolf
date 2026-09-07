# Implementation notes

What the game actually does with an animal, what was built for it and how, so the next stage -
wiring the XML and testing in game - does not have to rediscover it. Player-facing text is in
[README.md](README.md); the asset facts that predate the build are in [BRIEF.md](BRIEF.md). Every
game fact below was read out of the shipped game (`tools\dump-cecil.ps1 -IL`, `tools\scan-cecil.ps1`,
`mods\Werewolf\tools\find-stfld.ps1`, the ripped `animals.bundle`), not assumed.

## Where it stands

| step | state |
|------|-------|
| 1. asset inspected | done - see BRIEF.md; corrections below ("What the brief got wrong") |
| 2. rig prepared in Blender | done - six bones renamed, six FBXs re-exported, `art\export\` |
| 3. textures converted | done - `art\textures\`, 2048, packed for Unity's Standard (Specular) shader |
| 4. Unity project | done - `unity\Assets\Editor\*.cs`; builds headlessly |
| 5. animator controller | done - authored in code from the seller's clips, vanilla wolf's shape |
| 6. prefab + bundle | done - **brown only**, `pack\Resources\werewolf.unity3d` 6.8 MB, loads back by name, poses render-checked |
| 7. XML | written - `entityclasses.xml`, `physicsbodies.xml`, `Localization.csv`. **Untested in game** |
| 8. DLL | **not needed** - see "Why there is no DLL" |
| 9. deployed | done - `.\mods\Werewolf\build.ps1 -NoCode -Deploy` |
| 10. in-game test | **this is the next step** - checklist under "What the XML has to say" |
| 11. biome variants | deliberately not yet - see "Biome variants" |

## Build

```powershell
python mods\Werewolf\tools\prepare-textures.py
& 'C:\Program Files\Blender Foundation\Blender 5.2\blender.exe' --background --factory-startup --python mods\Werewolf\tools\blender-prepare-werewolf.py
.\tools\build-asset-bundle.ps1 -Mod Werewolf -Prefab
.\mods\Werewolf\build.ps1 -NoCode -Deploy
```

The first two only need re-running when the source asset changes. `-Prefab` copies `art\export`
and `art\textures` into `unity\Assets\Werewolf`, configures the importers, builds the controller,
materials and prefabs (`ModelSetup.All`), then the bundle (`BundleBuilder.Build`). Logs land in
`dist\unity-prefab.log` and `dist\unity-build.log`; every line the build prints starts with
`[Werewolf]`. A rebuild after the first takes about two minutes.

Two checks worth running after any rebuild, both offline:

```powershell
python mods\Werewolf\tools\check-physicsbody-paths.py
```

resolves every `<collider>` path in `physicsbodies.xml` against the built prefab the way
`PhysicsBodyInstance.bindCollider` does, and fails if one would land nowhere or on a transform with
no collider. Both are silent in game - the creature spawns and simply cannot be hit there. As of
2026-09-06 all twelve resolve.

`WerewolfPreview.Render` renders the prefab in ten poses to `art\work\preview\*.png` and logs, per
clip, how many animation curve bindings resolve on the prefab; that number must equal the total
(970 here). Run it through Unity with `-batchmode` but **without** `-nographics` - the command is
in the header of `unity\Assets\Editor\WerewolfPreview.cs`.

## What the brief got wrong

- **Bone count is 96 - the brief was right, the first Blender look was wrong.** Blender's FBX
  importer default *Ignore Leaf Bones* silently drops the 19 chain tips (`*_03` fingers,
  `*Toe3_*`, `Tail5`), so an inspection with the default reported 77. `blender-prepare-werewolf.py`
  imports with `ignore_leaf_bones=False`; the built prefab's renderers report 96 bones. Use
  `blender-inspect-fbx.py --keep-leaf` to see them all.
- **The origin IS at the feet.** The "z spans -144 to +99" was raw vertex data before the
  armature's transform; in world space the mesh stands on z = 0.008 m. Nothing to fix.
- **2.43 m is the length, not the height.** Height 2.27 m, length 2.43 m, width 1.88 m (the arms).
- **The material problem does not need a DLL** - see below.

## Game facts, checked against this install

**How an animal is loaded.** `EntityClass.Init` reads `Prefab` (mandatory) and `PrefabCombined`;
`EntityInstanceAssets` loads the prefab through `LoadManager`, which resolves
`#@modfolder(Werewolf):Resources/werewolf.unity3d?Werewolf` (leading `#` = asset bundle, the
part after `?` = asset name; `AssetBundle.LoadAsset(name)` finds the prefab by its short name -
verified when the bundle was loaded back after the build). `EntityFactory.CreateEntityOperation.
CompleteEntity` instantiates it, takes the child named **`GameObject`** as `Entity.ModelTransform`
and the child named **`Physics`** as `Entity.PhysicsTransform`. `EModelBase.createModel` with
`IsPrefabCombined` takes **the first child of `GameObject`** as the model. So the prefab layout is
fixed and ours copies `animalWolf.prefab` node for node:

```
Werewolf
  GameObject                 tag E_Enemy
    Model                    [Animator, LODGroup]         <- first child, the model
      Armature/root          tag E_BP_BipedRoot
        Hips ... Head        tag E_BP_Head; capsules + rigidbodies on 12 bones
      LOD0..LOD3             tag LOD  [SkinnedMeshRenderer]
  Physics                    tag Physics, layer 15 (CC Physics)  (1 capsule)
```

No `LargeEntityBlocker` node, deliberately. `Entity.PhysicsInit` finds that tag, reparents the node
to the world and adds a dynamic Rigidbody to it; `Entity.ApplyFixedUpdate` then snaps the entity's
position to that rigidbody every frame. Copied from the bear, it made the werewolf a PhysX body that
could not fit through a 2-block doorway its character controller capsule walks through (7.3 m/s
moved per second inside the collision call, 0.7 m/s kept). Wolves have none. `src/WerewolfBlocker.cs`
also strips one at runtime.

`AvatarAnimalController.SwitchModelAndView` does `SetAnimator(GetModelTransform())`, i.e.
`GetComponent<Animator>()` on `Model` itself - the Animator has to be on that exact transform.

**Tags are indices.** A bundle stores `m_Tag` as `20000 + index in the project's tag list`, so
`unity\ProjectSettings\TagManager.asset` carries the game's own 62-entry table, read out of
`7DaysToDie_Data\data.unity3d` with UnityPy (the sibling mod's guesses were wrong in two places:
20021 is `E_BP_BipedRoot`, not "Origin", and 20037 is `LargeEntityBlocker`, not "Collider"). The
ones the prefab uses: E_Enemy 15, E_BP_BipedRoot 21, LargeEntityBlocker 37, Physics 43, LOD 54,
and E_BP_Body 10 / E_BP_Head 11 / E_BP_LArm 13 / E_BP_RArm 16 / E_BP_LLeg 17 / E_BP_RLeg 18 /
E_BP_LLowerArm 31 / E_BP_RLowerArm 32 / E_BP_LLowerLeg 33 / E_BP_RLowerLeg 34. Layers: 15 `CC
Physics`, 19 `LargeEntityBlocker`, 21 `Physics` (ragdoll), 0 `Default` (hit colliders). The whole
table is in the TagManager file.

**Which transforms the game finds, and how** (`EModelBase.SwitchModelAndView`):

| field | lookup |
|---|---|
| `meshTransform` | child tagged `E_Mesh`, else `Find("LOD0")`, else first Renderer - ours is `LOD0` |
| `headTransform` | tag `E_BP_Head` anywhere below - the prefab tags `Head` |
| `bipedRootTransform` | tag `E_BP_BipedRoot` - the prefab tags `root`; fallback names `Bip001`/`Bip01` |
| `bipedPelvisTransform` | child of biped root whose name contains `pelvis`, then `hips`, then `hip` - `Hips` |
| physics body | `new PhysicsBodyInstance(bipedRootTransform, layout, mode)` |

`AvatarAnimalController.assignBodyParts` then finds `Hips Head LeftUpLeg RightUpLeg LeftArm
RightArm` by name under the model (recursive). Those are the six renames; the other 71 bones keep
their Unreal names.

**physicsbodies.xml paths are `Transform.Find` from the biped root.** `PhysicsBodyInstance.
bindCollider` does `modelRoot.Find(path)` on each `<collider>` path, wraps the Box/Capsule/Sphere
collider it finds there (else a *null* collider that never registers a hit), adds a `Rigidbody` if
there is none, and sets the tag and the layer from the XML. A missing path logs `PhysicsBodies
{entity}, {body}, path not found {path}`. So the prefab has to carry the colliders (sizes are
authored, not configured) and the XML has to carry paths that match this rig - hence a new body
`AWerewolf` in `pack\Config\physicsbodies.xml`; vanilla's `AWolf` paths would resolve nothing.

**The animator: what `AvatarAnimalController` reads and writes.** Parameters, exact names:
Float `Forward Strafe IdleTime`; Int `Attack MovementState Random BodyPartHit HitDamage
HitDirection`; Bool `IsAlive IsDead IsMoving IsSwim CriticalHit`; Trigger `AttackTrigger
DeathTrigger PainTrigger ElectrocuteTrigger JumpStart JumpLand BeginCorpseEat EndCorpseEat
TriggerAlive`. State tags:

- layer 0, tag **`Attack`**: `IsAnimationAttackPlaying()` = a 0.5 s grace timer, then
  `!IsInTransition(0) && baseState.tagHash == Attack`. The attack states exit to Move at 0.75 of
  the 1 s clip, which is when the AI may start the next one.
- layer 0, tag **`Death`**: once `tagHash == Death && normalizedTime >= 1 && !IsInTransition`
  the death animation is over (`isInDeathAnim = false`, and `DoRagdoll` if `HasDeathAnim`).
- layer **1**, tag **`Hit`**: `hitLayerIndex = 1` whenever `PainResistPerHit >= 0` (Awake). The
  game drives **that layer's weight** itself (`InitHitDuration` / `Update`: target 0.15..0.8 per
  hit, then back to 0), and `IsAnimationHitRunning()` = `tag == Hit && normalizedTime < 0.55` (or in
  transition). So layer 1 must be the pain layer, additive, with an empty default state - exactly
  vanilla's `animalWolfController`. The Hit clip is imported with an **additive reference pose**
  (its own first frame) so it is a delta and not a whole pose added on top of the walk.
- `AttackStart` / `AttackReady` only matter for `StartAction(_animType >= 3000)` special actions,
  which this creature does not have.

**`Forward` is speed / 10.** `EntityAlive.updateSpeedForwardAndStrafe`: `speedForward = 0.5 *
speedForward + distance moved this tick`, steady state = 2 x metres per 0.05 s tick = m/s ÷ 10.
Vanilla's wolf blends idle / walk / run at 0 / 0.1 / 0.3, i.e. 1 m/s and 3 m/s. Ours: 0 / 0.15 /
0.5 (the constants at the top of `WerewolfAnimator.cs`).

**The clips carry no root motion.** `tools\blender-clip-motion.py`: every take moves the root
0.00 m. So `RootMotion` must be **false** in the XML (with it true the entity waits for root motion
that never comes), the game moves the body, and the blend tree only picks the gait. Feet slide when
the speed is far from a threshold; tune the thresholds or `MoveSpeed`/`MoveSpeedAggro`.

**Vanilla wolf controller, for reference** (`tools\unity-controller-summary.py` on the ripped
`animalWolfController.controller`): Base Layer default `Move` = 1D blend on Forward (idle 0, walk
0.1, run 0.3); AnyState -> `Attack` (AttackTrigger && Forward > 0.149) / `AttackStandingStill`
(Forward < 0.15), both tagged Attack, exit to Move at 0.74 / 0.61; AnyState -> `Death` (tag
Death), no exit; `Swim` blend; `Knockdown`/`GetUpChest` tagged Stun (no clips for us, skipped);
`idleSleep` on SleeperTrigger (skipped). Pain layer: additive, `Empty` default, `Pain` tagged Hit
on PainTrigger. The dire wolf adds `Eat` on BeginCorpseEat -> EndCorpseEat and turn blends on a
`TurnRate` float - a parameter we deliberately do **not** declare, because `AvatarController.
SetAnimator` enables turn-rate writing when it sees it and we have no turn clips.

**Materials: Unity's Standard shader is in the game.** The vanilla `wolf.mat` uses the built-in
`Standard (Specular setup)` with keywords `_NORMALMAP _SPECGLOSSMAP _EMISSION` (mountain lion,
boar, snake, chicken, vulture likewise; the dire wolf alone uses TFP's `Game/Animal/Fur`). A bundle
material on the same shader with the same keyword set therefore renders with a variant that is
known to be compiled into the player. That is what `Werewolf.mat` is. What is given up
against TFP's shader: `SetFade` (spawn dissolve; it skips shaders without `Game/Character` in the
name), the `MatColor` tint (`_EmissiveColor` - Standard has `_EmissionColor`, so the call is a
no-op) and `_ElectricShockEnabled`. None of those are needed for colour variants that are
their own prefabs.

## Why there is no DLL

The zombie mod needs one for two things an animal does not: the game's `ZController` has to be
loaded onto the prefab at runtime (an animal's controller ships in the prefab), and the zombie skin
shader has to answer to `_EmissiveColor`, `_Fade`, `_Irradiated` (a vanilla animal on Standard
answers to none of them and works). Colour variants are each their own prefab
and `entity_class`, so `MatColor` is not used either. Everything the game needs is in the bundle
and the XML. If a DLL is wanted later - for the dire wolf's fur shader, a rage special, a custom
voice - `mods\ZTwinBrothers\src\` is the pattern, and `art\textures\werewolf_rmo.png` is already
packed for `_RMOL` (roughness R, metallic G, occlusion B).

## The pipeline, step by step

### 1. Rig: `tools\blender-prepare-werewolf.py`

Imports each source FBX (`--factory-startup`, always), renames the six bones, deletes the
Advanced-Skeleton control empties (`Group/MotionSystem/RootSystem/FK*`) and their duplicate takes
from the animation files, renames the takes (`Armature|Idle|BaseLayer` -> `Idle`), measures the
LOD0 mesh around every bone, and exports:

| file | content |
|---|---|
| `art\export\Werewolf.fbx` | armature + 4 LOD meshes, no animation, 2.4 MB |
| `art\export\anim\Werewolf_Anim.fbx` | 12 takes: Idle 4 s, Wait 8 s, Walk 1.33, Run 0.8, Agr 2.67, Attack_L 1, Attack_R 1, Jump 0.87, Fall 2, Landing 1.07, Hit 1.33, Death 6.67 |
| `art\export\anim\Werewolf_{Walk,Run}_4_Legs.fbx` | 1.0 s / 0.5 s loops |
| `art\export\anim\Werewolf_Eating{,_Out}.fbx` | 4.6 s loop / 0.67 s exit |
| `art\export\werewolf_bones.{json,txt}` | per bone: parent, length, median and p90 vertex distance from the axis, axial extent |

Why the rename happens in Blender and not Unity: a Generic animator binds clip curves by transform
*path*, so the model and every clip must agree on bone names; renaming the sources once is
deterministic and inspectable, rewriting curve paths in an `AssetPostprocessor` is not. Why the
object transforms are **not** applied: Blender puts the file's cm unit and Y-up on the armature
object (scale 0.01, rot 90 X) and leaves pose-bone location keyframes in file units; applying it
would rescale the rest bones but not the F-curves and a jump would lift the pelvis 70 m. Exporting
with the default axis/unit conversion undoes the import exactly - the Unity build reports the
hips at y 1.354 m, the head at 2.045 m and **no transform with a non-unit scale**.

**The `AnimRoot` empty in every animation FBX is load-bearing.** Unity's importer collapses a file
whose only root node is the armature: the `Armature` node disappears and the clip curve paths come
out as `root/Hips/...`, while the model FBX - whose `Armature` has mesh children and survives - is
`Armature/root/Hips/...`. A Generic animator binds by path, so the first build played every clip
as the bind pose (found by `WerewolfPreview`: 780 curve bindings per clip, 10 resolving). A second
empty root node in the animation files keeps Unity from collapsing the first; it imports as an
inert GameObject and all 970 bindings per clip now resolve on the prefab.

Checks: `blender-inspect-fbx.py <fbx> --keep-leaf` (objects, bones with world positions, takes),
`blender-clip-motion.py <fbx>` (root travel and pelvis height per take), and after a Unity build
`WerewolfPreview.Render` (renders eleven poses and logs, per clip, how many curve bindings resolve
on the prefab - that number must equal the total). The renders in `art\work\preview` were checked
by eye on 2026-09-06: hunched idle, four-legged walk and run, swipe, flinch, crouched eating, jump,
death lying flat; the fur albedo and the normal map read correctly.

### 2. Textures: `tools\prepare-textures.py`

4096 TGA -> 2048 PNG. Per variant `werewolf_albedo_*` (sRGB) and `werewolf_emission_*` (sRGB, the
eyes and glow; almost entirely black, compresses to nothing). Shared `werewolf_normal` (already
Unity/OpenGL convention - the package is a Unity package), `werewolf_specgloss` (RGB flat 51 =
specular 0.2, A = the seller's smoothness alpha, un-inverted because Standard also reads smoothness
in A; metallic in the source is all zero) and `werewolf_rmo` for the fur-shader route (roughness =
255 - smoothness, occlusion flat 255 - there is no AO map; 0 would kill ambient light). Measured
against `dire_wolf_RMO.tga` (R mean 123 / G 61 / B 238): ours R 237 / G 0 / B 255 - a very rough,
non-metallic, unoccluded surface, which is what a fur texture is. `--stats` re-prints the channels.

### 3. Unity: `unity\Assets\Editor\`

- `ModelSetup.cs` - copies the art in, configures textures (normal as NormalMap; specgloss linear
  with alpha), model (Generic, `optimizeGameObjects` **off** because the game finds bones by name,
  root motion node `Armature/root`), animations (Generic, `sourceAvatar` = the model's avatar,
  loop flags per clip, Hit additive), calls `WerewolfAnimator.Build`, builds a material and a
  prefab per shipped variant, deletes the assets of variants that were dropped, and prints a
  report (heights, non-unit scales, the tree).
- `WerewolfAnimator.cs` - the controller. Layer 0: `Move` (blend Idle / Walk_4_Legs / Run_4_Legs
  at 0 / 0.15 / 0.5), `IdleBreak` (Wait, when IdleTime > 10 and not moving), `AttackL`/`AttackR`
  (tag Attack; L when Forward < 0.15, R when moving - the only varying number the game hands the
  animator is `Random`, rolled per hit, so speed is what alternates them), `Death` (tag Death, held
  on the last frame, `TriggerAlive` back to Move), `Jump -> Fall -> (JumpLand) Landing -> Move`
  with a 4 s fall time-out, `Eat -> EatOut` on Begin/EndCorpseEat, `Swim` (Walk_4_Legs at 0.6, no
  swim clip exists). Layer 1 `Pain`: additive, `Empty` default, `Pain` (tag Hit) on PainTrigger,
  back to Empty at exit time 1. Unused clips: `Walk`, `Run` (the bipedal gait - swap into the
  blend tree if the hunched two-legged walk is wanted at low speed), `Agr` (a roar; a natural
  fit for a rage special if a DLL ever triggers one).
- `BundleBuilder.cs` - the shipped prefabs into `werewolf.unity3d`; everything else comes along as a
  dependency and is shared. Loads the bundle back and checks each `LoadAsset(name)`.
- `WerewolfPreview.cs` - pose renders plus the curve-binding check, see "Build".
- `WerewolfDebug.cs` - prints the imported hierarchy of every FBX and the path roots the clips
  animate (`-executeMethod WerewolfDebug.PrintImport`, works with `-nographics`). This is what
  showed the collapsed `Armature` node.

Colliders (`ModelSetup.BuildBodyColliders`): capsules on the twelve `physicsbodies.xml` bones,
pointing at the next bone down the chain, torso radii by hand (0.30 hips, 0.33 chest, 0.22 neck,
0.26 head sphere) because the spine bones' vertex groups are the strip of back fur and measure a
third of the real chest; limb radii = measured median x 0.75, clamped 0.10..0.30. Rigidbodies
(kinematic) on all twelve and `CharacterJoint`s to the nearest jointed ancestor, hinge-ish limits
on calves and forearms - so `HasRagdoll="true"` is available but the XML draft ships it off until
it has been seen in game. `Collider` (LargeEntityBlocker): three axis-aligned capsules - spine at
chest height, upright through the hips, head. `Physics`: r 0.45, h 2.2 (the bear's is r 0.44 h
1.82).

LODs: 22,724 / 15,148 / 10,080 / 6,726 tris at screen heights 0.40 / 0.20 / 0.09 / 0.025 (then
culled). All four renderers carry the `LOD` tag and the variant's material.

## What the XML has to say

Three files, all **untested in game**: `entityclasses.xml` (one class, `animalWerewolf`),
`physicsbodies.xml` (the `AWerewolf` collider set for this rig) and `Localization.csv` (the
display name, English and Russian).

The wiring that is settled, and why each value is what it is, is commented at the property in
`entityclasses.xml`. In short: `extends="animalTemplateHostile"`,
`AvatarController=AvatarAnimalController`,
`Prefab=#@modfolder(Werewolf):Resources/werewolf.unity3d?Werewolf`, `PrefabCombined=true`,
`PhysicsBody=AWerewolf`, `RootMotion=false`, `HasDeathAnim=true`, `HasRagdoll=false`, `SizeScale=1`.

**The stats are the dire wolf's on purpose** - health 3000, XP 2000, `MoveSpeedAggro 1, 1.1`, its
hand item for damage - so that a bad first result reads as "our wiring is wrong", not "our numbers
are odd". Two things are NOT the dire wolf's and must not be copied from it: `PhysicsBody` (its
`AWolf` paths resolve nothing on this rig) and `RootMotion` (our clips carry none).

**Speeds and the gait are one setting, not two.** Calibrated off the vanilla wolf: an
`entity_class` speed of 1.0 is about 3 m/s, the animator's `Forward` input is about m/s ÷ 10, and
the blend switches to the walk at `Forward` 0.1 and to the run at 0.3. So `MoveSpeed .3`
(inherited) walks and `MoveSpeedAggro 1` runs. Raising `MoveSpeedAggro` without raising
`RunThreshold` in `WerewolfAnimator.cs` (or the reverse) leaves the creature charging in a
half-blended walk.

First in-game checks, in this order, with the log open. Deploy with
`.\mods\Werewolf\build.ps1 -NoCode -Deploy`, then in the console `dm`, then:

1. `se 0 animalWerewolf` spawns without `Mandatory property` / `Could not load` /
   `Mod reference for a mod that is not loaded` errors.
2. No `PhysicsBodies animalWerewolf, AWerewolf, path not found` lines - each one is a wrong path
   in `physicsbodies.xml` and a body part that never registers a hit.
3. No `XML patch ... did not apply`: a bad xpath fails silently.
4. It stands in the hunched idle and walks/runs **on four legs** when approaching (`ai fp` freezes
   movement). Standing in the T-pose or sliding while the legs do not move means the clips did not
   bind - re-run `WerewolfPreview.Render` and check the curve-binding count.
5. `ai anim attack`: the swipe plays and lands damage. Being shot plays the flinch on top of the
   locomotion, not a folded-in-half pose; folding means the `Hit` clip lost its additive reference
   pose on import.
6. Death: it falls, holds the lying pose, and the corpse is harvestable. Then try
   `HasRagdoll="true"`.
7. Eyes glow in the dark (the emission map).
8. Its size next to the player and through a doorway - `SizeScale` and the `Physics` capsule
   (r 0.45) are the two knobs.

## Colours, and biome variants

The purchased asset has four albedo/emission pairs. **The seller's names do not describe what the
creature reads as in game** - the one called `Brown` looks grey under a night sky, which is what
sent the first build back. Measured mean albedo lightness, out of 255:

| seller's name | lightness | cast | reads as |
|---|---|---|---|
| `Dark` | 34.8 | neutral (R−B +2) | almost black - **this is what ships** |
| `Brown` | 39.4 | warm (R−B +13) | dark grey-brown |
| `Gray` | 44.0 | neutral (R−B −1) | grey, a shade lighter than brown |
| `Ice` | 63.4 | cool (R−B −19) | pale blue - the snow one |

All four are converted into `art\textures` and all four get a material in the Unity project, so
`WerewolfPreview.Render` writes a `colour_<Variant>.png` comparison shot of each. Only the ones in
`ModelSetup.Variants` get a prefab and reach the bundle.

**Changing the main colour is one line** - `ModelSetup.Variants` in
`unity\Assets\Editor\ModelSetup.cs` - and no XML. Whatever colour is first in that array is built
as the bundle asset named plain `Werewolf`, which is what `entityclasses.xml` already asks for.

**Adding a biome variant** is that line plus an `entity_class`:

1. Append the name to `ModelSetup.Variants`. It gets a prefab named `Werewolf<Variant>` in the
   bundle; mesh, skeleton, clips and controller are shared, so it costs about 2.8 MB of texture and
   nothing else. Dropping it back out deletes the stale prefab (`DropUnshippedVariants`).
2. An `entity_class` extending `animalWerewolf` with its own `Prefab=...?Werewolf<Variant>` and
   whatever stats differ.

Then put each class in the biome spawn groups (`entitygroups.xml`), which has no draft yet: `Ice`
in the snow biome, `Gray` or `Brown` in the forest, and so on. The colours are separate prefabs,
not a runtime tint - vanilla's `MatColor` mechanism needs TFP's shader and its `_EmissiveColor`
property, which Unity's Standard shader does not have.

## Sound

The entity currently speaks with the dire wolf's voice, named straight out of vanilla's
`sounds.xml` (`wolfdireroam`, `wolfdirealert`, `wolfdireattack`, `wolfdirepain`, `wolfdiredeath`,
`wolfdiresense`, `wolfdiregiveup`). That costs the mod nothing and is a fine placeholder.

For generating the mod's own voice, the dire wolf's 26 clips are extracted as reference:

```powershell
.\tools\rip-game-assets.ps1 -Rip -PrimaryOnly -Name animal-sounds -OutDir mods\Werewolf\art\reference -BundlePath "D:\SteamLibrary\steamapps\common\7 Days To Die\Data\Addressables\Standalone\automatic_assets_sounds\animals.bundle"
python mods\Werewolf\tools\extract-reference-sounds.py
```

The rip decodes to `.ogg` (the game's XML calls them `.wav`; same audio). The second command reads
the role → clip mapping out of the game's own `sounds.xml` rather than guessing from file names,
and sorts the clips into `art\work\sound\reference\<role>\`. What came out, all 44.1 kHz mono:

| role | clips | length |
|---|---|---|
| roam | 4 | 1.87 - 3.62 s |
| alert | 4 | 0.94 - 1.68 s |
| attack | 4 | 0.96 - 1.12 s |
| pain | 4 | 0.77 - 1.76 s |
| death | 4 | 2.74 - 3.36 s |
| sense | 4 | 1.00 - 2.41 s |
| giveup | 2 | 3.03 - 3.33 s |

**Nothing under `art\reference` or `art\work\sound\reference` ships** - it is TFP's audio, kept as
something for a generator to listen to. Generated clips go in `art\work\sound\<role>\`, which is
the layout `tools\build-asset-bundle.ps1` already knows how to pick up (it renames them per role
and imports them as mono, decompress-on-load). Shipping them then needs a `sounds.xml` in this mod
declaring one `SoundDataNode` per role pointing at the bundle, and the `Sound*` properties in
`entityclasses.xml` switched from `wolfdire*` to those node names - the sibling mod did exactly
this and its `pack\Config\sounds.xml` is the template.

## The zigzag bug, and why the movement capsule is not the model

Reported after the first in-game run: the creature would not charge in a straight line, it swerved
around obstacles that were not there. The cause was the capsule on the prefab's `Physics` node,
and it is worth writing down because nothing about it is guessable.

`Entity.AddCharacterController` reads the **CapsuleCollider on the `Physics` transform** - centre,
height and radius - and builds the entity's character controller out of it. `SizeScale` then
multiplies the whole entity. So the capsule is not decoration and not a hit box: it *is* the
controller, and `EntityMoveHelper` paths with its two numbers:

- `CheckWorldBlocked` raycasts a column `ccHeight` tall. Over 2 m and the creature needs **three**
  clear blocks of headroom, so ordinary terrain, tree canopy and every doorway block it.
- `CalcObstacleSideStep` arcs around anything inside `ccRadius`. Wider than a block and **one-block
  gaps read as walls**, so it side-steps constantly.

The first build had r .45 h 2.2, which `SizeScale` 1.2 turned into **1.08 m wide and 2.64 m tall** -
the widest and by far the tallest controller in the game. Hence the swerving. Effective sizes for
comparison, prefab radius times SizeScale:

| | r | h |
|---|---|---|
| Wolf | 0.22 | 0.92 |
| Dire Wolf | 0.34 | 1.57 |
| Bear | 0.44 | 1.82 |
| Werewolf, first build | **0.54** | **2.64** |
| Werewolf, now | 0.42 | 1.90 |

The lesson is vanilla's own: **size the controller to the gameplay envelope, not to the mesh.** A
bear's model is far bigger than its 1.82 m controller. Ours is a 2.7 m creature on a 1.9 m
controller, and nothing is lost by that - the visible bulk still blocks other entities through the
`LargeEntityBlocker` capsules, and shots still land on the per-bone physics body.

`ModelSetup.EntitySizeScale` has to stay equal to `SizeScale` in `entityclasses.xml`: the capsule
is authored pre-scale and divided by it, so the effective numbers above are what the constants in
`BuildPrefab` actually say. Change one, change both.

Two smaller things that also shape the approach, if it still looks wrong: `AIGroupCircle` is 1 from
`animalTemplateHostile`, which `EAIApproachAndAttackTarget` uses to make animals circle a target
rather than converge on it, and `AIPathCostScale` `.15, .2` is the dire wolf's. Both are inherited
on purpose; neither was the zigzag.

## The zigzag, solved: it was circling, and the animator was hiding it

The trace settled it. With the player standing still the log reads:

```
[ww 17964] chasing  Forward 0.00 Strafe 0.35 -> ~3.5 m/s  target lyovius at 2.4 m  clear
[ww 17964] chasing  Forward 0.05 Strafe -0.34 -> ~3.4 m/s  target lyovius at 2.5 m  clear
[ww 17964] chasing  Forward -0.05 Strafe 0.00 -> ~0.5 m/s  target lyovius at 2.2 m  clear
```

**`clear` every time, and `Strafe` bigger than `Forward`.** Nothing was blocking it and the path
was fine, two or three nodes in a straight line. The creature was moving sideways at 3.5 m/s while
holding station 2.3 m from the player. Not swerving around an obstacle: orbiting.

**Cause one, the behaviour: `AIGroupCircle`.** `animalTemplateHostile` sets it to 1, and
`EAIApproachAndAttackTarget` then relocates the animal to a random angle of **18 to 46 degrees**
around the target (`RandomFloat * 28 + 18`, side chosen at random) every 46 ticks, whenever
something else is attacking the same target - and the log shows a `zombieYo` next to it. That is
wolf-pack circling, and it is right for a wolf. On a single 2.7 m predator it reads as "will not
come at me". `pack\Config\entityclasses.xml` now sets `AIGroupCircle` to 0.

**Cause two, and this one was mine: the locomotion blend ignored `Strafe`.** It was a 1D blend on
`Forward` alone, so `Forward 0.00, Strafe 0.35` selected the **idle** clip - the creature skating
sideways at walking pace in a standing pose. That made an ordinary sideways step look like a
physics fault, and it would have done so after any obstacle side-step or path correction too, not
only while circling.

The blend is now **FreeformCartesian2D on `Forward` and `Strafe`**. There are no sideways clips to
blend, so the forward gait is placed at all eight compass points (diagonals normalised, so a
diagonal walk is walking pace and not 1.41x of it): moving in any direction at walking pace plays
the walk, at running pace the run, with the sped-up sprint copy still at `Forward` 0.7 only. Legs
cycling in the wrong direction beat a statue sliding across the ground. Vanilla's animal
controllers are 1D and have the same blind spot; ours no longer does.

**What the trace ruled out on the way**, all worth not re-testing: the path itself (2-6 nodes,
straight, `PathPoint` coordinates one block apart), obstacles (`clear` throughout), root motion
(the clips carry no root translation or rotation), and the six-second velocity lead in
`GetMoveToLocation` (the player was standing still, so the lead was near zero).

## The chase aims ahead of a moving target

Found while chasing the zigzag, and it explains a lot of it without any obstacle being involved.
`EAIApproachAndAttackTarget.GetMoveToLocation` returns

```
target.position + targetVelocity * 6
```

**a six-second lead.** The creature steers at where it predicts the target will be, not at the
target. Stand still and the aim is exact; move at all and the aim point swings metres to the side,
and the creature curves toward it. The faster the creature, the more ground that curve covers
before the next update straightens it out - which is why raising the speed made a mild vanilla
behaviour into an obvious weave.

Path recomputation is on a tick counter (`pathCounter`, 60 ticks for the way home, smaller values
in the chase branches), and between recomputes the aim point keeps moving, so the steering
corrects continuously.

`werewolf trace` prints the aim point, how far it sits from the target ("lead"), the move-to
distance, the failed-move count and the remaining path nodes, precisely so this can be told apart
from a genuine obstacle:

| what the trace shows | what it means |
|---|---|
| large `lead`, `clear` | the prediction, not an obstacle. Test by standing perfectly still |
| `blocked: ...` while weaving | a real obstacle; then the controller size or the terrain is at fault |
| node count churning every tick | it is re-planning constantly, a different fault with the same look |

The clean experiment: **stand completely still** and watch. If it comes straight at you when you
are still and weaves when you move, it is the lead and nothing else is wrong.

## What the model's shape does and does not affect

The creature attacks upright and runs on all fours, so its footprint changes shape between gaits.
That does not reach the movement code. The only geometry the game moves it with is the character
controller, which is a **circle** - radius and height, no orientation - built from the `Physics`
capsule. Nothing in `EntityMoveHelper` reads the mesh, the bounds or the animation pose.

Two related things that are worth knowing anyway:

- **The avatar is Generic, not Humanoid.** A four-legged rig has nothing to retarget, and the
  entity runs `AvatarAnimalController`, the animal path. The sibling zombie mod is the humanoid
  one; none of its retargeting applies here.
- **Vanilla offsets the model, we do not.** `animalWolf` puts its `Model` node at z -0.364 and the
  dire wolf at -0.335, pushing a long body back so it sits sensibly on a round controller. Our
  model is at zero, and its body already sits mostly behind the origin: in Unity space it spans
  z -1.44 at the tail to +0.99 at the nose. So the nose overhangs the controller by about half a
  metre. That is cosmetic - it shifts where the body looks relative to where the game thinks it
  is - and is the first thing to adjust if the creature looks like it bites from too far away.

## Open questions

- **Attack timing / damage.** The attack states exit at 0.75; whether the AI applies damage at the
  right moment of the swipe is only visible in game. Move the exit time in `WerewolfAnimator.cs`.
- **Gait speeds.** With no root motion the feet will slide at speeds between thresholds. If it
  bothers, the fix is per-state `speed` multipliers or extra blend children, not XML.
- **Ragdoll quality.** Procedural joints; look at it before shipping `HasRagdoll=true`.
- **Hit-collider coverage.** Twelve capsules; the belly and the tail are not covered by an
  `E_BP_*` collider. Add bones to `PhysicsBones` in `ModelSetup.cs` **and** to physicsbodies.xml,
  then re-run `check-physicsbody-paths.py`.
- **Collider sizes.** Resolved and shaped, but only ever seen as numbers. The first time the
  creature is shot in game is the first real look at whether the capsules sit where the fur is.
- **Sound.** Borrowed dire wolf voice. Own clips go through the same route the sibling mod used
  (`sounds.xml` + AudioClips listed in the bundle by name).

## Reference material (gitignored)

`art\reference\animals-project` and `animals-models` - the whole vanilla `animals.bundle` ripped
with `tools\rip-game-assets.ps1 -Rip -BundlePath ... -Name animals -OutDir mods\Werewolf\art\reference`
(1.9 GB). The useful parts: `ExportedProject\Assets\AssetBundles\Automatic\Entities\Animals\Wolf\
animalWolf.prefab`, `DireWolf\animalDireWolfPrefab.prefab`, `AnimatorController\animalWolfController.
controller`, `Shader\Game_Animal_Fur.shader` (properties), and the textures. Read them with
`tools\unity-yaml-tree.py <prefab> --full` and `tools\unity-controller-summary.py <controller>`.
Nothing from there ships.
