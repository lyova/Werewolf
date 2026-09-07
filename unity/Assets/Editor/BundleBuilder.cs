// Builds the mod's AssetBundle. Runs from the Unity menu, or headless from
// tools\build-asset-bundle.ps1 through -executeMethod BundleBuilder.Build.
//
// The bundle holds the four entity prefabs, named exactly as entityclasses.xml asks for after
// the '?' in the Prefab property:
//   #@modfolder(Werewolf):Resources/werewolf.unity3d?Werewolf         (brown)
//   #@modfolder(Werewolf):Resources/werewolf.unity3d?WerewolfDark
//   #@modfolder(Werewolf):Resources/werewolf.unity3d?WerewolfGray
//   #@modfolder(Werewolf):Resources/werewolf.unity3d?WerewolfIce
// Everything else - meshes, avatar, clips, controller, materials, textures - comes along as a
// dependency of the prefabs and is shared between them, so the four cost one set of textures.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class BundleBuilder
{
    const string BundleName = "werewolf.unity3d";

    // Relative to the Unity project folder, which is <mod>\unity
    const string DefaultOutput = "../pack/Resources";

    [MenuItem("Werewolf/Build AssetBundle")]
    public static void BuildFromMenu()
    {
        var output = Path.GetFullPath(Path.Combine(Application.dataPath, "..", DefaultOutput));
        var path = BuildTo(output);
        Debug.Log("[Werewolf] built " + path);
        EditorUtility.RevealInFinder(path);
    }

    // Entry point for batch mode. Reads -outputPath <dir> when it is given.
    public static void Build()
    {
        var exitCode = 0;
        try
        {
            var output = ArgValue("-outputPath");
            if (string.IsNullOrEmpty(output))
            {
                output = Path.GetFullPath(Path.Combine(Application.dataPath, "..", DefaultOutput));
            }
            var path = BuildTo(output);
            Debug.Log("[Werewolf] built " + path);
        }
        catch (Exception e)
        {
            Debug.LogError("[Werewolf] bundle build failed: " + e);
            exitCode = 1;
        }

        if (Application.isBatchMode) EditorApplication.Exit(exitCode);
    }

    static string BuildTo(string outputDir)
    {
        var prefabs = ModelSetup.Variants.Select(ModelSetup.PrefabPath).ToArray();
        var missing = prefabs.Where(p => AssetDatabase.LoadAssetAtPath<GameObject>(p) == null).ToArray();
        if (missing.Length > 0)
        {
            throw new FileNotFoundException(
                "no prefab at " + string.Join(", ", missing) + " - run tools\\build-asset-bundle.ps1 -Mod Werewolf -Prefab first");
        }

        Directory.CreateDirectory(outputDir);

        // The voice clips have to be listed EXPLICITLY, unlike the meshes and textures. Those come
        // along as dependencies of the prefabs and never need naming; a clip is referenced by name
        // from sounds.xml instead, and a bundle dependency has no name to ask for. Discovered
        // rather than hard-coded, because how many clips each role has is not the build's business.
        var audio = Directory.Exists(ModelSetup.AudioDir)
            ? AssetDatabase.FindAssets("t:AudioClip", new[] { ModelSetup.AudioDir })
                .Select(AssetDatabase.GUIDToAssetPath)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : new string[0];

        var build = new AssetBundleBuild
        {
            assetBundleName = BundleName,
            assetNames = prefabs.Concat(audio).ToArray(),
        };

        var manifest = BuildPipeline.BuildAssetBundles(
            outputDir,
            new[] { build },
            BuildAssetBundleOptions.ChunkBasedCompression,
            BuildTarget.StandaloneWindows64);
        if (manifest == null) throw new Exception("BuildPipeline returned no manifest");

        // Unity leaves a .manifest per bundle plus a manifest bundle named after the output folder.
        // The game has no use for either; this folder holds nothing but the bundle.
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { BundleName, ".gitkeep" };
        foreach (var junk in Directory.GetFiles(outputDir).Where(f => !keep.Contains(Path.GetFileName(f))))
        {
            File.Delete(junk);
        }

        var bundle = Path.Combine(outputDir, BundleName);
        if (!File.Exists(bundle)) throw new FileNotFoundException("expected the bundle at " + bundle);

        LogContents(bundle);
        return bundle;
    }

    static void LogContents(string bundlePath)
    {
        var loaded = AssetBundle.LoadFromFile(bundlePath);
        if (loaded == null)
        {
            Debug.LogWarning("[Werewolf] the bundle was written but could not be loaded back");
            return;
        }
        var names = loaded.GetAllAssetNames();
        Debug.Log("[Werewolf] bundle holds " + names.Length + " assets: " + string.Join(", ", names));
        foreach (var variant in ModelSetup.Variants)
        {
            var go = loaded.LoadAsset<GameObject>(ModelSetup.PrefabName(variant));
            Debug.Log("[Werewolf] LoadAsset(\"" + ModelSetup.PrefabName(variant) + "\") -> " + (go != null ? "ok" : "NULL"));
        }
        loaded.Unload(true);
        Debug.Log("[Werewolf] bundle size " + (new FileInfo(bundlePath).Length / 1024 / 1024) + " MB");
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
