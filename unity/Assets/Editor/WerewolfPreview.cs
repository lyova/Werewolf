// Renders the built prefab in a few poses to PNG, so the skinning, the materials and the clip
// import can be judged by eye without launching the game or the Unity GUI.
//
//   Unity.exe -batchmode -projectPath mods\Werewolf\unity -executeMethod WerewolfPreview.Render -previewOut <dir> -logFile ... -quit
//
// NOT -nographics: the camera has to render. Poses come from AnimationMode sampling, the same
// route the Animation window uses; a PlayableGraph does nothing outside play mode (see the
// sibling mod's AnimationPreview.cs for the full story). Skinned renderers are forced to re-skin
// per render, otherwise every frame shows the bind pose.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

public static class WerewolfPreview
{
    // clip, normalized time, camera yaw (degrees around the model; 0 = from the front)
    static readonly (string clip, float t, float yaw)[] Shots =
    {
        ("Idle", 0.0f, 35f),
        ("Idle", 0.0f, 90f),
        ("Idle", 0.0f, 180f),
        ("Walk_4_Legs", 0.25f, 60f),
        ("Run_4_Legs", 0.5f, 60f),
        // Both swipes, from the FRONT (yaw 0), because the question they answer is where the head
        // is pointing. Only the left one was ever seen in game until the swipes were made to
        // alternate, so if it turns the head that was the pose on every single attack.
        // The chase pose, from the FRONT. Reported in game: while running the head mostly points
        // off to the right, while walking or standing it points straight ahead. Walk and run are
        // different clips, so that difference is measurable here rather than guessable.
        ("Run_4_Legs", 0.00f, 0f),
        ("Run_4_Legs", 0.25f, 0f),
        ("Run_4_Legs", 0.50f, 0f),
        ("Run_4_Legs", 0.75f, 0f),
        ("Walk_4_Legs", 0.25f, 0f),
        ("Walk_4_Legs", 0.75f, 0f),
        ("Idle", 0.5f, 0f),
        ("Attack_L", 0.25f, 0f),
        ("Attack_L", 0.40f, 0f),
        ("Attack_L", 0.60f, 0f),
        ("Attack_R", 0.25f, 0f),
        ("Attack_R", 0.40f, 0f),
        ("Attack_R", 0.60f, 0f),
        ("Attack_L", 0.4f, 20f),
        ("Hit", 0.3f, 35f),
        ("Death", 0.98f, 60f),
        ("Eating", 0.5f, 45f),
        ("Jump", 0.5f, 60f),
        // The howl, four frames across the clip. "Agr" is the seller's name for it and the only
        // clip in the asset it could be; these shots are how that gets confirmed by eye rather
        // than by the name alone, because the whole summon is built on it.
        ("Agr", 0.15f, 35f),
        ("Agr", 0.40f, 35f),
        ("Agr", 0.60f, 35f),
        ("Agr", 0.85f, 35f),
    };

    const int Size = 768;

    public static void Render()
    {
        var exitCode = 0;
        try
        {
            Run();
        }
        catch (Exception e)
        {
            Debug.LogError("[Werewolf] preview failed: " + e);
            exitCode = 1;
        }
        if (Application.isBatchMode) EditorApplication.Exit(exitCode);
    }

    static void Run()
    {
        var outDir = ArgValue("-previewOut")
                     ?? Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "art", "work", "preview"));
        Directory.CreateDirectory(outDir);

        var clips = new Dictionary<string, AnimationClip>();
        foreach (var path in Directory.GetFiles(ModelSetup.AnimDir, "*.fbx"))
        {
            foreach (var clip in AssetDatabase.LoadAllAssetRepresentationsAtPath(path.Replace('\\', '/')).OfType<AnimationClip>())
            {
                if (!clip.name.StartsWith("__preview__")) clips[clip.name] = clip;
            }
        }

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        // LOW ambient, on purpose. This was 0.45 flat grey plus 1.7 of directional light between
        // the two lamps below - a photographic studio - and at that level the ambient term alone
        // lifts a near-black pelt to mid grey. Every judgement about how dark this creature is,
        // made off these renders, was made against lighting that made the question meaningless.
        //
        // 0.08 is roughly what an overcast outdoor scene contributes, and the key below is one
        // sun. The point of these frames is to show what the TEXTURE looks like on the model, not
        // to flatter it.
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.08f, 0.08f, 0.1f);
        RenderSettings.ambientIntensity = 1f;
        RenderSettings.skybox = null;

        var key = new GameObject("key").AddComponent<Light>();
        key.type = LightType.Directional;
        key.intensity = 1f;
        key.transform.rotation = Quaternion.Euler(40f, -30f, 0f);
        var fill = new GameObject("fill").AddComponent<Light>();
        fill.type = LightType.Directional;
        fill.intensity = 0.25f;
        fill.transform.rotation = Quaternion.Euler(-10f, 150f, 0f);

        var camera = new GameObject("camera").AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.16f, 0.17f, 0.2f, 1f);
        camera.fieldOfView = 35f;
        camera.nearClipPlane = 0.05f;
        camera.farClipPlane = 50f;

        // Whatever is actually built - only the brown one, until biome variants are added back.
        var variants = ModelSetup.Variants;
        var written = 0;
        AnimationMode.StartAnimationMode();
        try
        {
            foreach (var variant in variants)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ModelSetup.PrefabPath(variant));
                if (prefab == null) throw new FileNotFoundException("no prefab for " + variant);
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                var model = instance.transform.Find("GameObject/Model").gameObject;
                var animator = model.GetComponent<Animator>();
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                foreach (var skin in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    skin.updateWhenOffscreen = true;
                    skin.forceMatrixRecalculationPerRender = true;
                }
                // The LODGroup would pick a lower LOD for a far camera; force LOD0 for the check.
                var lodGroup = model.GetComponent<LODGroup>();
                if (lodGroup != null) lodGroup.ForceLOD(0);

                // Every pose for the main creature; a single idle for any extra variant.
                var shots = variant == ModelSetup.Variants[0] ? Shots : Shots.Take(1).ToArray();
                var hips = ModelSetup.FindRecursive(model.transform, "Hips");
                var head = ModelSetup.FindRecursive(model.transform, "Head");
                var handL = ModelSetup.FindRecursive(model.transform, "hand_l");
                var handR = ModelSetup.FindRecursive(model.transform, "hand_r");
                foreach (var (clipName, t, yaw) in shots)
                {
                    if (!clips.TryGetValue(clipName, out var clip)) throw new Exception("no clip " + clipName);
                    if (variant == ModelSetup.Variants[0]) ReportBindings(model, clip);
                    AnimationMode.BeginSampling();
                    AnimationMode.SampleAnimationClip(model, clip, clip.length * t);
                    AnimationMode.EndSampling();
                    // WHERE THE MUZZLE POINTS, in the model's own frame. Reported in game: the head
                    // looks off to one side while the creature chases, and straight ahead while it
                    // walks or stands. Walk and run are different clips, so if that is in the
                    // artwork it shows up as a yaw that differs between them - and if it is not,
                    // the cause is something the game does at runtime and the search moves there.
                    //
                    // Signed: positive is the creature's left, negative its right. Measured off the
                    // head bone's own forward axis, projected flat, against the model's forward.
                    var headYaw = 0f;
                    var headPitch = 0f;
                    if (head != null)
                    {
                        var local = Quaternion.Inverse(model.transform.rotation) * head.rotation;
                        var facing = local * Vector3.forward;
                        headYaw = Mathf.Atan2(facing.x, facing.z) * Mathf.Rad2Deg;
                        headPitch = -Mathf.Asin(Mathf.Clamp(facing.y, -1f, 1f)) * Mathf.Rad2Deg;
                    }
                    // HOW FAR THE STRIKE ACTUALLY REACHES, forward of the creature's own origin.
                    //
                    // The Range property on the hand item is measured CENTRE TO CENTRE - the AI
                    // compares Entity.GetDistanceSq, which is plain position-to-position with no
                    // regard for how big either body is. So half of that number is buried inside
                    // the attacker, and how much depends entirely on its shape: a bear is two
                    // metres of horizontal animal and its centre sits about a metre behind the
                    // snout, while this one rears up to swing and its centre is almost directly
                    // under the muzzle. Copying a bear's 3.1 onto an upright creature therefore
                    // buys far more real gap than it did on the bear, and it swipes at air.
                    //
                    // What matters for a creature that strikes with arms is where the CLAW gets
                    // to, so that is what this measures: the forward reach of each hand and of the
                    // head, in the model's own frame, at each sampled moment of the swing.
                    var reach = "";
                    if (clipName.StartsWith("Attack", StringComparison.Ordinal))
                    {
                        // Called through the transform each time rather than cached into a local:
                        // assigning a method group to var wants C# 10 and this editor compiles C# 9.
                        var hl = handL != null ? model.transform.InverseTransformPoint(handL.position).z : 0f;
                        var hr = handR != null ? model.transform.InverseTransformPoint(handR.position).z : 0f;
                        var hd = head != null ? model.transform.InverseTransformPoint(head.position).z : 0f;
                        reach = " | REACH forward: handL " + hl.ToString("0.00", CultureInfo.InvariantCulture)
                                + " handR " + hr.ToString("0.00", CultureInfo.InvariantCulture)
                                + " head " + hd.ToString("0.00", CultureInfo.InvariantCulture) + " m";
                    }
                    Debug.Log("[Werewolf] " + clipName + " @" + t.ToString("0.00", CultureInfo.InvariantCulture)
                              + ": Hips world " + hips.position.ToString("0.000") + reach
                              + " | HEAD yaw " + headYaw.ToString("0.0", CultureInfo.InvariantCulture)
                              + " pitch " + headPitch.ToString("0.0", CultureInfo.InvariantCulture)
                              + " (+ is the creature's left)");

                    var target = new Vector3(0f, 1.1f, 0f);
                    var dir = Quaternion.Euler(12f, yaw, 0f) * Vector3.forward;
                    camera.transform.position = target - dir * 6.5f;
                    camera.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);

                    var file = Path.Combine(outDir, variant + "_" + clipName + "_" + t.ToString("0.00", CultureInfo.InvariantCulture) + "_" + (int)yaw + ".png");
                    File.WriteAllBytes(file, Shoot(camera));
                    written++;
                }
                written += RenderColourComparison(instance, model, camera, clips, outDir);
                written += RenderTintLadder(instance, model, camera, clips, outDir);
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }
        finally
        {
            AnimationMode.StopAnimationMode();
        }
        Debug.Log("[Werewolf] wrote " + written + " preview frames to " + outDir);
    }

    /// <summary>
    /// One idle shot per colour the asset has, by swapping the material on the LOD renderers of
    /// the already-built prefab instance. The point is to pick a colour by looking rather than by
    /// trusting the seller's names, which do not match what the creature reads as in game - the
    /// one called Brown looks grey, and the darkest is called Dark. Files are named
    /// colour_&lt;Variant&gt;.png so they sort together.
    /// </summary>
    /// <summary>
    /// The same creature in several albedo tints, under DAYLIGHT and under NIGHT, so "make it
    /// darker, more red-black" can be settled by looking at pictures instead of by another round of
    /// build, deploy, restart, argue.
    ///
    /// TWO LIGHTING SETUPS, because the complaint and the constraint live at opposite ends of the
    /// day. The creature reads grey at NOON - a near-neutral pelt lit by neutral sunlight has
    /// nothing in it for the light to tint, and the brighter the sun the greyer it gets. But it
    /// also has to stay legible at NIGHT, and a tint dark enough to fix the noon problem can take
    /// the whole model to a silhouette after dark. Any candidate has to survive both frames.
    ///
    /// The rest of the preview deliberately runs at a low ambient (see Run) so that judgements
    /// about how dark the texture is are not made under studio lighting. That is exactly wrong for
    /// this question, so the daylight rungs raise it for the shot and put it back afterwards.
    ///
    /// The camera looks the creature in the face here (yaw 180), not at the three-quarter rear the
    /// rest of the preview uses: the muzzle, chest and inner limbs are the pale skin that reads as
    /// grey at a distance, and they are all on the front.
    /// </summary>
    static int RenderTintLadder(GameObject instance, GameObject model, Camera camera,
                                Dictionary<string, AnimationClip> clips, string outDir)
    {
        var variant = ModelSetup.Variants[0];
        var source = AssetDatabase.LoadAssetAtPath<Material>(ModelSetup.MaterialPath(variant));
        if (source == null) return 0;

        var renderers = instance.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        var original = renderers.Select(r => r.sharedMaterial).ToArray();
        var written = 0;

        var wasAmbient = RenderSettings.ambientLight;
        var wasBackground = camera.backgroundColor;
        var lights = UnityEngine.Object.FindObjectsOfType<Light>();
        var key = lights.FirstOrDefault(l => l.name == "key");
        var fill = lights.FirstOrDefault(l => l.name == "fill");
        var wasKey = key != null ? key.intensity : 0f;
        var wasFill = fill != null ? fill.intensity : 0f;

        try
        {
            AnimationMode.BeginSampling();
            AnimationMode.SampleAnimationClip(model, clips["Idle"], 0f);
            AnimationMode.EndSampling();

            var target = new Vector3(0f, 1.05f, 0f);
            var dir = Quaternion.Euler(8f, 180f, 0f) * Vector3.forward;
            camera.transform.position = target - dir * 5.2f;
            camera.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
            // A LIGHT background for these. The rest of the preview sits on near-black, which is
            // the one ground a near-black creature cannot be judged against.
            camera.backgroundColor = new Color(0.78f, 0.78f, 0.80f, 1f);


            // (name, ambient, key, fill). Midday is a hard neutral sun over a bright sky - the case
            // that goes grey. Night is a dim blue sky with a weak moon, near what the game gives at
            // 01:00 with no torch.
            var lighting = new (string name, Color ambient, float key, float fill)[]
            {
                ("day", new Color(0.42f, 0.44f, 0.50f), 1.7f, 0.35f),
                ("night", new Color(0.05f, 0.06f, 0.09f), 0.35f, 0.10f),
            };

            foreach (var (when, ambient, keyI, fillI) in lighting)
            {
                RenderSettings.ambientLight = ambient;
                if (key != null) key.intensity = keyI;
                if (fill != null) fill.intensity = fillI;

                foreach (var (label, colour, _) in WerewolfCandidates.Candidates)
                {
                    // A whole MATERIAL per candidate, not a _Color on one probe. The tint has to
                    // reach the specular map as well as the albedo (ModelSetup.TintedSpecGloss),
                    // and a ladder that only moved _Color came out five identical frames - which
                    // is exactly how this was found.
                    var material = ModelSetup.CandidateMaterial(label, colour, colour);
                    foreach (var r in renderers) r.sharedMaterial = material;
                    var shipped = colour == ModelSetup.DarkColour ? "_SHIPPED" : "";
                    var name = "dark_" + when + "_" + label + "_" + ColorUtility.ToHtmlStringRGB(colour) + shipped + ".png";
                    File.WriteAllBytes(Path.Combine(outDir, name), Shoot(camera));
                    written++;
                }
            }
        }
        finally
        {
            RenderSettings.ambientLight = wasAmbient;
            camera.backgroundColor = wasBackground;
            if (key != null) key.intensity = wasKey;
            if (fill != null) fill.intensity = wasFill;
            for (var i = 0; i < renderers.Length; i++) renderers[i].sharedMaterial = original[i];
        }
        Debug.Log("[Werewolf] " + written + " dark-variant candidate frames");
        return written;
    }

    static int RenderColourComparison(GameObject instance, GameObject model, Camera camera,
                                      Dictionary<string, AnimationClip> clips, string outDir)
    {
        var renderers = instance.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        var original = renderers.Select(r => r.sharedMaterial).ToArray();
        var written = 0;
        try
        {
            AnimationMode.BeginSampling();
            AnimationMode.SampleAnimationClip(model, clips["Idle"], 0f);
            AnimationMode.EndSampling();

            var target = new Vector3(0f, 1.1f, 0f);
            var dir = Quaternion.Euler(12f, 35f, 0f) * Vector3.forward;
            camera.transform.position = target - dir * 6.5f;
            camera.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);

            foreach (var variant in ModelSetup.AvailableVariants)
            {
                var path = ModelSetup.MaterialPath(variant);
                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material == null)
                {
                    Debug.LogWarning("[Werewolf] no material at " + path + " - skipping " + variant);
                    continue;
                }
                foreach (var r in renderers) r.sharedMaterial = material;
                var shipped = variant == ModelSetup.Variants[0] ? "_SHIPPED" : "";
                File.WriteAllBytes(Path.Combine(outDir, "colour_" + variant + shipped + ".png"), Shoot(camera));
                written++;
            }
        }
        finally
        {
            for (var i = 0; i < renderers.Length; i++) renderers[i].sharedMaterial = original[i];
        }
        Debug.Log("[Werewolf] " + written + " colour comparison frames");
        return written;
    }

    /// <summary>
    /// The one check that matters for a Generic rig: do the clip's curve paths resolve on this
    /// hierarchy? A clip whose paths point nowhere plays silently as the bind pose.
    /// </summary>
    static void ReportBindings(GameObject model, AnimationClip clip)
    {
        var bindings = AnimationUtility.GetCurveBindings(clip);
        var resolved = 0;
        string firstMiss = null;
        foreach (var b in bindings)
        {
            if (AnimationUtility.GetAnimatedObject(model, b) != null) resolved++;
            else if (firstMiss == null) firstMiss = b.path + " (" + b.propertyName + ")";
        }
        Debug.Log("[Werewolf] clip " + clip.name + ": " + bindings.Length + " curve bindings, " + resolved
                  + " resolve on the prefab" + (firstMiss != null ? ", first miss: " + firstMiss : "")
                  + (bindings.Length > 0 ? ", e.g. " + bindings[0].path : ""));
    }

    static byte[] Shoot(Camera camera)
    {
        var rt = new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
        var tex = new Texture2D(Size, Size, TextureFormat.RGB24, false);
        try
        {
            camera.targetTexture = rt;
            camera.Render();
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
            tex.Apply(false);
            RenderTexture.active = prev;
            return tex.EncodeToPNG();
        }
        finally
        {
            camera.targetTexture = null;
            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);
            UnityEngine.Object.DestroyImmediate(tex);
        }
    }

    static string ArgValue(string name)
    {
        var args = Environment.GetCommandLineArgs();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name) return args[i + 1];
        }
        return null;
    }
}
