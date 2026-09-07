// Builds a scene for looking at the creature by hand, and saves it so the Unity editor opens
// straight into it:
//
//   Unity.exe -projectPath mods\Werewolf\unity -executeMethod WerewolfInspect.Build -quit
//   then open Assets/Werewolf/Inspect.unity
//
// It exists because judging the colour off a batch render turned out to be worthless: the preview
// scene had 0.45 flat ambient plus 1.7 of directional light, which lifts a near-black pelt to mid
// grey on its own. The three quads are the point of this scene - they show the TEXTURES as data,
// unlit and unshaded, next to the same textures shaded on the model. If the model looks lighter
// than its own albedo quad, the difference is lighting, not the texture.

using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

public static class WerewolfInspect
{
    const string ScenePath = ModelSetup.Dir + "/Inspect.unity";

    [MenuItem("Werewolf/Build inspect scene")]
    public static void Build()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // Outdoor-ish, not a studio. Keep this in step with WerewolfPreview.
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.08f, 0.08f, 0.1f);
        RenderSettings.ambientIntensity = 1f;

        var key = new GameObject("sun").AddComponent<Light>();
        key.type = LightType.Directional;
        key.intensity = 1f;
        key.shadows = LightShadows.Soft;
        key.transform.rotation = Quaternion.Euler(45f, -30f, 0f);

        // The creature, one per colour, so the four can be compared in one look rather than from
        // memory. Only the first one ships; the rest are here to answer "is this really the
        // darkest".
        var x = -4f;
        foreach (var variant in ModelSetup.AvailableVariants)
        {
            var prefabPath = ModelSetup.PrefabPath(variant);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                // Only the shipped variants have a prefab. Build a bare model for the others so
                // the colour can still be seen.
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(ModelSetup.ModelPath);
                var mat = AssetDatabase.LoadAssetAtPath<Material>(ModelSetup.MaterialPath(variant));
                if (model == null || mat == null) continue;
                var bare = (GameObject)PrefabUtility.InstantiatePrefab(model);
                bare.name = variant + " (material only)";
                bare.transform.position = new Vector3(x, 0f, 0f);
                foreach (var r in bare.GetComponentsInChildren<SkinnedMeshRenderer>(true)) r.sharedMaterial = mat;
                x += 2.6f;
                continue;
            }

            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            go.name = variant + (variant == ModelSetup.Variants[0] ? " (SHIPPED)" : "");
            go.transform.position = new Vector3(x, 0f, 0f);
            x += 2.6f;
        }

        // The textures as data: unlit quads, no shading, no lighting. What is on these IS what is
        // in the bundle.
        var unlit = Shader.Find("Unlit/Texture");
        if (unlit != null)
        {
            var shipped = ModelSetup.Variants[0].ToLower();
            var sheets = new[]
            {
                ("albedo " + shipped, ModelSetup.TexDir + "/werewolf_albedo_" + shipped + ".png"),
                ("normal",            ModelSetup.TexDir + "/werewolf_normal.png"),
                ("specgloss",         ModelSetup.TexDir + "/werewolf_specgloss.png"),
                ("emission " + shipped, ModelSetup.TexDir + "/werewolf_emission_" + shipped + ".png"),
            };

            var qx = -4f;
            foreach (var (label, path) in sheets)
            {
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (tex == null) continue;
                var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                quad.name = "texture: " + label;
                quad.transform.position = new Vector3(qx, 1.2f, 3f);
                quad.transform.localScale = Vector3.one * 2.2f;
                var m = new Material(unlit) { name = "unlit " + label };
                m.mainTexture = tex;
                quad.GetComponent<MeshRenderer>().sharedMaterial = m;
                qx += 2.6f;
            }
        }

        var camera = new GameObject("view").AddComponent<Camera>();
        camera.transform.position = new Vector3(0.6f, 1.6f, -5.5f);
        camera.transform.rotation = Quaternion.Euler(6f, 0f, 0f);
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.16f, 0.17f, 0.2f, 1f);

        Directory.CreateDirectory(ModelSetup.Dir);
        EditorSceneManager.SaveScene(scene, ScenePath);
        Debug.Log("[Werewolf] inspect scene at " + ScenePath
                  + " - four colours on the left to right, the raw textures behind them");

        if (Application.isBatchMode) EditorApplication.Exit(0);
    }
}
