// Diagnostics for the import: prints the imported hierarchy of the model FBX and of each animation
// FBX, and the distinct root path segments the clips animate. Batch:
//   Unity.exe -batchmode -nographics -projectPath mods\Werewolf\unity -executeMethod WerewolfDebug.PrintImport -logFile ... -quit

using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class WerewolfDebug
{
    public static void PrintImport()
    {
        try
        {
            Print(ModelSetup.ModelPath);
            foreach (var path in Directory.GetFiles(ModelSetup.AnimDir, "*.fbx")) Print(path.Replace('\\', '/'));
        }
        finally
        {
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
    }

    static void Print(string path)
    {
        var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
        Debug.Log("[Werewolf] === " + path + (go == null ? " (no GameObject)" : ""));
        if (go != null)
        {
            var lines = new List<string>();
            Walk(go.transform, 0, lines, 3);
            foreach (var l in lines) Debug.Log("[Werewolf]   " + l);
        }
        foreach (var clip in AssetDatabase.LoadAllAssetRepresentationsAtPath(path).OfType<AnimationClip>())
        {
            if (clip.name.StartsWith("__preview__")) continue;
            var bindings = AnimationUtility.GetCurveBindings(clip);
            var roots = bindings.Select(b => b.path.Split('/')[0]).Distinct().Take(6).ToArray();
            var props = bindings.Where(b => b.path == "").Select(b => b.propertyName).Distinct().Take(12).ToArray();
            Debug.Log("[Werewolf]   clip " + clip.name + ": " + bindings.Length + " bindings; top segments: "
                      + string.Join(", ", roots.Select(r => "'" + r + "'")) + "; root-level props: " + string.Join(", ", props));
            break;
        }
    }

    static void Walk(Transform t, int depth, List<string> into, int limit)
    {
        var comps = t.GetComponents<Component>().Where(c => c != null && !(c is Transform)).Select(c => c.GetType().Name);
        into.Add(new string(' ', depth * 2) + t.name + (comps.Any() ? "  [" + string.Join(", ", comps) + "]" : "")
                 + "  pos=" + t.localPosition.ToString("0.00") + " rot=" + t.localEulerAngles.ToString("0") + " scale=" + t.localScale.ToString("0.00"));
        if (depth >= limit) { if (t.childCount > 0) into.Add(new string(' ', (depth + 1) * 2) + "... " + t.childCount); return; }
        for (var i = 0; i < t.childCount; i++) Walk(t.GetChild(i), depth + 1, into, limit);
    }
}
