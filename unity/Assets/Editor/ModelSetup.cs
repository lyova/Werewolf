// Builds everything the Werewolf bundle ships, headlessly: imports the prepared FBXs and textures
// from ..\art, configures the importers, builds the animator controller (WerewolfAnimator.cs), a
// material per colour the asset has, and an entity prefab per SHIPPED colour (ModelSetup.Variants),
// all under Assets\Werewolf. Only the shipped prefabs and what they reference reach the bundle.
//
//   .\tools\build-asset-bundle.ps1 -Mod Werewolf -Prefab       (runs ModelSetup.All)
//
// Everything under Assets\Werewolf is generated; only this folder (Assets\Editor) and
// ProjectSettings are kept in git.
//
// The prefab layout copies the vanilla animal entity prefab (animalWolf.prefab, ripped with
// tools\rip-game-assets.ps1) node for node, because the game walks it by name and tag:
//
//   Werewolf                      the entity root; EntityFactory instantiates this
//     GameObject   tag E_Enemy    Entity.ModelTransform; EModelBase attaches here and takes its
//                                 FIRST child as the model (PrefabCombined=true)
//       Model      [Animator]     AvatarAnimalController.SetAnimator(GetModelTransform()) - the
//                                 Animator has to be on this exact transform
//         LOD0..3  tag LOD        the four skinned meshes; createModel / SetFade look for the tag
//         Armature/root  tag E_BP_BipedRoot   EModelBase.bipedRootTransform; physicsbodies.xml
//                                 paths are Transform.Find()ed from here ("Hips/LeftUpLeg/...")
//           Hips ... Head tag E_BP_Head        the six names AvatarAnimalController needs, and
//                                 capsule colliders + rigidbodies on the twelve physics bones
//         (no LargeEntityBlocker - see BuildBlocker below for why it was removed)
//     Physics      tag Physics, layer 15 (CC Physics)   the movement capsule
//
// Tags are stored in a bundle as indices, so ProjectSettings\TagManager.asset carries the game's
// exact tag table (read out of data.unity3d with UnityPy) - LOD is index 54, E_Enemy 15 and so on.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class ModelSetup
{
    public const string Mod = "Werewolf";
    public const string Dir = "Assets/Werewolf";
    public const string ModelPath = Dir + "/Werewolf.fbx";
    public const string AnimDir = Dir + "/Anim";
    public const string TexDir = Dir + "/Textures";
    public const string MatDir = Dir + "/Materials";
    public const string PrefabDir = Dir + "/Prefabs";
    public const string AudioDir = Dir + "/Audio";
    public const string ControllerPath = Dir + "/WerewolfController.controller";
    public const string BonesTable = Dir + "/werewolf_bones.txt";

    /// <summary>
    /// Colour variants that get a prefab and a slot in the bundle. THE FIRST ONE IS THE MAIN
    /// CREATURE and is the one entityclasses.xml points at.
    ///
    /// Changing the main colour is this line and nothing else - the first entry always ships as
    /// the bundle asset named "Werewolf", so no XML has to change with it. Adding a second entry
    /// gives a prefab named "Werewolf&lt;Variant&gt;" that an extra entity_class can point at, for
    /// biome variants; each of those costs one albedo and one emission texture in the bundle
    /// (about 2.8 MB) and nothing else, because mesh, skeleton, clips and controller are shared.
    ///
    /// The measured colours, so the seller's names do not have to be guessed at (mean albedo
    /// lightness out of 255, from tools\prepare-textures.py):
    ///   Dark   34.8  near-neutral, the darkest of the four - almost black in game
    ///   Brown  39.4  warm, but dark enough to read as grey-brown under a night sky
    ///   Gray   44.0  neutral, a shade lighter than brown
    ///   Ice    63.4  much lighter and clearly blue - the snow one
    /// </summary>
    // Three colours ship, one per biome family, wired up in pack/Config/spawning.xml:
    //
    //   Dark    everywhere else - forest, burnt forest, wasteland
    //   Ice     the snow biome
    //   Brown   the desert
    //
    // THE FIRST ONE IS STILL THE MAIN CREATURE. It is built as the bundle asset named plain
    // "Werewolf", which is what animalWerewolf points at; the others become "WerewolfIce" and
    // "WerewolfBrown" for the entity classes of the same name. Reordering this list therefore
    // changes which colour animalWerewolf is, so do not reorder it casually.
    //
    // Each extra colour costs one albedo and one emission texture in the bundle, about 2.8 MB,
    // and nothing else: mesh, skeleton, clips, controller, normal and specular maps are shared.
    public static readonly string[] Variants = { "Dark", "Ice", "Brown" };

    /// <summary>
    /// TEMPORARY DIAGNOSTIC. When set, the shipped material's albedo is replaced by a flat colour
    /// of this name instead of the creature's texture. Set to null to ship normally.
    ///
    /// Why it exists: swapping the whole variant to Ice changed the EYES in game and nothing else.
    /// The eyes are the emission map and the body is the albedo, and both live on the same
    /// material in the same bundle, so "emission arrives, albedo does not" is a much sharper
    /// statement than "it still looks grey" - and it is worth proving rather than arguing about,
    /// because grey lit by daylight looks like a lot of things.
    ///
    /// Flat saturated red cannot be confused with lighting, wear, weather or a dark pelt. If the
    /// creature comes out red, _MainTex is being sampled and the question was only ever how dark
    /// the artwork is. If it comes out grey, _MainTex is not reaching the shader at all and the
    /// search moves to the material and the shader.
    /// </summary>
    /// <remarks>Answered: the creature came out red, so _MainTex is sampled and the albedo
    /// travels into the game intact. Keep this null; it is here for the next time something
    /// visual is in doubt, and it is cheaper than arguing about a screenshot.</remarks>
    public const string DebugFlatAlbedo = null;

    /// <summary>
    /// What the purchased asset has textures for. All of these get a material and a comparison
    /// render (WerewolfPreview writes one idle shot per variant); only the ones in Variants get a
    /// prefab and reach the bundle.
    /// </summary>
    public static readonly string[] AvailableVariants = { "Brown", "Dark", "Gray", "Ice" };

    /// <summary>
    /// The prefab and bundle asset name - what entityclasses.xml asks for after the '?' in the
    /// Prefab property. The main variant, whichever colour it happens to be, is plain "Werewolf",
    /// so the XML survives a change of colour untouched.
    /// </summary>
    public static string PrefabName(string variant) =>
        variant == Variants[0] ? "Werewolf" : "Werewolf" + variant;

    public static string PrefabPath(string variant) => PrefabDir + "/" + PrefabName(variant) + ".prefab";

    /// <summary>
    /// Materials are named after the COLOUR, not after the prefab, so that changing which colour
    /// ships does not rename files. Only the prefab carries the "this is the main one" name.
    /// </summary>
    public static string MaterialPath(string variant) => MatDir + "/Werewolf_" + variant + ".mat";

    /// <summary>
    /// A multiplier on the albedo, per colour. White leaves the seller's texture alone.
    ///
    /// Dark needs one. It IS the darkest of the four - measured mean albedo 36/255 against gray's
    /// 44 and brown's 39, and darker than the vanilla dire wolf's 52/35/32 - but "darkest of these
    /// four" is not the same as "black". The pelt is dark grey and the SKIN under it (chest, inner
    /// limbs, muzzle) is much paler, which is what reads as grey at a distance however dark the fur
    /// is. Lit by daylight a 0.14 albedo sits around mid grey, the same way asphalt does.
    ///
    /// So this is a deliberate departure from the asset, not a correction of one. 0.5 takes it to
    /// roughly a 0.07 albedo, which reads near-black in daylight while leaving enough range for the
    /// fur relief to still be visible - a flat black creature loses all the sculpting. Raise it
    /// toward 1 for the seller's own colour, lower it toward 0.3 for a silhouette.
    /// </summary>
    static Color Tint(string variant) =>
        !string.IsNullOrEmpty(DebugFlatAlbedo) ? Color.white
        : variant == "Dark" ? DarkColour
        : Color.white;

    /// <summary>The specular map's tint, which is a separate decision from the albedo's - see
    /// DarkColour. Every other variant keeps the shared, untinted map.</summary>
    static Color SpecTint(string variant) => variant == "Dark" ? DarkSpecColour : Color.white;

    /// <summary>
    /// How far the Dark albedo is pulled down. The seller's texture averages 36/255 - genuinely
    /// the darkest of the four, and darker than the vanilla dire wolf - but it is dark GREY fur
    /// over pale skin, and daylight lifts a 0.14 albedo to about the tone of asphalt.
    ///
    /// 1.0 means the seller's artwork, untouched, and that is where this belongs unless somebody
    /// deliberately wants a departure from it.
    ///
    /// It spent a while at 0.5 and then 0.22 on a wrong diagnosis. The creature was shipping too
    /// pale, and darkening it here looked like the fix; the actual cause was that the seller's
    /// _Albedo_Brightness had been dropped when their shadergraph was replaced by Unity Standard.
    /// tools/prepare-textures.py bakes that in now. Two lessons kept here rather than in a commit
    /// message: a knob added to compensate for a bug hides the bug, and the asset's own material
    /// is part of the artwork, not just a preview of it.
    ///
    /// Note this can only DARKEN - _Color multiplies - so it is not the place to brighten anything.
    /// </summary>
    public const float DarkTint = 1f;

    /// <summary>
    /// The Dark variant's albedo multiplier, and the one place its colour is decided.
    ///
    /// The seller's Dark texture is dark GREY fur over paler skin. Grey is exactly the colour
    /// daylight is made of, so under a midday sun the creature came out reading as a big grey dog:
    /// there was nothing in the pelt for the light to tint. A darker grey does not fix that, it
    /// only makes a darker grey dog - which is what the DarkTint experiments above were.
    ///
    /// A COLOURED multiplier does fix it, because it takes the light out of two channels and leaves
    /// it in one. Green and blue are cut hard and red is left much higher, so every lit surface
    /// comes back oxblood instead of grey, and every unlit one goes to near black. The relief of
    /// the fur survives because the red channel still has range to work in - a flat black creature
    /// loses all its sculpting, which is the trap at the other end.
    ///
    /// TWO TINTS, not one: the albedo and the specular map are tinted separately, because they do
    /// different jobs on this creature. TintedSpecGloss has the measurements; short version, the
    /// specular map is neutral and about twice the albedo, so it is the specular that decides how
    /// bright the creature reads and the albedo that decides what colour it is.
    ///
    /// Both are NEUTRAL GREY here, and equal: this is a plain four-tones-darker version of the
    /// seller's artwork, no hue shift at all.
    ///
    /// A red-black version was tried first and rejected in game. It multiplied both maps by
    /// (0.36, 0.07, 0.06); Unity converts a material colour to linear, so those channels arrive as
    /// (0.107, 0.006, 0.004), green and blue are gone entirely, and every lit pixel comes back red.
    /// It read as a flat dark red animal rather than a black one with red in it.
    ///
    /// 0.65 is roughly four tones down. The number behaves as a plain perceptual multiplier despite
    /// the gamma conversion: the shader multiplies linear light by c^2.2 and the frame is encoded
    /// back at 1/2.2, so what lands on screen is about 0.65 of the original brightness.
    ///
    /// Tune it by looking, not by arguing: the Werewolf > Dark candidates scene menu item builds
    /// Assets/Werewolf/Scenes/DarkCandidates.unity, with candidates side by side under a sun that
    /// can be dragged to any time of day.
    /// </summary>
    public static readonly Color DarkColour = new Color(0.65f, 0.65f, 0.65f);

    /// <summary>The specular map's tint - a separate decision from the albedo's, see DarkColour.
    /// It is the specular that carries the grey, so a darkening that skipped it would not darken
    /// the creature much at all.</summary>
    public static readonly Color DarkSpecColour = new Color(0.65f, 0.65f, 0.65f);

    /// <summary>
    /// A material for one of WerewolfCandidates' colours, built exactly the way the shipping
    /// material is - same shader, same maps, the tint on both the albedo and the specular map - so
    /// what the candidate scene shows is what shipping that colour would look like.
    /// </summary>
    public static Material CandidateMaterial(string label, Color tint, Color specTint)
    {
        var shader = Shader.Find("Standard (Specular setup)");
        if (shader == null) throw new Exception("no 'Standard (Specular setup)' shader in this editor");
        Directory.CreateDirectory(MatDir);

        var path = MatDir + "/Werewolf_DarkCandidate_" + label + ".mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            material = new Material(shader);
            AssetDatabase.CreateAsset(material, path);
        }
        material.shader = shader;
        material.SetFloat("_Mode", 0f);
        material.SetColor("_Color", tint);
        material.SetTexture("_MainTex", Load<Texture2D>(TexDir + "/werewolf_albedo_dark.png"));
        material.SetTexture("_BumpMap", Load<Texture2D>(TexDir + "/werewolf_normal.png"));
        material.SetFloat("_BumpScale", 1f);
        material.EnableKeyword("_NORMALMAP");
        material.SetTexture("_SpecGlossMap", TintedSpecGloss(specTint));
        material.SetFloat("_SmoothnessTextureChannel", 0f);
        material.SetFloat("_GlossMapScale", 1f);
        material.SetFloat("_Glossiness", 0.5f);
        material.SetFloat("_SpecularHighlights", 1f);
        material.SetFloat("_GlossyReflections", 1f);
        material.EnableKeyword("_SPECGLOSSMAP");
        material.SetTexture("_EmissionMap", Load<Texture2D>(TexDir + "/werewolf_emission_dark.png"));
        material.SetColor("_EmissionColor", Color.white);
        material.EnableKeyword("_EMISSION");
        material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
        EditorUtility.SetDirty(material);
        return material;
    }

    /// <summary>
    /// A copy of the shared specular/gloss map with its SPECULAR COLOUR multiplied by a tint, and
    /// the reason the Dark variant can be any colour at all.
    ///
    /// WHY THIS EXISTS. _Color multiplies the albedo and nothing else, and on this creature the
    /// albedo is not what you are looking at. Measured off the seller's maps: the dark albedo
    /// averages 36/255 while the specular map averages 51/255 with a smoothness of 17/255. So the
    /// reflected term is about twice the diffuse one, and at that roughness it is spread over the
    /// whole pelt like a second diffuse - a broad NEUTRAL GREY sheen. That sheen is what daylight
    /// lights up, and it is why the creature reads as a grey dog at noon however far the albedo is
    /// pushed down. A ladder of five _Color tints rendered side by side was indistinguishable,
    /// which is the measurement that settled it.
    ///
    /// _SpecColor cannot help: with the _SPECGLOSSMAP keyword on, the Standard specular shader
    /// takes the specular colour from the map and ignores that property entirely. The map itself
    /// has to be tinted, so it is copied per colour.
    ///
    /// The map is LINEAR data, not a picture (sRGB is off in its importer), so the tint is applied
    /// in linear too - Color.linear - to match what Unity does to _Color on the albedo side. The
    /// alpha channel is smoothness and is copied untouched.
    ///
    /// One file per colour, cached by hex, so a rebuild does not redo the work.
    /// </summary>
    static Texture2D TintedSpecGloss(Color tint)
    {
        var source = TexDir + "/werewolf_specgloss.png";
        if (tint == Color.white) return Load<Texture2D>(source);

        var hex = ColorUtility.ToHtmlStringRGB(tint);
        var path = TexDir + "/werewolf_specgloss_" + hex + ".png";
        if (!File.Exists(path))
        {
            // Read through ImageConversion rather than the imported asset: an imported texture is
            // not readable unless its importer says so, and the source is compressed by then.
            var src = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!src.LoadImage(File.ReadAllBytes(source))) throw new Exception("cannot read " + source);

            var linear = tint.linear;
            var px = src.GetPixels32();
            for (var i = 0; i < px.Length; i++)
            {
                px[i].r = (byte)Mathf.Clamp(px[i].r * linear.r, 0f, 255f);
                px[i].g = (byte)Mathf.Clamp(px[i].g * linear.g, 0f, 255f);
                px[i].b = (byte)Mathf.Clamp(px[i].b * linear.b, 0f, 255f);
                // alpha (smoothness) untouched
            }
            src.SetPixels32(px);
            src.Apply();
            File.WriteAllBytes(path, src.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(src);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            ConfigureTexture(path, TextureImporterType.Default, sRGB: false, alpha: true, max: 2048);
            Debug.Log("[Werewolf] wrote a specular map tinted " + hex + " -> " + path);
        }
        return Load<Texture2D>(path);
    }

    /// <summary>A flat colour saved as a real texture asset, so it travels into the bundle exactly
    /// the way the real albedo does - same importer, same compression, same code path. A colour set
    /// through _Color instead would prove nothing, because that is a different property.</summary>
    static Texture2D FlatTexture(string name)
    {
        var path = TexDir + "/werewolf_albedo_debug_" + name + ".png";
        if (!File.Exists(path))
        {
            var colour = name == "red" ? new Color32(220, 20, 20, 255)
                       : name == "green" ? new Color32(20, 200, 20, 255)
                       : new Color32(20, 20, 220, 255);
            var tex = new Texture2D(64, 64, TextureFormat.RGBA32, false);
            var px = new Color32[64 * 64];
            for (var i = 0; i < px.Length; i++) px[i] = colour;
            tex.SetPixels32(px);
            tex.Apply();
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            ConfigureTexture(path, TextureImporterType.Default, sRGB: true, alpha: false, max: 64);
        }
        Debug.LogWarning("[Werewolf] DIAGNOSTIC BUILD: albedo replaced by flat " + name
                         + " - set ModelSetup.DebugFlatAlbedo to null to ship the real texture");
        return Load<Texture2D>(path);
    }

    // The twelve bones the game's physics body binds colliders to, with the E_BP_* tag
    // physicsbodies.xml gives each one. Order matters for nothing; the paths in the XML do.
    // (bone, tag, bone the capsule points at, radius in m or 0 to take the measured median)
    // The torso radii are set by hand: the spine bones' vertex groups are the strip of back fur
    // along the spine, so their measured radius (0.15) is a third of the real chest, and a shot
    // through the belly would miss every collider. The limb medians are trusted but scaled down a
    // quarter, because fur and the bone sitting off the limb's centre both inflate them.
    static readonly (string bone, string tag, string toward, float radius)[] PhysicsBones =
    {
        ("Hips", "E_BP_Body", "spine_02", 0.30f),
        ("spine_03", "E_BP_Body", "spine_05", 0.33f),
        ("neck_01", "E_BP_Body", "Head", 0.22f),
        ("Head", "E_BP_Head", null, 0.26f),
        ("LeftUpLeg", "E_BP_LLeg", "calf_l", 0f),
        ("calf_l", "E_BP_LLowerLeg", "foot_l", 0f),
        ("RightUpLeg", "E_BP_RLeg", "calf_r", 0f),
        ("calf_r", "E_BP_RLowerLeg", "foot_r", 0f),
        ("LeftArm", "E_BP_LArm", "lowerarm_l", 0f),
        ("lowerarm_l", "E_BP_LLowerArm", "hand_l", 0f),
        ("RightArm", "E_BP_RArm", "lowerarm_r", 0f),
        ("lowerarm_r", "E_BP_RLowerArm", "hand_r", 0f),
    };

    const float MeasuredRadiusScale = 0.75f;

    /// <summary>
    /// Must equal SizeScale in entityclasses.xml. The game multiplies the whole entity by it,
    /// including the movement capsule, so the capsule below is authored pre-scale and divided by
    /// this to land on the effective size that was actually chosen. Change one, change both.
    /// </summary>
    const float EntitySizeScale = 1.2f;

    // Screen-height fractions below which the next LOD takes over; the last one culls.
    static readonly float[] LodHeights = { 0.40f, 0.20f, 0.09f, 0.025f };

    static string ArtDir => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "art"));

    [MenuItem("Werewolf/Build Prefabs")]
    public static void BuildFromMenu() => Run();

    /// <summary>Batch entry point: tools\build-asset-bundle.ps1 -Prefab calls this.</summary>
    public static void All()
    {
        var exitCode = 0;
        try
        {
            Run();
        }
        catch (Exception e)
        {
            Debug.LogError("[Werewolf] prefab build failed: " + e);
            exitCode = 1;
        }

        if (Application.isBatchMode) EditorApplication.Exit(exitCode);
    }

    static void Run()
    {
        CopyArt();
        ConfigureTextures();
        RequireLinearColorSpace();

        var avatar = ConfigureModel();
        var clips = ConfigureAnimations(avatar);
        ConfigureAudio();
        var controller = WerewolfAnimator.Build(ControllerPath, clips);
        var materials = BuildMaterials();
        foreach (var variant in Variants) BuildPrefab(variant, avatar, controller, materials[variant]);
        DropUnshippedVariants();
        AssetDatabase.SaveAssets();
        Debug.Log("[Werewolf] done: " + Variants.Length + " prefab(s) in " + PrefabDir
                  + " (" + string.Join(", ", Variants) + ")");
    }

    /// <summary>
    /// Deletes prefabs left over from a previous build - a variant that is no longer shipped, and
    /// the stale "Werewolf&lt;Variant&gt;" name of whichever variant is now the main one. Neither
    /// could reach the bundle (BundleBuilder lists its assets explicitly), but a project full of
    /// prefabs that no longer match the current model is a good way to ship the wrong one.
    /// Materials and textures are kept: they cost nothing in the bundle and the comparison
    /// renders need them.
    /// </summary>
    static void DropUnshippedVariants()
    {
        var keep = new HashSet<string>(Variants.Select(PrefabName));
        foreach (var stale in Directory.GetFiles(PrefabDir, "*.prefab"))
        {
            var path = stale.Replace('\\', '/');
            if (keep.Contains(Path.GetFileNameWithoutExtension(path))) continue;
            AssetDatabase.DeleteAsset(path);
            Debug.Log("[Werewolf] removed stale prefab " + path);
        }
    }

    // ---------------------------------------------------------------- copy in

    /// <summary>
    /// Pulls the prepared art in from ..\art (Blender output in export\, PIL output in textures\).
    /// Copied rather than referenced because Unity only imports from under Assets.
    /// </summary>
    static void CopyArt()
    {
        var export = Path.Combine(ArtDir, "export");
        var textures = Path.Combine(ArtDir, "textures");
        Require(Path.Combine(export, "Werewolf.fbx"), "run tools\\blender-prepare-werewolf.py");
        Require(Path.Combine(textures, "werewolf_normal.png"), "run tools\\prepare-textures.py");

        Directory.CreateDirectory(Dir);
        Directory.CreateDirectory(AnimDir);
        Directory.CreateDirectory(TexDir);

        Copy(Path.Combine(export, "Werewolf.fbx"), ModelPath);
        Copy(Path.Combine(export, "werewolf_bones.txt"), BonesTable);
        foreach (var fbx in Directory.GetFiles(Path.Combine(export, "anim"), "*.fbx"))
        {
            Copy(fbx, AnimDir + "/" + Path.GetFileName(fbx));
        }

        // Only what the Standard material uses. werewolf_rmo.png stays in art\ for the fur-shader
        // route described in NOTES.md.
        //
        // Every AVAILABLE variant's textures come in, not just the shipped ones, so that the
        // comparison renders can show all four and so that changing which colour ships is a
        // one-line edit with no re-import. They cost nothing in the bundle: BuildPipeline only
        // pulls in what the listed prefabs actually reference.
        var wanted = new List<string> { "werewolf_normal.png", "werewolf_specgloss.png" };
        foreach (var v in AvailableVariants)
        {
            wanted.Add("werewolf_albedo_" + v.ToLower() + ".png");
            wanted.Add("werewolf_emission_" + v.ToLower() + ".png");
        }
        foreach (var name in wanted)
        {
            Require(Path.Combine(textures, name), "run tools\\prepare-textures.py");
            Copy(Path.Combine(textures, name), TexDir + "/" + name);
        }

        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
    }

    static void Copy(string from, string to)
    {
        var dst = Path.GetFullPath(to);
        if (File.Exists(dst) && new FileInfo(dst).Length == new FileInfo(from).Length
                             && File.GetLastWriteTimeUtc(dst) >= File.GetLastWriteTimeUtc(from))
        {
            return; // unchanged: skipping avoids a reimport of a 9 MB animation file
        }

        File.Copy(from, dst, true);
        Debug.Log("[Werewolf] copied " + Path.GetFileName(from));
    }

    static void Require(string path, string hint)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("missing " + path + " - " + hint);
    }

    // ---------------------------------------------------------------- textures

    static void ConfigureTextures()
    {
        foreach (var v in AvailableVariants)
        {
            ConfigureTexture(TexDir + "/werewolf_albedo_" + v.ToLower() + ".png", TextureImporterType.Default, sRGB: true, alpha: false, max: 2048);
            ConfigureTexture(TexDir + "/werewolf_emission_" + v.ToLower() + ".png", TextureImporterType.Default, sRGB: true, alpha: false, max: 2048);
        }
        ConfigureTexture(TexDir + "/werewolf_normal.png", TextureImporterType.NormalMap, sRGB: false, alpha: false, max: 2048);
        // Specular colour in RGB (linear data, not a picture) and smoothness in A, which is why
        // the alpha has to survive compression.
        ConfigureTexture(TexDir + "/werewolf_specgloss.png", TextureImporterType.Default, sRGB: false, alpha: true, max: 2048);
    }

    static void ConfigureTexture(string path, TextureImporterType type, bool sRGB, bool alpha, int max)
    {
        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
        if (importer == null) throw new Exception("no importer for " + path);

        importer.textureType = type;
        importer.sRGBTexture = sRGB;
        importer.alphaSource = alpha ? TextureImporterAlphaSource.FromInput : TextureImporterAlphaSource.None;
        importer.alphaIsTransparency = false;
        importer.mipmapEnabled = true;
        importer.wrapMode = TextureWrapMode.Repeat;
        importer.maxTextureSize = max;
        importer.textureCompression = TextureImporterCompression.Compressed;
        importer.SaveAndReimport();

        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        Debug.Log("[Werewolf] " + Path.GetFileName(path) + ": " + tex.width + "x" + tex.height + " " + tex.format + " sRGB=" + sRGB);
    }


    /// <summary>The Rig tab's "Root node" for a Generic avatar - vanilla's animalStandardWolfAvatar
    /// names Origin, ours names the rig's root bone.</summary>
    public const string RootMotionBone = "root";

    /// <summary>
    /// Sets the Generic rig's root node, which has NO public C# API - ModelImporter exposes
    /// motionNodeName (a different setting, the Animation tab's Root Motion Node) and nothing
    /// else. The Rig tab writes m_RootMotionBoneName on the importer's SerializedObject, storing
    /// the LAST path component (UnityEditor.ModelImporterRigEditor.GenericGUI ->
    /// FileUtil.GetLastPathNameComponent), so a bare bone name is what belongs here.
    ///
    /// This is what was missing and why root motion silently did nothing: with the field empty
    /// Unity generates no RootT/RootQ curves at all, the baked travel stays in the pose, and the
    /// model walks away from an entity that never moves.
    /// </summary>
    static void SetRootMotionBone(string assetPath, string boneName)
    {
        var importer = AssetImporter.GetAtPath(assetPath);
        if (importer == null) throw new Exception("no importer for " + assetPath);

        var so = new SerializedObject(importer);
        SerializedProperty found = null;
        var it = so.GetIterator();
        while (it.Next(true))
        {
            if (it.propertyType == SerializedPropertyType.String &&
                it.name.IndexOf("RootMotionBoneName", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                found = it.Copy();
                break;
            }
        }
        if (found == null) throw new Exception("this editor's ModelImporter has no RootMotionBoneName property");

        if (found.stringValue == boneName) return;
        found.stringValue = boneName;
        so.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.WriteImportSettingsIfDirty(assetPath);
        AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
        Debug.Log("[Werewolf] root node '" + boneName + "' set on " + Path.GetFileName(assetPath));
    }

    /// <summary>
    /// The colour space has to match the game's or nothing else matters.
    ///
    /// 7 Days to Die runs LINEAR - PlayerSettings.m_ActiveColorSpace is 1 in its data.unity3d. A
    /// bundle built by a Gamma project loads into it perfectly happily and renders washed out and
    /// pale, because the shader inside it was compiled to do gamma maths in a pipeline that is
    /// already doing them. It cost an evening: the near-black werewolf shipped looking like light
    /// tan clay, with the textures, the material and the bundle all provably correct.
    ///
    /// There is no fixing this at build time - switching the setting makes Unity reimport every
    /// texture and recompile every shader - so the build refuses instead and says what to change.
    /// </summary>
    static void RequireLinearColorSpace()
    {
        if (PlayerSettings.colorSpace == ColorSpace.Linear) return;
        throw new Exception(
            "this project is in " + PlayerSettings.colorSpace + " colour space and the game is in Linear. "
            + "Anything built now will render washed out and pale in game. Set Edit > Project Settings > "
            + "Player > Other Settings > Color Space to Linear (or m_ActiveColorSpace: 1 in "
            + "ProjectSettings/ProjectSettings.asset), let the reimport finish, and build again.");
    }

    // ---------------------------------------------------------------- audio

    /// <summary>
    /// The creature's voice. tools\build-asset-bundle.ps1 copies one folder per role out of
    /// art\sound and renames the contents to ww_&lt;role&gt;&lt;n&gt;, which is the name
    /// pack/Config/sounds.xml then asks for; nothing here chooses names.
    ///
    /// Every clip is a short one-shot from a 3D AudioSource, so they are forced to MONO - a stereo
    /// image is thrown away by an engine that pans by position anyway, and it doubles the data -
    /// and decompressed on load, because seeking into a compressed clip to start a snarl is the
    /// wrong trade for a few hundred KB.
    /// </summary>
    static void ConfigureAudio()
    {
        if (!Directory.Exists(AudioDir))
        {
            Debug.Log("[Werewolf] no " + AudioDir + " - the creature keeps whatever voice "
                      + "entityclasses.xml names");
            return;
        }

        var guids = AssetDatabase.FindAssets("t:AudioClip", new[] { AudioDir });
        if (guids.Length == 0)
        {
            Debug.Log("[Werewolf] no audio clips in " + AudioDir);
            return;
        }

        var total = 0f;
        var byRole = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var guid in guids)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            var importer = (AudioImporter)AssetImporter.GetAtPath(path);
            if (importer == null) continue;

            importer.forceToMono = true;
            importer.loadInBackground = false;

            var settings = importer.defaultSampleSettings;
            settings.loadType = AudioClipLoadType.DecompressOnLoad;
            settings.compressionFormat = AudioCompressionFormat.Vorbis;
            settings.quality = 0.7f;
            settings.sampleRateSetting = AudioSampleRateSetting.PreserveSampleRate;
            // Lives in the per-platform sample settings, not on the importer: the importer-level
            // AudioImporter.preloadAudioData is obsolete and refuses to compile.
            settings.preloadAudioData = true;
            importer.defaultSampleSettings = settings;
            importer.SaveAndReimport();

            var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            if (clip == null) continue;
            total += clip.length;

            // ww_alert3 -> alert. Trailing digits are the index the build script assigned.
            var role = Path.GetFileNameWithoutExtension(path);
            if (role.StartsWith("ww_", StringComparison.Ordinal)) role = role.Substring(3);
            role = role.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            byRole.TryGetValue(role, out var n);
            byRole[role] = n + 1;
        }

        var summary = string.Join(", ", byRole.Select(kv => kv.Key + " " + kv.Value).ToArray());
        Debug.Log("[Werewolf] " + guids.Length + " audio clips, " + total.ToString("0.0", CultureInfo.InvariantCulture)
                  + " s total, forced to mono: " + summary);
        Debug.Log("[Werewolf] every one of these needs an <AudioClip> line in pack/Config/sounds.xml - "
                  + "the count is not discovered at runtime");
    }

    // ---------------------------------------------------------------- model

    static Avatar ConfigureModel()
    {
        var importer = (ModelImporter)AssetImporter.GetAtPath(ModelPath);
        if (importer == null) throw new Exception("no model importer for " + ModelPath);

        importer.materialImportMode = ModelImporterMaterialImportMode.None;
        importer.importBlendShapes = false;
        importer.importVisibility = false;
        importer.importCameras = false;
        importer.importLights = false;
        // The game finds bones by name and physicsbodies.xml by path: nothing may be optimised away.
        importer.optimizeGameObjects = false;
        importer.importAnimation = false;
        importer.useFileScale = true;
        importer.globalScale = 1f;
        importer.importNormals = ModelImporterNormals.Import;
        importer.importTangents = ModelImporterTangents.CalculateMikk;
        importer.meshCompression = ModelImporterMeshCompression.Off;
        importer.isReadable = false;

        // Generic, not Humanoid: a four-legged rig has nothing to retarget, and the game's animal
        // controller only writes parameters.
        importer.animationType = ModelImporterAnimationType.Generic;
        importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
        // The Animation tab's "Root Motion Node" - a SECOND, different feature that writes
        // MotionT/MotionQ curves. Vanilla leaves it off: animal_direwolf_run.anim carries
        // MotionT.x/y/z all zero and the travel in RootT instead. Off here too, so there is
        // exactly one source of root motion.
        importer.motionNodeName = "";
        importer.SaveAndReimport();
        SetRootMotionBone(ModelPath, RootMotionBone);

        var avatar = AssetDatabase.LoadAllAssetRepresentationsAtPath(ModelPath).OfType<Avatar>().FirstOrDefault();
        if (avatar == null) throw new Exception("the import produced no Avatar for " + ModelPath);
        Debug.Log("[Werewolf] avatar '" + avatar.name + "' isValid=" + avatar.isValid + " isHuman=" + avatar.isHuman);
        if (!avatar.isValid) throw new Exception("the generic avatar is not valid - see the import log above");

        var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
        foreach (var needed in new[] { "root", "Hips", "Head", "LeftUpLeg", "RightUpLeg", "LeftArm", "RightArm" })
        {
            if (FindRecursive(model.transform, needed) == null)
            {
                throw new Exception("the model has no transform named " + needed + " - the Blender rename did not land");
            }
        }

        return avatar;
    }

    // ---------------------------------------------------------------- animations

    /// <summary>Clip name -> looping. Everything else is a one-shot.</summary>
    static readonly HashSet<string> Loops = new HashSet<string>
    {
        "Idle", "Wait", "Walk", "Run", "Walk_4_Legs", "Run_4_Legs", "Eating", "Fall",
    };

    /// <summary>Clip -> metres per second of travel its legs depict, from
    /// toolslender-measure-stride.py. Under root motion this is the creature's real speed
    /// (times SizeScale), so the build checks the imported clips against it.</summary>
    static readonly Dictionary<string, float> LocomotionSpeeds = new Dictionary<string, float>
    {
        { "Walk_4_Legs", 1.75f },
        { "Run_4_Legs", 6.10f },
    };

    static Dictionary<string, AnimationClip> ConfigureAnimations(Avatar avatar)
    {
        var clips = new Dictionary<string, AnimationClip>();
        var files = Directory.GetFiles(AnimDir, "*.fbx").Select(p => p.Replace('\\', '/')).OrderBy(p => p).ToArray();
        if (files.Length == 0) throw new Exception("no animation FBX under " + AnimDir);

        foreach (var path in files)
        {
            var importer = (ModelImporter)AssetImporter.GetAtPath(path);
            if (importer == null) throw new Exception("no importer for " + path);

            importer.materialImportMode = ModelImporterMaterialImportMode.None;
            importer.importBlendShapes = false;
            importer.importVisibility = false;
            importer.importCameras = false;
            importer.importLights = false;
            importer.optimizeGameObjects = false;
            importer.useFileScale = true;
            importer.globalScale = 1f;
            importer.animationType = ModelImporterAnimationType.Generic;
            // Same skeleton as the model, so the clips bind to the model's avatar. The bone names
            // match because both went through the same Blender rename.
            importer.avatarSetup = ModelImporterAvatarSetup.CopyFromOther;
            importer.sourceAvatar = avatar;
            importer.importAnimation = true;
            importer.animationCompression = ModelImporterAnimationCompression.Optimal;
            importer.resampleCurves = true;

            var defaults = importer.defaultClipAnimations;
            var list = new List<ModelImporterClipAnimation>();
            foreach (var take in defaults)
            {
                var name = StripTake(take.takeName);
                if (list.Any(c => c.name == name)) continue;
                var clip = new ModelImporterClipAnimation
                {
                    name = name,
                    takeName = take.takeName,
                    firstFrame = take.firstFrame,
                    lastFrame = take.lastFrame,
                    loopTime = Loops.Contains(name),
                    loopPose = false,
                    wrapMode = Loops.Contains(name) ? WrapMode.Loop : WrapMode.Default,
                    // Vanilla's exact clip settings (animal_direwolf_run.anim): keep the height
                    // in the pose, EXTRACT the ground travel and the facing into root motion.
                    // toolslender-bake-root-motion.py put the travel on the root bone first,
                    // and the Rig tab's root node (SetRootMotionBone) is what lets Unity find it.
                    keepOriginalPositionY = true,
                    keepOriginalPositionXZ = false,
                    keepOriginalOrientation = false,
                };
                // The hit reaction plays on an ADDITIVE layer (AvatarController drives that
                // layer's weight from 0 to ~0.8 per hit), so the clip has to be a delta from its
                // own first frame - otherwise the full pose is added on top of the locomotion and
                // the creature folds in half on every bullet.
                if (name == "Hit")
                {
                    clip.hasAdditiveReferencePose = true;
                    clip.additiveReferencePoseFrame = take.firstFrame;
                }
                list.Add(clip);
            }
            importer.clipAnimations = list.ToArray();
            importer.SaveAndReimport();
            // CopyFromOther brings the source avatar's human description across, but the root node
            // is what root motion hangs off, so set it here too rather than trust the copy.
            SetRootMotionBone(path, RootMotionBone);

            var imported = AssetDatabase.LoadAllAssetRepresentationsAtPath(path).OfType<AnimationClip>().ToArray();
            foreach (var clip in imported)
            {
                if (clip.name.StartsWith("__preview__")) continue;
                if (clips.ContainsKey(clip.name)) throw new Exception("two animation files define a clip named " + clip.name);
                clips[clip.name] = clip;
                var rootSpeed = clip.averageSpeed.magnitude;
                Debug.Log("[Werewolf] clip " + clip.name + " " + clip.length.ToString("0.00", CultureInfo.InvariantCulture)
                          + "s loop=" + clip.isLooping
                          + " rootMotion=" + rootSpeed.ToString("0.00", CultureInfo.InvariantCulture) + " m/s"
                          + " genericRoot=" + clip.hasGenericRootTransform + " motionCurves=" + clip.hasMotionCurves
                          + " from " + Path.GetFileName(path));
            }
        }

        foreach (var needed in WerewolfAnimator.RequiredClips)
        {
            if (!clips.ContainsKey(needed)) throw new Exception("no clip named " + needed + " in " + AnimDir);
        }

        // The gate on the whole root motion path. With RootMotion="true" the entity's ground speed
        // IS this number times SizeScale - EntityAlive.MoveEntityHeaded replaces motion with the
        // accumulated root motion and DefaultMoveEntity skips Entity.Move altogether while
        // grounded. If extraction failed the clips read 0 m/s here, the creature would stand still
        // and the model would walk out from under it, so fail the build instead of shipping that.
        foreach (var pair in LocomotionSpeeds)
        {
            var measured = clips[pair.Key].averageSpeed.magnitude;
            // The turn the clip's own root motion adds. keepOriginalOrientation = false means
            // Unity extracts rotation as well as travel, so a clip whose body sways would steer
            // the creature off a straight line all by itself - the curving run that started this.
            // Vanilla's own animals import the same way, so a small number here is expected and
            // a large one is a bug in the clip.
            var turn = clips[pair.Key].averageAngularSpeed * Mathf.Rad2Deg;
            Debug.Log("[Werewolf] root motion " + pair.Key + ": " + measured.ToString("0.00", CultureInfo.InvariantCulture)
                      + " m/s (blender measured " + pair.Value.ToString("0.00", CultureInfo.InvariantCulture) + ")"
                      + ", turn " + turn.ToString("0.0", CultureInfo.InvariantCulture) + " deg/s");
            if (Mathf.Abs(turn) > 15f)
            {
                throw new Exception("clip " + pair.Key + " carries " + turn.ToString("0.0", CultureInfo.InvariantCulture)
                                    + " deg/s of root rotation - it would steer the creature in a circle");
            }
            if (measured < pair.Value * 0.6f)
            {
                throw new Exception("clip " + pair.Key + " carries " + measured.ToString("0.00", CultureInfo.InvariantCulture)
                                    + " m/s of root motion, expected about " + pair.Value.ToString("0.00", CultureInfo.InvariantCulture)
                                    + " - the travel is not being extracted, check the Rig tab root node");
            }
        }

        return clips;
    }

    /// <summary>"Armature|Idle" (Blender's exporter prefixes the object name) -> "Idle".</summary>
    static string StripTake(string takeName)
    {
        var i = takeName.LastIndexOf('|');
        return i >= 0 ? takeName.Substring(i + 1) : takeName;
    }

    // ---------------------------------------------------------------- materials

    /// <summary>
    /// Unity's Standard shader in its Specular setup, configured like the vanilla wolf's material
    /// (wolf.mat, keywords _NORMALMAP _SPECGLOSSMAP _EMISSION). That exact variant is compiled
    /// into the game's player because a vanilla animal ships with it; the purchased URP material
    /// cannot be used at all (the game is built-in pipeline). Emission carries the eyes and the
    /// glow pattern that differ per variant.
    /// </summary>
    static Dictionary<string, Material> BuildMaterials()
    {
        var shader = Shader.Find("Standard (Specular setup)");
        if (shader == null) throw new Exception("no 'Standard (Specular setup)' shader in this editor");
        Directory.CreateDirectory(MatDir);

        var normal = Load<Texture2D>(TexDir + "/werewolf_normal.png");
        var result = new Dictionary<string, Material>();

        foreach (var v in AvailableVariants)
        {
            var path = MaterialPath(v);
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }
            material.shader = shader;
            material.SetFloat("_Mode", 0f);
            material.SetColor("_Color", Tint(v));
            material.SetTexture("_MainTex", string.IsNullOrEmpty(DebugFlatAlbedo)
                ? Load<Texture2D>(TexDir + "/werewolf_albedo_" + v.ToLower() + ".png")
                : FlatTexture(DebugFlatAlbedo));
            material.SetTexture("_BumpMap", normal);
            material.SetFloat("_BumpScale", 1f);
            material.EnableKeyword("_NORMALMAP");
            // The specular map carries the creature's colour as much as the albedo does - see
            // TintedSpecGloss. A white tint hands back the shared map untouched.
            material.SetTexture("_SpecGlossMap", TintedSpecGloss(SpecTint(v)));
            material.SetFloat("_SmoothnessTextureChannel", 0f);
            material.SetFloat("_GlossMapScale", 1f);
            material.SetFloat("_Glossiness", 0.5f);
            material.SetFloat("_SpecularHighlights", 1f);
            material.SetFloat("_GlossyReflections", 1f);
            material.EnableKeyword("_SPECGLOSSMAP");
            material.SetTexture("_EmissionMap", Load<Texture2D>(TexDir + "/werewolf_emission_" + v.ToLower() + ".png"));
            material.SetColor("_EmissionColor", Color.white);
            material.EnableKeyword("_EMISSION");
            material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            EditorUtility.SetDirty(material);
            result[v] = material;
        }

        Debug.Log("[Werewolf] " + result.Count + " materials on '" + shader.name + "'; albedo tint "
                  + Variants[0] + "=" + ColorUtility.ToHtmlStringRGB(Tint(Variants[0])));
        return result;
    }

    static T Load<T>(string path) where T : UnityEngine.Object
    {
        var asset = AssetDatabase.LoadAssetAtPath<T>(path);
        if (asset == null) throw new FileNotFoundException("missing asset " + path);
        return asset;
    }

    // ---------------------------------------------------------------- prefab

    static void BuildPrefab(string variant, Avatar avatar, RuntimeAnimatorController controller, Material material)
    {
        var fbx = Load<GameObject>(ModelPath);
        var bones = ReadBoneTable();

        var root = new GameObject(PrefabName(variant));
        try
        {
            var entityGo = new GameObject("GameObject");
            entityGo.tag = "E_Enemy";
            entityGo.transform.SetParent(root.transform, false);

            // Model has to be the FIRST child of GameObject: createModel takes GetChild(0).
            var model = UnityEngine.Object.Instantiate(fbx);
            model.name = "Model";
            model.transform.SetParent(entityGo.transform, false);
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;
            model.transform.localScale = Vector3.one;

            var animator = model.GetComponent<Animator>() ?? model.AddComponent<Animator>();
            animator.avatar = avatar;
            animator.runtimeAnimatorController = controller;
            // ON, exactly as vanilla ships it (animalWolf.prefab and animalDireWolfPrefab.prefab
            // both serialise m_ApplyRootMotion: 1) and as RootMotion="true" in entityclasses.xml
            // requires. AvatarAnimalController.SwitchModelAndView adds an AvatarRootMotion
            // component to this transform, and Unity only calls its OnAnimatorMove - the one
            // route by which Animator.deltaPosition reaches EntityAlive.accumulatedRootMotion -
            // while this flag is on. The game never sets it, so the prefab decides.
            animator.applyRootMotion = true;
            animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
            animator.updateMode = AnimatorUpdateMode.Normal;

            // The four LODs come out of the FBX under Armature; they move up next to the skeleton
            // as LOD0..LOD3 so EModelBase.SwitchModelAndView's Find("LOD0") lands and the game's
            // material code finds them by the LOD tag.
            var renderers = model.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .OrderBy(r => r.name, StringComparer.Ordinal).ToArray();
            if (renderers.Length != 4) throw new Exception("expected 4 skinned meshes (Werewolf0..3), found " + renderers.Length);
            for (var i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                r.transform.SetParent(model.transform, true);
                r.gameObject.name = "LOD" + i;
                r.gameObject.tag = "LOD";
                r.sharedMaterial = material;
                r.updateWhenOffscreen = false;
                r.skinnedMotionVectors = true;
                r.quality = SkinQuality.Auto;
            }

            var lodGroup = model.AddComponent<LODGroup>();
            var lods = new LOD[renderers.Length];
            for (var i = 0; i < renderers.Length; i++) lods[i] = new LOD(LodHeights[i], new Renderer[] { renderers[i] });
            lodGroup.SetLODs(lods);
            lodGroup.fadeMode = LODFadeMode.None;
            lodGroup.RecalculateBounds();

            var rigRoot = FindRecursive(model.transform, "root");
            rigRoot.gameObject.tag = "E_BP_BipedRoot";
            var hips = FindRecursive(model.transform, "Hips");
            var head = FindRecursive(model.transform, "Head");

            BuildBodyColliders(model.transform, bones);
            // No BuildBlocker call: a LargeEntityBlocker turns the creature into a PhysX body,
            // see the note on BuildBlocker.

            var physics = new GameObject("Physics");
            physics.tag = "Physics";
            physics.layer = 15; // CC Physics
            physics.transform.SetParent(root.transform, false);
            var capsule = physics.AddComponent<CapsuleCollider>();
            // THIS CAPSULE IS THE CHARACTER CONTROLLER, not a hit box. Entity.AddCharacterController
            // reads the CapsuleCollider on the "Physics" transform - centre, height, radius - and
            // builds the controller from it, then SizeScale multiplies the lot.
            //
            // EntityMoveHelper then paths with those two numbers, and both have hard voxel limits:
            //   CheckWorldBlocked raycasts a column ccHeight tall, so a controller over 2 m needs
            //   THREE clear blocks of headroom and is blocked by ordinary terrain and doorways;
            //   CalcObstacleSideStep arcs around anything within ccRadius, so a controller wider
            //   than a block treats one-block gaps as walls and side-steps constantly.
            // Get either wrong and the creature runs at its target in zigzags, forever going round
            // obstacles that are not there. Which is exactly what r .45 h 2.2 x SizeScale 1.2 did:
            // 1.08 m wide and 2.64 m tall.
            //
            // So these are sized to the gameplay envelope, not to the model, the way vanilla does
            // it - a bear's mesh is far bigger than its r .44 h 1.82 controller.
            // The visible bulk is not lost; the hit colliders are the per-bone physics body above.
            //
            // Effective size lands at r .42 h 1.9.
            //
            // .34 was tried, on the theory that a 1 m gap taken diagonally offers only
            // 1.00/sqrt(2) = 0.707 m across the direction of travel, less than this creature's .84.
            // The theory is WRONG and the test that killed it was one command: spawn an
            // animalZombieBear, which is .88 wide - wider than ours - and walk it at the same gap.
            // It went straight through without breaking stride. The capsule slides along surfaces
            // rather than being cut by the literal diagonal, so widths up to nearly a full block
            // are fine. Ripped from the game's own prefabs for reference, effective widths are:
            // bear and zombie bear .88, boss boar .80, ours .84, dire wolf .67, boar .60.
            //
            // Whatever stops this creature at a gap, it is not how wide it is.
            const float effectiveRadius = 0.42f;
            const float effectiveHeight = 1.90f;
            var scale = EntitySizeScale;
            capsule.radius = effectiveRadius / scale;
            capsule.height = effectiveHeight / scale;
            capsule.direction = 1;
            capsule.center = new Vector3(0f, effectiveHeight / scale * 0.5f, 0f);

            Directory.CreateDirectory(PrefabDir);
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath(variant));
            Report(root, renderers[0], hips, head, variant);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }
    }

    /// <summary>
    /// Capsules, rigidbodies and joints on the twelve bones physicsbodies.xml names. The game
    /// wraps whatever Collider it finds on each path (PhysicsBodyInstance.bindCollider: Box,
    /// Capsule or Sphere, else a null collider that never registers a hit), adds a Rigidbody where
    /// one is missing and sets the E_BP_* tag and the layer from the XML - so the tags here are a
    /// courtesy to EModelBase, which looks E_BP_Head up before the physics body exists.
    /// Radii are measured off the LOD0 vertices per bone in Blender (werewolf_bones.txt, median
    /// distance to the bone axis). The joints make the ragdoll; HasRagdoll in the XML decides
    /// whether it is used.
    /// </summary>
    static void BuildBodyColliders(Transform model, Dictionary<string, BoneStats> bones)
    {
        var rigidbodies = new Dictionary<string, Rigidbody>();
        foreach (var (boneName, tag, _, _) in PhysicsBones)
        {
            var bone = FindRecursive(model, boneName);
            if (bone == null) throw new Exception("no bone " + boneName + " for the physics body");
            bone.gameObject.tag = tag;
            var rb = bone.gameObject.AddComponent<Rigidbody>();
            rb.mass = 5f;

            // TRUE. False was tried for one build, to match the vanilla bear whose eleven ragdoll
            // bodies are all isKinematic 0, on the theory that kinematic bodies are immovable to a
            // character controller and might be what was stopping the creature dead on open floor.
            // It changed nothing measurable - the creature still crawled through its Move state at
            // 0.1-0.45 m/s - and it introduced twelve gravity-bearing rigidbodies whose effect on
            // that same measurement is unknown, because the measurement did not exist before the
            // change. Reverted so the remaining slowness can be read against a known baseline.
            rb.isKinematic = true;
            rb.useGravity = true;
            rb.interpolation = RigidbodyInterpolation.None;
            rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
            rigidbodies[boneName] = rb;
        }

        foreach (var (boneName, _, toward, fixedRadius) in PhysicsBones)
        {
            var bone = FindRecursive(model, boneName);
            bones.TryGetValue(boneName, out var stats);
            var radius = fixedRadius > 0f
                ? fixedRadius
                : Mathf.Clamp(stats != null && stats.R50 > 0 ? stats.R50 * MeasuredRadiusScale : 0.15f, 0.10f, 0.30f);

            if (toward == null)
            {
                // The head: a sphere around the skull, centred where the measured vertices sit
                // along the bone (the muzzle is ahead of the head joint).
                var sphere = bone.gameObject.AddComponent<SphereCollider>();
                sphere.radius = radius;
                var along = stats != null ? (stats.AlongMin + stats.AlongMax) * 0.5f * stats.Length : 0.05f;
                var child = bone.childCount > 0 ? bone.GetChild(0) : null;
                var axis = child != null ? child.localPosition.normalized : Vector3.up;
                sphere.center = axis * along;
                continue;
            }

            var target = FindRecursive(model, toward);
            var local = bone.InverseTransformPoint(target.position);
            var capsule = bone.gameObject.AddComponent<CapsuleCollider>();
            capsule.direction = MajorAxis(local);
            capsule.radius = radius;
            capsule.height = local.magnitude + radius;
            capsule.center = local * 0.5f;
        }

        foreach (var (boneName, _, _, _) in PhysicsBones)
        {
            if (boneName == "Hips") continue;
            var bone = FindRecursive(model, boneName);
            Rigidbody parentRb = null;
            for (var t = bone.parent; t != null && parentRb == null; t = t.parent) rigidbodies.TryGetValue(t.name, out parentRb);
            if (parentRb == null) continue;

            var joint = bone.gameObject.AddComponent<CharacterJoint>();
            joint.connectedBody = parentRb;
            joint.enableProjection = true;
            joint.enablePreprocessing = false;
            var isHinge = boneName.StartsWith("calf") || boneName.StartsWith("lowerarm");
            joint.lowTwistLimit = new SoftJointLimit { limit = isHinge ? -60f : -25f };
            joint.highTwistLimit = new SoftJointLimit { limit = isHinge ? 0f : 25f };
            joint.swing1Limit = new SoftJointLimit { limit = isHinge ? 5f : 30f };
            joint.swing2Limit = new SoftJointLimit { limit = isHinge ? 5f : 30f };
        }
    }

    /// <summary>
    /// NOT CALLED ANY MORE, kept as a record. The LargeEntityBlocker capsules (layer 19) were copied
    /// from the bear to stop players walking through the body. What the tag actually does in the
    /// game is far more than that: Entity.PhysicsInit finds the node by tag, reparents it to the
    /// world and adds a DYNAMIC Rigidbody to it, and from then on Entity.ApplyFixedUpdate snaps the
    /// entity's position to wherever PhysX left that rigidbody, every frame. The character
    /// controller's capsule (r 0.42) walked through a 2-block doorway; the blocker's three capsules
    /// (scaled by SizeScale 1.2, the spine one at chest height) did not, PhysX pushed them back out
    /// of the wall and the creature was dragged back with them - measured as 7.3 m/s moved inside
    /// the collision call and 0.7 m/s of it kept. Wolves and dogs have no blocker; only the bear-sized
    /// animals that are meant to be pushed around by physics do. WerewolfBlocker.cs also removes one
    /// at runtime in case an older bundle is deployed.
    /// </summary>
    static void BuildBlocker(Transform model, Transform hips, Transform head, Transform spine5, Transform neck)
    {
        var go = new GameObject("Collider");
        go.tag = "LargeEntityBlocker";
        go.layer = 19;
        go.transform.SetParent(model.transform, false);

        var h = model.InverseTransformPoint(hips.position);
        var s5 = model.InverseTransformPoint(spine5.position);
        var hd = model.InverseTransformPoint(head.position);
        var nk = model.InverseTransformPoint(neck.position);

        var spine = go.AddComponent<CapsuleCollider>();
        spine.direction = 2; // along Z, the facing axis
        spine.radius = 0.38f;
        spine.center = new Vector3(0f, (h.y + s5.y) * 0.5f, (h.z + s5.z) * 0.5f);
        spine.height = Mathf.Abs(s5.z - h.z) + 0.9f;

        var legs = go.AddComponent<CapsuleCollider>();
        legs.direction = 1;
        legs.radius = 0.36f;
        legs.center = new Vector3(0f, (h.y + 0.35f) * 0.5f, h.z);
        legs.height = h.y - 0.35f + 0.72f;

        var skull = go.AddComponent<CapsuleCollider>();
        skull.direction = 2;
        skull.radius = 0.3f;
        skull.center = new Vector3(0f, (nk.y + hd.y) * 0.5f, (nk.z + hd.z) * 0.5f + 0.1f);
        skull.height = Mathf.Abs(hd.z - nk.z) + 0.8f;
    }

    static int MajorAxis(Vector3 v)
    {
        var a = new[] { Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z) };
        return a[0] >= a[1] && a[0] >= a[2] ? 0 : a[1] >= a[2] ? 1 : 2;
    }

    class BoneStats
    {
        public float Length, R50, R90, AlongMin, AlongMax;
    }

    /// <summary>werewolf_bones.txt from blender-prepare-werewolf.py: name parent length r50 r90 alongMin alongMax vertices.</summary>
    static Dictionary<string, BoneStats> ReadBoneTable()
    {
        var table = new Dictionary<string, BoneStats>();
        if (!File.Exists(BonesTable))
        {
            Debug.LogWarning("[Werewolf] no " + BonesTable + " - colliders get a default radius");
            return table;
        }
        foreach (var line in File.ReadAllLines(BonesTable))
        {
            if (line.StartsWith("#") || string.IsNullOrWhiteSpace(line)) continue;
            var p = line.Split(' ');
            if (p.Length < 8) continue;
            table[p[0]] = new BoneStats
            {
                Length = F(p[2]), R50 = F(p[3]), R90 = F(p[4]), AlongMin = F(p[5]), AlongMax = F(p[6]),
            };
        }
        return table;
    }

    static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

    public static Transform FindRecursive(Transform t, string name)
    {
        if (t.name == name) return t;
        for (var i = 0; i < t.childCount; i++)
        {
            var hit = FindRecursive(t.GetChild(i), name);
            if (hit != null) return hit;
        }
        return null;
    }

    // ---------------------------------------------------------------- report

    /// <summary>
    /// What was actually built. Heights are read off the bones, because sharedMesh.bounds on an
    /// imported skinned mesh means nothing: a wrong FBX scale shows as a 227 m or 2 cm creature.
    /// </summary>
    static void Report(GameObject root, SkinnedMeshRenderer lod0, Transform hips, Transform head, string variant)
    {
        var bones = lod0.bones;
        Debug.Log("[Werewolf] prefab '" + root.name + "' (" + variant + "): LOD0 " + lod0.sharedMesh.triangles.Length / 3
                  + " tris, " + bones.Length + " bones, material '" + lod0.sharedMaterial.name + "'");
        Debug.Log("[Werewolf] Hips at y=" + hips.position.y.ToString("0.000", CultureInfo.InvariantCulture)
                  + " z=" + hips.position.z.ToString("0.000", CultureInfo.InvariantCulture)
                  + " m, Head at y=" + head.position.y.ToString("0.000", CultureInfo.InvariantCulture)
                  + " z=" + head.position.z.ToString("0.000", CultureInfo.InvariantCulture)
                  + " m, skinned bounds " + lod0.bounds.size.ToString("0.00"));

        var scaled = root.GetComponentsInChildren<Transform>(true)
            .Where(t => (t.localScale - Vector3.one).sqrMagnitude > 1e-6f)
            .Select(t => t.name + "=" + t.localScale.ToString("0.###"))
            .ToArray();
        Debug.Log("[Werewolf] transforms with a non-unit scale: " + (scaled.Length == 0 ? "none" : string.Join(", ", scaled)));

        var lines = new List<string>();
        Walk(root.transform, 0, lines, 4);
        foreach (var line in lines) Debug.Log("[Werewolf]   " + line);
    }

    static void Walk(Transform t, int depth, List<string> into, int limit)
    {
        var components = t.GetComponents<Component>()
            .Where(c => c != null && !(c is Transform))
            .Select(c => c.GetType().Name);
        var tag = t.tag == "Untagged" ? "" : "  tag=" + t.tag;
        var layer = t.gameObject.layer == 0 ? "" : "  layer=" + t.gameObject.layer;
        into.Add(new string(' ', depth * 2) + t.name + tag + layer
                 + (components.Any() ? "  [" + string.Join(", ", components) + "]" : ""));
        if (depth >= limit)
        {
            if (t.childCount > 0) into.Add(new string(' ', (depth + 1) * 2) + "... " + t.childCount + " children");
            return;
        }
        for (var i = 0; i < t.childCount; i++) Walk(t.GetChild(i), depth + 1, into, limit);
    }
}
