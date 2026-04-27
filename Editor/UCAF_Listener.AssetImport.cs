using UnityEngine;
using UnityEditor;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

public static partial class UCAF_Listener
{
    // ── Asset Import Settings API (v4.2 Phase A, FR-142 to FR-146) ────────

    static UCAFResult CmdSetTextureImport(UCAFCommand cmd)
    {
        string assetPath = cmd.GetParam("asset_path", "");
        if (string.IsNullOrEmpty(assetPath))
            return new UCAFResult { success = false, message = "asset_path required" };
        var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
        if (importer == null)
            return new UCAFResult { success = false, message = $"No TextureImporter at '{assetPath}'. Check path and ensure asset exists." };

        bool changed = false;

        string textureType = cmd.GetParam("textureType", "");
        if (!string.IsNullOrEmpty(textureType))
        {
            if (Enum.TryParse<TextureImporterType>(textureType, true, out var tt))
            { importer.textureType = tt; changed = true; }
            else return new UCAFResult { success = false, message = $"Unknown textureType '{textureType}'. Valid: {string.Join(", ", Enum.GetNames(typeof(TextureImporterType)))}" };
        }

        string compression = cmd.GetParam("compression", "");
        if (!string.IsNullOrEmpty(compression))
        {
            if (Enum.TryParse<TextureImporterCompression>(compression, true, out var tc))
            { importer.textureCompression = tc; changed = true; }
            else return new UCAFResult { success = false, message = $"Unknown compression '{compression}'. Valid: {string.Join(", ", Enum.GetNames(typeof(TextureImporterCompression)))}" };
        }

        string srgb = cmd.GetParam("srgb", "");
        if (!string.IsNullOrEmpty(srgb)) { importer.sRGBTexture = srgb == "true"; changed = true; }

        string mipmap = cmd.GetParam("mipmap", "");
        if (!string.IsNullOrEmpty(mipmap)) { importer.mipmapEnabled = mipmap == "true"; changed = true; }

        string wrapMode = cmd.GetParam("wrapMode", "");
        if (!string.IsNullOrEmpty(wrapMode))
        {
            if (Enum.TryParse<TextureWrapMode>(wrapMode, true, out var wm))
            { importer.wrapMode = wm; changed = true; }
        }

        string filterMode = cmd.GetParam("filterMode", "");
        if (!string.IsNullOrEmpty(filterMode))
        {
            if (Enum.TryParse<FilterMode>(filterMode, true, out var fm))
            { importer.filterMode = fm; changed = true; }
        }

        string maxSizeStr = cmd.GetParam("maxSize", "");
        if (!string.IsNullOrEmpty(maxSizeStr) && int.TryParse(maxSizeStr, out int maxSize))
        { importer.maxTextureSize = maxSize; changed = true; }

        string readable = cmd.GetParam("readable", "");
        if (!string.IsNullOrEmpty(readable)) { importer.isReadable = readable == "true"; changed = true; }

        if (!changed)
            return new UCAFResult { success = false, message = "No recognized texture import parameters provided. Valid: textureType, compression, srgb, mipmap, wrapMode, filterMode, maxSize, readable" };

        importer.SaveAndReimport();
        return new UCAFResult { success = true, message = $"Texture import settings updated: {assetPath}" };
    }

    static UCAFResult CmdSetModelImport(UCAFCommand cmd)
    {
        string assetPath = cmd.GetParam("asset_path", "");
        if (string.IsNullOrEmpty(assetPath))
            return new UCAFResult { success = false, message = "asset_path required" };
        var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
        if (importer == null)
            return new UCAFResult { success = false, message = $"No ModelImporter at '{assetPath}'. Only FBX/OBJ/DAE model files are supported." };

        bool changed = false;

        string animationType = cmd.GetParam("animationType", "");
        if (!string.IsNullOrEmpty(animationType))
        {
            if (Enum.TryParse<ModelImporterAnimationType>(animationType, true, out var at))
            { importer.animationType = at; changed = true; }
            else return new UCAFResult { success = false, message = $"Unknown animationType '{animationType}'. Valid: None, Legacy, Generic, Humanoid" };
        }

        string importMaterials = cmd.GetParam("importMaterials", "");
        if (!string.IsNullOrEmpty(importMaterials))
        {
            importer.materialImportMode = importMaterials == "true"
                ? ModelImporterMaterialImportMode.ImportViaMaterialDescription
                : ModelImporterMaterialImportMode.None;
            changed = true;
        }

        string meshCompression = cmd.GetParam("meshCompression", "");
        if (!string.IsNullOrEmpty(meshCompression))
        {
            if (Enum.TryParse<ModelImporterMeshCompression>(meshCompression, true, out var mc))
            { importer.meshCompression = mc; changed = true; }
        }

        string readWriteEnabled = cmd.GetParam("readWriteEnabled", "");
        if (!string.IsNullOrEmpty(readWriteEnabled)) { importer.isReadable = readWriteEnabled == "true"; changed = true; }

        string optimizeGameObjects = cmd.GetParam("optimizeGameObjects", "");
        if (!string.IsNullOrEmpty(optimizeGameObjects)) { importer.optimizeGameObjects = optimizeGameObjects == "true"; changed = true; }

        if (!changed)
            return new UCAFResult { success = false, message = "No recognized model import parameters provided. Valid: animationType, importMaterials, meshCompression, readWriteEnabled, optimizeGameObjects" };

        importer.SaveAndReimport();
        return new UCAFResult { success = true, message = $"Model import settings updated: {assetPath}" };
    }

    // Key fix for Session 1 loopTime bug (FR-144).
    static UCAFResult CmdSetAnimationClipImport(UCAFCommand cmd)
    {
        string assetPath = cmd.GetParam("asset_path", "");
        if (string.IsNullOrEmpty(assetPath))
            return new UCAFResult { success = false, message = "asset_path required" };
        var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
        if (importer == null)
            return new UCAFResult { success = false, message = $"No ModelImporter at '{assetPath}'" };

        string clipName = cmd.GetParam("clip_name", "");

        ModelImporterClipAnimation[] clips = importer.clipAnimations;
        if (clips == null || clips.Length == 0)
            clips = importer.defaultClipAnimations;
        if (clips == null || clips.Length == 0)
            return new UCAFResult { success = false, message = $"No animation clips in {assetPath}" };

        bool found = false;
        int updatedCount = 0;

        for (int i = 0; i < clips.Length; i++)
        {
            bool matches = string.IsNullOrEmpty(clipName) || clips[i].name == clipName;
            if (!matches) continue;
            found = true;

            string loopTimeStr = cmd.GetParam("loopTime", "");
            if (!string.IsNullOrEmpty(loopTimeStr)) clips[i].loopTime = loopTimeStr == "true";

            string loopPoseStr = cmd.GetParam("loopPose", "");
            if (!string.IsNullOrEmpty(loopPoseStr)) clips[i].loopPose = loopPoseStr == "true";

            string cycleOffsetStr = cmd.GetParam("cycleOffset", "");
            if (!string.IsNullOrEmpty(cycleOffsetStr) &&
                float.TryParse(cycleOffsetStr, NumberStyles.Float, CultureInfo.InvariantCulture, out float co))
                clips[i].cycleOffset = co;

            updatedCount++;
            if (!string.IsNullOrEmpty(clipName)) break;
        }

        if (!found)
        {
            var available = string.Join(", ", Array.ConvertAll(clips, c => c.name));
            return new UCAFResult { success = false, message = $"No clip named '{clipName}' in {assetPath}. Available: {available}" };
        }

        importer.clipAnimations = clips;
        importer.SaveAndReimport();
        return new UCAFResult { success = true, message = $"Animation clip import updated: {updatedCount} clip(s) in {assetPath}" };
    }

    static UCAFResult CmdSetAudioImport(UCAFCommand cmd)
    {
        string assetPath = cmd.GetParam("asset_path", "");
        if (string.IsNullOrEmpty(assetPath))
            return new UCAFResult { success = false, message = "asset_path required" };
        var importer = AssetImporter.GetAtPath(assetPath) as AudioImporter;
        if (importer == null)
            return new UCAFResult { success = false, message = $"No AudioImporter at '{assetPath}'" };

        bool changed = false;
        AudioImporterSampleSettings settings = importer.defaultSampleSettings;

        string loadType = cmd.GetParam("loadType", "");
        if (!string.IsNullOrEmpty(loadType))
        {
            if (Enum.TryParse<AudioClipLoadType>(loadType, true, out var lt))
            { settings.loadType = lt; changed = true; }
            else return new UCAFResult { success = false, message = $"Unknown loadType '{loadType}'. Valid: DecompressOnLoad, CompressedInMemory, Streaming" };
        }

        string compressionFormat = cmd.GetParam("compressionFormat", "");
        if (!string.IsNullOrEmpty(compressionFormat))
        {
            if (Enum.TryParse<AudioCompressionFormat>(compressionFormat, true, out var cf))
            { settings.compressionFormat = cf; changed = true; }
        }

        string qualityStr = cmd.GetParam("quality", "");
        if (!string.IsNullOrEmpty(qualityStr) && float.TryParse(qualityStr, NumberStyles.Float, CultureInfo.InvariantCulture, out float quality))
        { settings.quality = quality; changed = true; }

        string forceToMono = cmd.GetParam("forceToMono", "");
        if (!string.IsNullOrEmpty(forceToMono)) { importer.forceToMono = forceToMono == "true"; changed = true; }

        string loadInBackground = cmd.GetParam("loadInBackground", "");
        if (!string.IsNullOrEmpty(loadInBackground)) { importer.loadInBackground = loadInBackground == "true"; changed = true; }

        if (!changed)
            return new UCAFResult { success = false, message = "No recognized audio import parameters provided" };

        importer.defaultSampleSettings = settings;
        importer.SaveAndReimport();
        return new UCAFResult { success = true, message = $"Audio import settings updated: {assetPath}" };
    }

    // Validates assets against rules defined in ucaf_workspace/import_rules.json.
    // Rule format: { "rules": [{ "glob": "Assets/Animations/Mixamo/**", "checks": [{"key":"animationType","value":"Humanoid"},{"key":"loopTime","value":"true"}] }] }
    static UCAFResult CmdValidateImports(UCAFCommand cmd)
    {
        string rulesPath = Path.Combine(WorkspacePath, "import_rules.json");
        if (!File.Exists(rulesPath))
            return new UCAFResult { success = false, message = $"import_rules.json not found at {rulesPath}. Create it to define validation rules." };

        UCAFImportRules rules;
        try { rules = JsonUtility.FromJson<UCAFImportRules>(File.ReadAllText(rulesPath)); }
        catch (Exception ex) { return new UCAFResult { success = false, message = $"Failed to parse import_rules.json: {ex.Message}" }; }

        if (rules == null || rules.rules == null || rules.rules.Count == 0)
            return new UCAFResult { success = true, message = "No rules defined in import_rules.json" };

        var result = new UCAFImportValidationResult();

        foreach (var rule in rules.rules)
        {
            if (string.IsNullOrEmpty(rule.glob) || rule.checks == null || rule.checks.Count == 0) continue;

            // Extract search folder from glob (everything up to the first wildcard segment)
            string searchFolder = "Assets";
            string[] parts = rule.glob.Replace('\\', '/').Split('/');
            var folderParts = new List<string>();
            foreach (var p in parts)
            {
                if (p.Contains("*") || p.Contains("?")) break;
                folderParts.Add(p);
            }
            if (folderParts.Count > 0) searchFolder = string.Join("/", folderParts);

            string extFilter = "";
            string lastPart = parts[parts.Length - 1];
            if (lastPart.StartsWith("*.")) extFilter = lastPart.Substring(1); // e.g. ".fbx"

            string[] guids = AssetDatabase.FindAssets("", new[] { searchFolder });
            foreach (var guid in guids)
            {
                string ap = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(extFilter) &&
                    !ap.EndsWith(extFilter, StringComparison.OrdinalIgnoreCase)) continue;

                result.total_checked++;
                var importer = AssetImporter.GetAtPath(ap);

                foreach (var check in rule.checks)
                {
                    string violation = null;
                    if (importer is ModelImporter mi)
                        violation = ValidateModelImporterRule(mi, check.key, check.value);
                    else if (importer is TextureImporter ti)
                        violation = ValidateTextureImporterRule(ti, check.key, check.value);
                    else if (importer is AudioImporter ai)
                        violation = ValidateAudioImporterRule(ai, check.key, check.value);

                    if (violation != null)
                        result.issues.Add(new UCAFImportIssue {
                            asset_path = ap,
                            rule       = check.key,
                            expected   = check.value,
                            actual     = violation
                        });
                }
            }
        }

        result.issues_found = result.issues.Count;
        string msg = result.issues_found == 0
            ? $"All {result.total_checked} assets comply with import rules"
            : $"{result.issues_found} violation(s) found across {result.total_checked} assets";

        return new UCAFResult {
            success   = result.issues_found == 0,
            message   = msg,
            data_json = JsonUtility.ToJson(result)
        };
    }

    static string ValidateModelImporterRule(ModelImporter mi, string key, string expected)
    {
        switch (key.ToLowerInvariant())
        {
            case "animationtype":
            {
                string actual = mi.animationType.ToString();
                return actual.Equals(expected, StringComparison.OrdinalIgnoreCase) ? null : actual;
            }
            case "looptime":
            {
                bool expLoop = expected == "true";
                var clips = mi.clipAnimations;
                if (clips == null || clips.Length == 0) clips = mi.defaultClipAnimations;
                foreach (var c in clips)
                    if (c.loopTime != expLoop) return c.loopTime.ToString().ToLowerInvariant();
                return null;
            }
            case "importmaterials":
            {
                bool expImport = expected == "true";
                bool actualImport = mi.materialImportMode != ModelImporterMaterialImportMode.None;
                return actualImport == expImport ? null : actualImport.ToString().ToLowerInvariant();
            }
            default: return null;
        }
    }

    static string ValidateTextureImporterRule(TextureImporter ti, string key, string expected)
    {
        switch (key.ToLowerInvariant())
        {
            case "texturetype":
            {
                string actual = ti.textureType.ToString();
                return actual.Equals(expected, StringComparison.OrdinalIgnoreCase) ? null : actual;
            }
            case "srgb":
            {
                bool expSrgb = expected == "true";
                return ti.sRGBTexture == expSrgb ? null : ti.sRGBTexture.ToString().ToLowerInvariant();
            }
            case "maxsize":
            {
                if (int.TryParse(expected, out int exp) && ti.maxTextureSize == exp) return null;
                return ti.maxTextureSize.ToString();
            }
            default: return null;
        }
    }

    static string ValidateAudioImporterRule(AudioImporter ai, string key, string expected)
    {
        switch (key.ToLowerInvariant())
        {
            case "loadtype":
            {
                string actual = ai.defaultSampleSettings.loadType.ToString();
                return actual.Equals(expected, StringComparison.OrdinalIgnoreCase) ? null : actual;
            }
            case "forcetomono":
            {
                bool expMono = expected == "true";
                return ai.forceToMono == expMono ? null : ai.forceToMono.ToString().ToLowerInvariant();
            }
            default: return null;
        }
    }
}
