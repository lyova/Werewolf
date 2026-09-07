# Changelog

## Unreleased

- Rig prepared: six bones renamed to what `AvatarAnimalController` looks up, control-rig junk
  stripped, model and five animation FBXs re-exported from Blender, all 96 bones kept
  (`tools\blender-prepare-werewolf.py`).
- Textures converted to 2048 PNG packed for Unity's Standard (Specular) shader, plus an RMO for the
  fur-shader route (`tools\prepare-textures.py`).
- Unity project: headless build of the material, an animator controller authored from the seller's
  clips in the vanilla wolf controller's shape, and the entity prefab laid out like
  `animalWolf.prefab` (tags at the game's indices, colliders on the twelve physics bones, ragdoll
  joints, blocker and movement capsules, 4-level LODGroup), packed into `werewolf.unity3d`.
- Ships the **near-black** creature only (the asset calls it `Dark`). All four colours get a
  material and a comparison render; which one ships is one line, `ModelSetup.Variants`, and never
  touches the XML - whichever colour is first there is built as the bundle asset named `Werewolf`.
- Dire wolf voice extracted as reference for generating the mod's own, 26 clips sorted by role
  (`tools\extract-reference-sounds.py`, mapping read from the game's sounds.xml).
- `tools\check-physicsbody-paths.py` resolves the collider paths against the built prefab offline;
  `tools\dump-entity-stats.py` resolves entity stats through their extends chains for balancing.
- Config: `entityclasses.xml` (`animalWerewolf`, dire wolf stats deliberately),
  `physicsbodies.xml` (`AWerewolf` collider set for this rig), `Localization.csv`. Untested in game.
- Tooling for reading game assets: `unity-yaml-tree.py`, `unity-controller-summary.py`,
  `blender-inspect-fbx.py`, `blender-clip-motion.py`, `find-stfld.ps1`, `list-tag-strings.ps1`.
- `AIGroupCircle` inherited as 1 made the werewolf orbit its target at 18-46 degrees like a wolf
  pack. Turned off.
- **Moved onto root motion, the way every other entity in the game runs** (`RootMotion="true"`).
  The travel is baked back into the in-place locomotion clips
  (`tools\blender-bake-root-motion.py`; the originals stay in `art/export/anim-inplace`) at the
  speed the legs depict, measured off the planted feet: 6.10 m/s running and 1.75 m/s walking,
  which times `SizeScale` 1.2 is 7.3 and 2.1 m/s on the ground.

  A first attempt at this failed - the model ran about two metres and snapped back once per clip
  cycle - and the bundle was the reason, in two places, both fixed in `ModelSetup.cs`. The
  Animator had `applyRootMotion` off, and Unity only calls `OnAnimatorMove` while it is on, so
  nothing ever reached `EntityAlive.accumulatedRootMotion`. And the Generic rig had no root node:
  that setting is `m_RootMotionBoneName`, `ModelImporter` exposes no C# property for it (only
  `motionNodeName`, a different feature), so it had never been set - and with it empty Unity
  generates no root motion curves at all, leaving the baked travel in the pose to walk the model
  away from an entity that never moved. The build now writes it through the importer's
  `SerializedObject` and fails if an imported clip carries no root motion.

  Vanilla's clip settings came along with it (`KeepOriginalPositionY` 1, `PositionXZ` 0,
  `Orientation` 0, `m_ApplyRootMotion` 1), all read off `animalWolf.prefab` and
  `animal_direwolf_run.anim`.
- Locomotion blend back to vanilla's 1-D tree on `Forward` at 0 / 0.1 / 0.3. It had been made 2-D
  on Forward and Strafe so side-steps would animate; under root motion the clip *is* the movement,
  so a large Strafe would have fired the creature forwards at full run speed.
- Speeds retuned for that path: `MoveSpeed .4`, `MoveSpeedAggro 1.2, 1.7`. These are not metres
  per second and no longer set the speed at all - they place the creature on the blend, and the
  blend picks the clip. `Forward = 0.25 x` the number, so 1.2 lands exactly on the run.
- **Faster after dark.** The two numbers in `MoveSpeedAggro` are day and night
  (`EntityAlive.GetMoveSpeedAggro` takes the second under `World.IsDark()`), so the werewolf chases
  at 7.3 m/s in daylight, the pace its animation was drawn for, and 9.8 m/s once the sun is down.
- To make that reachable at all, the locomotion blend gained a **sprint node**: the run clip a
  second time at Forward 0.6 with `timeScale` 1.8. Past the run threshold the blend used to clamp
  to its last child, so a bigger `MoveSpeedAggro` did nothing whatsoever; the ceiling is now
  13.2 m/s. Speeding the clip up rather than sliding the creature along is what keeps the feet on
  the ground: root motion scales with the playback rate and the legs cycle faster by the same
  factor.
- `MaxTurnSpeed` 600 -> 800, sized for the night speed. One value serves both, and being more
  agile than needed by day costs nothing while the other way round it weaves after dark.
  `werewolf speed` now prints the turn rate that belongs with whatever speed you set.
- **Fights the undead as well as the living.** `EntityZombie` added to both `ApproachAndAttackTarget`
  and `SetNearestEntityAsTarget`, which is how the grizzly bear - the one vanilla animal that picks
  fights with zombies - has it. Zombie *animals* are deliberately left off: they are
  `EntityEnemyAnimal`, and adding that class would point the werewolf at dire wolves and at the
  ones it summons itself.
- **Triple damage to anything tagged `zombie`**, so 65 a swing becomes 195. A `passive_effect` on
  the hand item with an `EntityTagCompare` requirement on the target, the same shape vanilla uses
  for perk bonuses. Keyed on the tag rather than the class, so the zombie bear and zombie dog take
  it too if one of them starts the fight.
- **The howl.** The asset's unused `Agr` clip becomes a Howl state, and a werewolf in a fight rolls
  every 10 seconds for a 10% chance to summon another one. Two summons are allowed, counted across
  the whole pack - it makes no odds whether the original called them or a called one did - and the
  second locks the howl for 10 minutes before the allowance resets. So the worst case is three at
  once, then a quiet spell. The clip carries no root motion, so howling roots the creature for its
  2.67 seconds: the price of the second werewolf is the ground it would have closed on you.
- **Every biome, but only at night and only once the world has progressed.** Its own entity group
  and one spawn entry per biome, at roughly a zombie bear's rarity.

  Neither gate could live in `spawning.xml`. Its `time` attribute has only Day / Night / Any, and
  its night is 22:00-04:00 rather than the 21:00-05:00 wanted. And biome spawning has **no game
  stage concept at all** - `SpawnManagerBiomes.SpawnUpdate` reads exactly two things,
  `World.IsDaytime()` and the game difficulty. Vanilla gates its dangerous ambient spawns by PLACE
  instead: the demolisher needs a downtown POI and rolls 2%, the zombie bear needs the wasteland.
  Game stage exists only on the horde side, in `gamestages.xml`, where the demolisher first appears
  at stage 147 and the zombie bear at 50.

  So both are enforced in `WerewolfSpawnGate.cs`: 21:00-05:00, and world game stage 125 or higher -
  about day 50, just under the 147 that gates the demolisher - where the world's stage is the
  highest among the players online. Anything failing either is
  removed with `EnumRemoveEntityReason.Despawned` - no corpse, no loot, no 10000 XP - so penning
  one in until sunrise is not a way to farm it. `werewolf gate stage <n>` retunes it live.
- **Its own voice**, 28 generated clips in six roles riding in the asset bundle, with the dire
  wolf's `AudioSource` prefabs, `Noise` values, priorities and pitch spread - those are what the
  game's stealth and heat maps are already balanced around, so only the audio changes.
  - Roam and sense share one set of clips (only one was made) but keep separate nodes: roaming is
    quiet, on the large-creature source at priority 6, and sensing is loud on the alert source with
    ten times the noise. Collapsing them would make an idling werewolf audible across the map.
  - The howl is played from the DLL by `WerewolfHowl.Summon`, because no entity property points at
    a summon. It is the loudest thing the creature does and has the longest heat map trail: a howl
    brings a second werewolf and stirs up whatever else was listening. `PlayOneShot` goes through
    the game's network path, so unlike the animation every client hears it.
  - `SoundGiveUp` stays the dire wolf's - no clip of our own was made, and leaving it pointed at a
    real node beats having nothing to play.
- Two build bugs found while wiring the audio in, both in the shared
  `tools\build-asset-bundle.ps1`. The clip copy sat inside the branch that also copies model art,
  which Werewolf skips because its `ModelSetup` imports its own - so its sounds were silently never
  copied. And the source folder fell back to `art\work\sound`, which in this mod holds nothing but
  reference audio ripped out of the game. `art\sound` now wins, and a role folder named
  `reference` is refused outright so TFP's audio cannot end up in a bundle.
- `check-config-patches.py` now also cross-checks `sounds.xml` against `art\sound`. A clip name
  with no file plays silence after one warning; a file with no line is never heard at all. Both
  look identical to bad luck in play.
- Fixed from the first play test:
  - **The summon never fired.** `no entity class named animalWerewolf` in the log, from a lookup
    that scanned `entityClassName`, while the creature it was looking for stood there killing
    zombies. It now asks `EntityClass.FromString`, which is the `GetHashCode` that `EntityClass.Add`
    keys the table with in the first place; the scan is a fallback, and if both miss, the log names
    what the table actually held. Because `Summon` was returning false the pack counter never rose,
    so the limit and the 10-minute lock had never been exercised either.
  - **The biome spawner put five werewolves round one player in a night.** Two causes, two fixes.
    The group held nothing but the werewolf, so every spawn attempt was one - it now carries a
    `none` at 95, about the share the zombie bear has of the group it rides in. And `maxcount` in
    spawning.xml is per 80x80 m spawn area, several of which are live around a standing player, so
    `WerewolfSpawnGate.AmbientLimit` now holds the biome spawner to **one** werewolf alive
    world-wide and culls the furthest from any player. Summoned ones (`Dynamic`) and console-spawned
    ones (`Unknown`) are exempt, so the pack is 1 wandering in plus the 2 it can call.
  - Summons now land 10 blocks out and **behind the player**: twelve directions are tried round the
    howler and the first more than 60 degrees off the way they are facing wins. A werewolf appearing
    from nothing in front of you reads as a bug; the same one arriving from behind reads as the howl
    having worked.
  - The turn-rate hint printed by `werewolf speed` divided metres by metres and called the result
    degrees, advising 14 deg/s for a creature that needs 800. It converts from radians now.
- **The mod no longer patches the game at all.** `PathSmoothing` is gone - a Harmony prefix on
  `EntityMoveHelper.SetMoveTo` written while the chase was still weaving. It filtered to werewolves
  and was off by default, but it ran on every `SetMoveTo` call every entity in the world makes, and
  the weave turned out to be root motion anyway. Nothing in `src\` touches an entity that is not a
  werewolf now, and the only shared file the config appends to is a new entity group and one spawn
  entry per biome. Both are additive: `ChunkAreaBiomeSpawnData` keys its per-entry budgets in a
  dictionary rather than an array, so an appended `<spawn>` gets its own counters and cannot shift
  or consume a vanilla entry's, on a new save or an existing one.
- **Dawn now looks like dawn.** A werewolf blinking out of existence in front of a player reads as
  a bug, so at 05:00 the exit depends on who is watching: within 30 m of a player it DIES, with the
  death animation, the death sound and a body; further away it is despawned between frames as
  before.
  - Neither pays out, and that takes two separate mechanisms. `lootDropProb` is zeroed **on the
    entity**, which is the number `EntityAlive.dropItemOnDeath` rolls against for the bag - the
    entity class is untouched, so a werewolf the player actually kills still drops one. And the
    killing damage is `EnumDamageSource.Internal` with no attacker, so `entityThatKilledMe` stays
    null and `EntityAlive.AwardKill` returns on its first line: no kill credit, no score, no 10000
    XP.
  - It does leave a corpse, which can still be butchered. `werewolf gate death <m>` moves the
    radius; 0 makes every werewolf vanish silently again.
- `SightRange` 40 -> 25. Worth knowing what that actually changed: `EAISetNearestEntityAsTarget`
  takes `min(SightRange, the per-class cap in AITarget)`, and the numbers on each class there are
  *hearing, sight* rather than two sight figures. The player's own cap was already 20 and zombies
  are pinned at 8, so the distance at which it turns on a player did not move - what came down from
  40 to 25 is its general awareness and its hunting of wildlife.
- **Fixed the washed-out look: the Unity project was in Gamma colour space and the game is in
  Linear.** The near-black `Dark` variant shipped looking like light tan clay. Everything else
  checked out - the bundle carried `werewolf_albedo_dark` at 2048 with the sRGB flag set, the
  material pointed at it, and the model rendered correctly in the editor - because the mismatch is
  not in the assets. The shader compiled into the bundle does gamma maths, and the game's pipeline
  is already doing them, so the result comes out pale.

  `PlayerSettings.m_ActiveColorSpace` is 1 in the game's own `data.unity3d` and was 0 here.
  `ModelSetup.RequireLinearColorSpace` now refuses to build in anything but Linear and says what to
  change, because this is invisible until the bundle is in the game and everything upstream of it
  looks right.
- **The howl summoned nothing, and the reason had been there since it was written: an entity class
  id IS the class name's `GetHashCode`, so roughly half of all valid ids are NEGATIVE**, and the
  lookup used -1 to mean "not found". `animalWerewolf` hashes negative, so every summon was thrown
  away as a failed lookup and logged as "no entity class named animalWerewolf" while the class sat
  right there in the table. `WerewolfWorld.TryClassId` returns a bool now; no sentinel can collide
  with a real id.
- The `Dark` pelt is tinted to 0.35 of the seller's albedo (`ModelSetup.DarkTint`; `tint_*.png`
  under `art/work/preview` shows the alternatives, 1.0 restores the seller's own colour). It was already the darkest of the four -
  mean 36/255 against gray's 44, and darker than the vanilla dire wolf's 52/35/32 - but the pelt is
  dark GREY over pale skin, and a 0.14 albedo in daylight sits around mid grey the way asphalt
  does. This is a deliberate departure from the asset rather than a fix: `ModelSetup.Tint` is the
  one number, and 1.0 restores the seller's own colour.
- Two things about the preview renders, because they cost most of an evening between them.

  **The preview lighting was lying.** 0.45 flat ambient plus 1.7 of directional light between two
  lamps - a photographic studio. The ambient term alone lifts a near-black pelt to mid grey, so
  every judgement about how dark this creature is, made off those frames, was made under lighting
  that made the question meaningless. Now 0.08 ambient and one sun; the same model measures 38.7
  mean instead of 54.2.

  **And a flat-colour albedo settles in one restart what screenshots cannot.**
  `ModelSetup.DebugFlatAlbedo` replaces the albedo with a saturated flat colour, saved as a real
  texture asset so it travels the same code path. Swapping the whole variant to Ice had changed the
  eyes and left the body arguable, because ice fur is still fur; flat red is not confusable with
  lighting, weather or a dark pelt. It came out red, which proved `_MainTex` arrives intact and
  moved the question from "is the texture reaching the game" to "how dark is the artwork" - which
  is a tint, not a bug.
- Experience 7000 -> 10000.
- `tools\check-config-patches.py` resolves every xpath in `pack\Config` against the game's own
  config offline. A modlet patch that misses is silent in play, so a spawn group or a damage bonus
  can go missing without anything looking broken.
- `Werewolf.dll`: a `werewolf` console command for live tuning - speed in m/s or raw units, turn
  speed, and a trace of what the movement code is doing. The creature does not depend on it; it is
  a tuning tool, and it is why ModInfo now sets SkipWithAntiCheat.
- Movement capsule resized from an effective r .54 h 2.64 to r .42 h 1.90. The old one was built
  from the model and made the creature swerve around obstacles that were not there.
- Mod folder scaffolded, purchased asset inspected, implementation brief written.
