using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using System;
using System.Globalization;
using System.IO;
using System.Linq;

public static partial class UCAF_Listener
{
    // ── Prefab workflow ─────────────────────────────────────────────────

    static UCAFResult CmdCreatePrefab(UCAFCommand cmd)
    {
        var obj = ResolveObject(cmd, out string err);
        if (obj == null) return new UCAFResult { success = false, message = err };
        string assetPath = cmd.GetParam("asset_path", "");
        if (string.IsNullOrEmpty(assetPath))
            return new UCAFResult { success = false, message = "asset_path required" };

        string projectRoot = Path.GetDirectoryName(Application.dataPath);
        string dir = Path.GetDirectoryName(assetPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(Path.Combine(projectRoot, dir));

        bool connect = cmd.GetParam("connect", "true") == "true";
        GameObject prefab = connect
            ? PrefabUtility.SaveAsPrefabAssetAndConnect(obj, assetPath, InteractionMode.AutomatedAction)
            : PrefabUtility.SaveAsPrefabAsset(obj, assetPath);
        if (prefab == null)
            return new UCAFResult { success = false, message = $"Failed to save prefab at {assetPath}" };
        return new UCAFResult { success = true, message = $"Prefab saved: {assetPath}" };
    }

    static UCAFResult CmdApplyPrefab(UCAFCommand cmd)
    {
        var obj = ResolveObject(cmd, out string err);
        if (obj == null) return new UCAFResult { success = false, message = err };
        if (!PrefabUtility.IsPartOfPrefabInstance(obj))
            return new UCAFResult { success = false, message = $"{obj.name} is not a prefab instance" };
        PrefabUtility.ApplyPrefabInstance(obj, InteractionMode.AutomatedAction);
        return new UCAFResult { success = true, message = $"Applied overrides from {obj.name}" };
    }

    static UCAFResult CmdRevertPrefab(UCAFCommand cmd)
    {
        var obj = ResolveObject(cmd, out string err);
        if (obj == null) return new UCAFResult { success = false, message = err };
        if (!PrefabUtility.IsPartOfPrefabInstance(obj))
            return new UCAFResult { success = false, message = $"{obj.name} is not a prefab instance" };
        PrefabUtility.RevertPrefabInstance(obj, InteractionMode.AutomatedAction);
        return new UCAFResult { success = true, message = $"Reverted overrides on {obj.name}" };
    }

    // ── Play mode ───────────────────────────────────────────────────────

    static UCAFResult CmdPlayMode(UCAFCommand cmd)
    {
        bool enter = cmd.GetParam("enter", "true") == "true";
        EditorApplication.isPlaying = enter;
        return new UCAFResult { success = true, message = enter ? "Entered Play Mode." : "Exited Play Mode." };
    }

    // ── Asset import ────────────────────────────────────────────────────

    static UCAFResult CmdImportAsset(UCAFCommand cmd)
    {
        string packagePath = cmd.GetParam("package_path", "");
        bool interactive = cmd.GetParam("interactive", "false") == "true";
        if (!File.Exists(packagePath))
            return new UCAFResult { success = false, message = $"Package not found: {packagePath}" };
        AssetDatabase.ImportPackage(packagePath, interactive);
        return new UCAFResult { success = true, message = $"Importing: {packagePath}" };
    }

    // ── Lighting (basic, legacy + sun) ──────────────────────────────────
    // Note: for HDRP polish (Volumes, exposure, tonemap) use set_field on
    // the Volume's profile asset. This command handles the minimal basics only.

    static UCAFResult CmdSetLighting(UCAFCommand cmd)
    {
        bool fog = cmd.GetParam("fog", "false") == "true";
        RenderSettings.fog = fog;
        if (fog)
        {
            string fogColorHex = cmd.GetParam("fog_color", "#8899AA");
            if (ColorUtility.TryParseHtmlString(fogColorHex, out Color fc))
                RenderSettings.fogColor = fc;
            if (float.TryParse(cmd.GetParam("fog_density", "0.02"),
                               NumberStyles.Float, CultureInfo.InvariantCulture, out float fd))
                RenderSettings.fogDensity = fd;
        }

        string ambientHex = cmd.GetParam("ambient_color", "");
        if (!string.IsNullOrEmpty(ambientHex) && ColorUtility.TryParseHtmlString(ambientHex, out Color ac))
            RenderSettings.ambientLight = ac;

        var sun = RenderSettings.sun;
        if (sun == null)
        {
            var lights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude);
            foreach (var l in lights)
                if (l.type == LightType.Directional) { sun = l; break; }
        }
        if (sun != null)
        {
            string lightColorHex = cmd.GetParam("light_color", "");
            if (!string.IsNullOrEmpty(lightColorHex) && ColorUtility.TryParseHtmlString(lightColorHex, out Color lc))
                sun.color = lc;
            if (float.TryParse(cmd.GetParam("light_intensity", "-1"),
                               NumberStyles.Float, CultureInfo.InvariantCulture, out float li) && li >= 0)
                sun.intensity = li;
            if (float.TryParse(cmd.GetParam("light_rotation_x", "-999"),
                               NumberStyles.Float, CultureInfo.InvariantCulture, out float rx) && rx > -999)
            {
                float.TryParse(cmd.GetParam("light_rotation_y", "0"),
                               NumberStyles.Float, CultureInfo.InvariantCulture, out float ry);
                sun.transform.rotation = Quaternion.Euler(rx, ry, 0);
            }
        }

        return new UCAFResult { success = true, message = "Lighting updated." };
    }

    // ── Generic escape hatch (v3.0) ─────────────────────────────────────

    static UCAFResult CmdExecuteMenuItem(UCAFCommand cmd)
    {
        string menuPath = cmd.GetParam("menu_path", "");
        if (string.IsNullOrEmpty(menuPath))
            return new UCAFResult { success = false, message = "menu_path required" };

        bool ok;
        try { ok = EditorApplication.ExecuteMenuItem(menuPath); }
        catch (Exception ex)
        { return new UCAFResult { success = false, message = $"Menu invocation threw: {ex.Message}" }; }

        return new UCAFResult {
            success = ok,
            message = ok ? $"Executed: {menuPath}" : $"Menu item not found or failed: {menuPath}"
        };
    }

    // ── Alien setup (kept from v1.0 for compatibility) ──────────────────

    static UCAFResult CmdSetupAlien(UCAFCommand cmd)
    {
        try
        {
            AssetDatabase.Refresh();
            var type = System.AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                .FirstOrDefault(t => t.Name == "AlienSetup");
            if (type == null)
                return new UCAFResult { success = false, message = "AlienSetup type not found — still compiling?" };
            var method = type.GetMethod("Run",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (method == null)
                return new UCAFResult { success = false, message = "AlienSetup.Run method not found" };
            method.Invoke(null, null);
            return new UCAFResult { success = true, message = "Alien setup complete." };
        }
        catch (Exception ex)
        {
            return new UCAFResult { success = false, message = $"AlienSetup failed: {ex.Message}" };
        }
    }
}
