using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Builds a scene with the Dark variant in every candidate colour, side by side, so the colour can
/// be chosen by looking at the creature and turning it round rather than by arguing over a render.
///
///     Werewolf menu -> Dark candidates scene        (or ModelSetup-style batch: WerewolfCandidates.Build)
///
/// It opens Assets/Werewolf/Scenes/DarkCandidates.unity with five werewolves in a row on a ground
/// plane under a daylight sun, each with its own material, each labelled. Orbit the scene view to
/// look at them; the Sun object in the scene is a plain directional light, so dragging its rotation
/// or intensity is how "what about at dusk" gets answered.
///
/// WHY A SCENE AND NOT MORE RENDERS. Both, really - the batch renders in WerewolfPreview answer
/// "which is darkest" from a fixed angle, and this answers everything else. A pelt this dark
/// changes character completely with the angle between the light and the eye, because most of what
/// reaches the camera is a broad specular sheen rather than diffuse colour (see
/// ModelSetup.TintedSpecGloss), and no single still frame represents that honestly.
///
/// THE COLOURS ARE THE POINT, so they live in one array below. Each candidate gets its own material
/// asset, named after the label, built from the same textures the shipping material uses - the
/// albedo through _Color and the specular map through a tinted copy. Whichever wins goes into
/// ModelSetup.DarkColour and ships.
/// </summary>
public static class WerewolfCandidates
{
    /// <summary>
    /// The ladder. `current` is what ships today: white, meaning the seller's texture untouched.
    ///
    /// They are written the way a colour picker would show them - as an sRGB tint - and Unity
    /// converts to linear on the way into the shader, the same for the albedo and (through
    /// ModelSetup.TintedSpecGloss) for the specular map. That conversion bites hard at the dark
    /// end: a green channel of 0.30 in the picker is 0.07 in the light, so the ladder gets very
    /// red very quickly. That is deliberate - the whole complaint is that neutral grey has nothing
    /// for the light to tint.
    /// </summary>
    public static readonly (string label, Color colour, string note)[] Candidates =
    {
        ("current",  new Color(1.00f, 1.00f, 1.00f), "the seller's texture, untouched - what ships today"),
        ("ember",    new Color(0.78f, 0.42f, 0.35f), "warmed, still reads as a dark animal rather than a red one"),
        ("rust",     new Color(0.64f, 0.27f, 0.22f), "clearly red-brown in sunlight, near black in shadow"),
        ("oxblood",  new Color(0.55f, 0.13f, 0.11f), "dark red where the light hits, black everywhere else"),
        ("charcoal", new Color(0.36f, 0.07f, 0.06f), "black with a red bite only in the highlights"),
    };

    const string SceneDir = "Assets/Werewolf/Scenes";
    const string ScenePath = SceneDir + "/DarkCandidates.unity";

    /// <summary>Spacing between the five, in metres. They are about a metre wide at the claws.</summary>
    const float Spacing = 2.6f;

    [MenuItem("Werewolf/Dark candidates scene")]
    public static void Build()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ModelSetup.PrefabPath(ModelSetup.Variants[0]));
        if (prefab == null)
        {
            Debug.LogError("[Werewolf] no prefab yet - run ModelSetup.All (build.ps1 -Bundle) first");
            return;
        }

        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

        // Daylight, because noon is the case that fails. The default skybox supplies the ambient,
        // which is what the game does outdoors and what a flat grey ambient does not.
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Skybox;
        RenderSettings.ambientIntensity = 1f;

        var sun = Object.FindObjectOfType<Light>();
        if (sun != null)
        {
            sun.name = "Sun";
            sun.type = LightType.Directional;
            sun.intensity = 1.4f;
            sun.color = new Color(1f, 0.96f, 0.9f);
            sun.transform.rotation = Quaternion.Euler(52f, -35f, 0f);
            sun.shadows = LightShadows.Soft;
        }

        // Something to stand on and cast a shadow onto, so the silhouette is readable. A quad
        // rather than a Plane primitive: no extra material asset, and it takes the default one.
        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.name = "Ground";
        ground.transform.localScale = new Vector3(4f, 1f, 4f);

        var camera = Object.FindObjectOfType<Camera>();
        if (camera != null)
        {
            var centre = new Vector3(0f, 1.1f, 0f);
            var dir = Quaternion.Euler(8f, 180f, 0f) * Vector3.forward;
            camera.transform.position = centre - dir * 9f;
            camera.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
            camera.fieldOfView = 45f;
        }

        var first = -(Candidates.Length - 1) * 0.5f * Spacing;
        for (var i = 0; i < Candidates.Length; i++)
        {
            var (label, colour, note) = Candidates[i];
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            instance.name = string.Format("{0} - {1}", label, ColorUtility.ToHtmlStringRGB(colour));
            instance.transform.position = new Vector3(first + i * Spacing, 0f, 0f);
            // Facing the default camera. The pale skin that reads as grey - muzzle, chest, inner
            // limbs - is all on the front, so a rear three-quarter would hide the question.
            instance.transform.rotation = Quaternion.Euler(0f, 180f, 0f);

            // Same colour for both maps: this ladder varies plain brightness, and the specular is
            // half of what "brightness" means on this creature (ModelSetup.TintedSpecGloss).
            var material = ModelSetup.CandidateMaterial(label, colour, colour);
            foreach (var skin in instance.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                skin.sharedMaterial = material;
                skin.updateWhenOffscreen = true;
            }
            var lod = instance.GetComponentInChildren<LODGroup>();
            if (lod != null) lod.ForceLOD(0);

            var text = new GameObject("label " + label).AddComponent<TextMesh>();
            text.transform.SetParent(instance.transform, false);
            text.transform.localPosition = new Vector3(0f, 2.6f, 0f);
            text.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            text.text = label + "\n#" + ColorUtility.ToHtmlStringRGB(colour) + "\n" + note.Replace(" - ", "\n");
            text.characterSize = 0.09f;
            text.fontSize = 64;
            text.anchor = TextAnchor.LowerCenter;
            text.alignment = TextAlignment.Center;
            text.color = Color.black;
        }

        Directory.CreateDirectory(SceneDir);
        AssetDatabase.Refresh();
        EditorSceneManager.SaveScene(scene, ScenePath);
        AssetDatabase.SaveAssets();
        Debug.Log("[Werewolf] " + Candidates.Length + " candidates in " + ScenePath
                  + " - open it, orbit, and put the winner in ModelSetup.DarkColour");
        if (Application.isBatchMode) EditorApplication.Exit(0);
    }
}
