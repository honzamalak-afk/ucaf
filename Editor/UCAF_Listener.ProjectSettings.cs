using UnityEngine;
using UnityEditor;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

public static partial class UCAF_Listener
{
    // ── Project Settings structured API (v4.2 Phase A, FR-127 to FR-133) ──
    //
    // All commands use SerializedObject on the relevant ProjectSettings asset
    // so changes are persisted to disk (not just runtime state).

    // ── Tags ────────────────────────────────────────────────────────────────

    static UCAFResult CmdAddTag(UCAFCommand cmd)
    {
        string tag = cmd.GetParam("name", "");
        if (string.IsNullOrEmpty(tag))
            return new UCAFResult { success = false, message = "name required" };

        var tagManager = new SerializedObject(
            AssetDatabase.LoadMainAssetAtPath("ProjectSettings/TagManager.asset"));
        var tagsProperty = tagManager.FindProperty("tags");
        if (tagsProperty == null)
            return new UCAFResult { success = false, message = "Could not find tags property in TagManager" };

        for (int i = 0; i < tagsProperty.arraySize; i++)
        {
            if (tagsProperty.GetArrayElementAtIndex(i).stringValue == tag)
                return new UCAFResult { success = true, message = $"Tag '{tag}' already exists", data_json = "{\"skipped\":true}" };
        }

        tagsProperty.arraySize++;
        tagsProperty.GetArrayElementAtIndex(tagsProperty.arraySize - 1).stringValue = tag;
        tagManager.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.SaveAssets();
        return new UCAFResult { success = true, message = $"Tag added: '{tag}'" };
    }

    // ── Layers ──────────────────────────────────────────────────────────────

    static UCAFResult CmdAddLayer(UCAFCommand cmd)
    {
        string name = cmd.GetParam("name", "");
        if (string.IsNullOrEmpty(name))
            return new UCAFResult { success = false, message = "name required" };

        int requestedIndex = int.TryParse(cmd.GetParam("index", "-1"), out int ri) ? ri : -1;

        var tagManager = new SerializedObject(
            AssetDatabase.LoadMainAssetAtPath("ProjectSettings/TagManager.asset"));
        var layers = tagManager.FindProperty("layers");
        if (layers == null)
            return new UCAFResult { success = false, message = "Could not find layers property in TagManager" };

        // Check if already exists
        for (int i = 0; i < layers.arraySize; i++)
        {
            if (layers.GetArrayElementAtIndex(i).stringValue == name)
                return new UCAFResult { success = true, message = $"Layer '{name}' already exists at index {i}", data_json = $"{{\"skipped\":true,\"index\":{i}}}" };
        }

        // Find free slot (user layers start at index 8)
        int freeSlot = -1;
        if (requestedIndex >= 8 && requestedIndex < layers.arraySize)
        {
            if (string.IsNullOrEmpty(layers.GetArrayElementAtIndex(requestedIndex).stringValue))
                freeSlot = requestedIndex;
            else
                return new UCAFResult { success = false, message = $"Layer slot {requestedIndex} is already occupied by '{layers.GetArrayElementAtIndex(requestedIndex).stringValue}'" };
        }
        else
        {
            for (int i = 8; i < layers.arraySize; i++)
            {
                if (string.IsNullOrEmpty(layers.GetArrayElementAtIndex(i).stringValue))
                { freeSlot = i; break; }
            }
        }

        if (freeSlot < 0)
            return new UCAFResult { success = false, message = "No free layer slots (max 32 total, 24 user layers)" };

        layers.GetArrayElementAtIndex(freeSlot).stringValue = name;
        tagManager.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.SaveAssets();
        return new UCAFResult { success = true, message = $"Layer '{name}' added at index {freeSlot}", data_json = $"{{\"index\":{freeSlot}}}" };
    }

    // ── Physics collision matrix ─────────────────────────────────────────────

    static UCAFResult CmdSetPhysicsCollision(UCAFCommand cmd)
        => SetLayerCollisionInternal(cmd, physics2D: false);

    static UCAFResult CmdSetPhysics2DCollision(UCAFCommand cmd)
        => SetLayerCollisionInternal(cmd, physics2D: true);

    static UCAFResult SetLayerCollisionInternal(UCAFCommand cmd, bool physics2D)
    {
        string layerAStr = cmd.GetParam("layer_a", "");
        string layerBStr = cmd.GetParam("layer_b", "");
        bool collide     = cmd.GetParam("collide", "true") == "true";

        if (string.IsNullOrEmpty(layerAStr) || string.IsNullOrEmpty(layerBStr))
            return new UCAFResult { success = false, message = "layer_a and layer_b required (name or index)" };

        int la = ResolveLayerIndex(layerAStr);
        int lb = ResolveLayerIndex(layerBStr);
        if (la < 0) return new UCAFResult { success = false, message = $"Unknown layer: '{layerAStr}'" };
        if (lb < 0) return new UCAFResult { success = false, message = $"Unknown layer: '{layerBStr}'" };

        string settingsAsset = physics2D
            ? "ProjectSettings/Physics2DSettings.asset"
            : "ProjectSettings/DynamicsManager.asset";
        string matrixProp = physics2D
            ? "m_LayerCollisionMatrix"
            : "m_LayerCollisionMatrix";

        var so = new SerializedObject(AssetDatabase.LoadMainAssetAtPath(settingsAsset));
        var matrix = so.FindProperty(matrixProp);
        if (matrix == null || !matrix.isArray)
            return new UCAFResult { success = false, message = $"Could not find collision matrix in {settingsAsset}" };

        // Matrix: element[i] has bit j set means layers i and j DO collide.
        // We ensure both (i,j) and (j,i) are updated for symmetry.
        SetMatrixBit(matrix, la, lb, collide);
        SetMatrixBit(matrix, lb, la, collide);

        so.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.SaveAssets();

        string api = physics2D ? "Physics2D" : "Physics";
        string outcome = collide ? "will collide" : "will ignore collision";
        return new UCAFResult { success = true, message = $"{api}: layers '{layerAStr}' and '{layerBStr}' {outcome}" };
    }

    static int ResolveLayerIndex(string nameOrIndex)
    {
        if (int.TryParse(nameOrIndex, out int i) && i >= 0 && i < 32) return i;
        int n = LayerMask.NameToLayer(nameOrIndex);
        return n; // -1 if not found
    }

    static void SetMatrixBit(SerializedProperty matrix, int row, int col, bool value)
    {
        if (row >= matrix.arraySize || col >= matrix.arraySize) return;
        var elem    = matrix.GetArrayElementAtIndex(row);
        uint current = unchecked((uint)elem.intValue);
        if (value) current |=  (1u << col);
        else       current &= ~(1u << col);
        elem.intValue = unchecked((int)current);
    }

    // ── Quality Settings ─────────────────────────────────────────────────────

    static UCAFResult CmdSetQualitySetting(UCAFCommand cmd)
    {
        string key   = cmd.GetParam("key", "");
        string value = cmd.GetParam("value", "");
        string level = cmd.GetParam("level", "");

        if (string.IsNullOrEmpty(key))
            return new UCAFResult { success = false, message = "key required (e.g. shadowDistance, vSyncCount)" };
        if (string.IsNullOrEmpty(value))
            return new UCAFResult { success = false, message = "value required" };

        var so = new SerializedObject(AssetDatabase.LoadMainAssetAtPath("ProjectSettings/QualitySettings.asset"));
        var perLevelSettings = so.FindProperty("m_QualitySettings");
        if (perLevelSettings == null)
            return new UCAFResult { success = false, message = "Could not read QualitySettings" };

        int targetLevel;
        if (string.IsNullOrEmpty(level))
            targetLevel = QualitySettings.GetQualityLevel();
        else if (!int.TryParse(level, out targetLevel))
            return new UCAFResult { success = false, message = "level must be an integer (quality level index)" };

        if (targetLevel < 0 || targetLevel >= perLevelSettings.arraySize)
            return new UCAFResult { success = false, message = $"Quality level {targetLevel} out of range (0–{perLevelSettings.arraySize - 1})" };

        var levelProp = perLevelSettings.GetArrayElementAtIndex(targetLevel);
        var prop      = levelProp.FindPropertyRelative(key);
        if (prop == null)
            return new UCAFResult { success = false, message = $"Unknown quality setting key '{key}'. Try: shadowDistance, vSyncCount, antiAliasing, anisotropicFiltering, shadowCascades" };

        bool applied = SetSerializedPropertyValue(prop, value);
        if (!applied)
            return new UCAFResult { success = false, message = $"Could not set '{key}' to '{value}' — type mismatch or unsupported type ({prop.propertyType})" };

        so.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.SaveAssets();
        return new UCAFResult { success = true, message = $"Quality setting '{key}' = '{value}' on level {targetLevel}" };
    }

    // ── Graphics Settings ────────────────────────────────────────────────────

    static UCAFResult CmdSetGraphicsSetting(UCAFCommand cmd)
    {
        string key     = cmd.GetParam("key", "");
        string value   = cmd.GetParam("value", "");
        bool confirm   = cmd.GetParam("confirm", "false") == "true";

        if (string.IsNullOrEmpty(key))
            return new UCAFResult { success = false, message = "key required (e.g. renderPipeline, colorSpace)" };
        if (string.IsNullOrEmpty(value))
            return new UCAFResult { success = false, message = "value required" };

        // colorSpace change is destructive — requires confirmation
        if (key.Equals("colorSpace", StringComparison.OrdinalIgnoreCase) && !confirm)
            return new UCAFResult { success = false, message = "colorSpace change is destructive (reimports textures). Add confirm=true to proceed." };

        // renderPipeline — set by asset path
        if (key.Equals("renderPipeline", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("renderPipelineAsset", StringComparison.OrdinalIgnoreCase))
        {
            var rpa = AssetDatabase.LoadAssetAtPath<UnityEngine.Rendering.RenderPipelineAsset>(value);
            if (rpa == null)
                return new UCAFResult { success = false, message = $"RenderPipelineAsset not found at '{value}'" };
            UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline = rpa;
            return new UCAFResult { success = true, message = $"Render pipeline set to {rpa.name}" };
        }

        // colorSpace
        if (key.Equals("colorSpace", StringComparison.OrdinalIgnoreCase))
        {
            if (!Enum.TryParse<ColorSpace>(value, true, out var cs))
                return new UCAFResult { success = false, message = $"Unknown colorSpace '{value}'. Valid: Linear, Gamma" };
            PlayerSettings.colorSpace = cs;
            return new UCAFResult { success = true, message = $"Color space set to {cs}" };
        }

        // Generic: write via SerializedObject on GraphicsSettings asset
        var so = new SerializedObject(AssetDatabase.LoadMainAssetAtPath("ProjectSettings/GraphicsSettings.asset"));
        var prop = so.FindProperty(key);
        if (prop == null)
            return new UCAFResult { success = false, message = $"Unknown GraphicsSettings key '{key}'" };

        bool applied = SetSerializedPropertyValue(prop, value);
        if (!applied)
            return new UCAFResult { success = false, message = $"Could not set '{key}' to '{value}'" };

        so.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.SaveAssets();
        return new UCAFResult { success = true, message = $"Graphics setting '{key}' = '{value}'" };
    }

    // ── Input Axis (legacy InputManager) ─────────────────────────────────────

    static UCAFResult CmdSetInputAxis(UCAFCommand cmd)
    {
        string axisName = cmd.GetParam("name", "");
        if (string.IsNullOrEmpty(axisName))
            return new UCAFResult { success = false, message = "name required (e.g. Horizontal, Fire1)" };

        var so   = new SerializedObject(AssetDatabase.LoadMainAssetAtPath("ProjectSettings/InputManager.asset"));
        var axes = so.FindProperty("m_Axes");
        if (axes == null)
            return new UCAFResult { success = false, message = "Could not read InputManager axes" };

        // Find existing axis or add new one
        SerializedProperty axisEntry = null;
        for (int i = 0; i < axes.arraySize; i++)
        {
            var entry = axes.GetArrayElementAtIndex(i);
            if (entry.FindPropertyRelative("m_Name")?.stringValue == axisName)
            { axisEntry = entry; break; }
        }

        if (axisEntry == null)
        {
            axes.arraySize++;
            axisEntry = axes.GetArrayElementAtIndex(axes.arraySize - 1);
            axisEntry.FindPropertyRelative("m_Name").stringValue = axisName;
        }

        int changed = 0;
        string positiveBtn = cmd.GetParam("positiveButton", "");
        if (!string.IsNullOrEmpty(positiveBtn)) { axisEntry.FindPropertyRelative("positiveButton").stringValue = positiveBtn; changed++; }

        string negativeBtn = cmd.GetParam("negativeButton", "");
        if (!string.IsNullOrEmpty(negativeBtn)) { axisEntry.FindPropertyRelative("negativeButton").stringValue = negativeBtn; changed++; }

        string typeStr = cmd.GetParam("type", "");
        if (!string.IsNullOrEmpty(typeStr) && int.TryParse(typeStr, out int typeVal))
        { axisEntry.FindPropertyRelative("type").intValue = typeVal; changed++; }

        string sensitivityStr = cmd.GetParam("sensitivity", "");
        if (!string.IsNullOrEmpty(sensitivityStr) && float.TryParse(sensitivityStr, NumberStyles.Float, CultureInfo.InvariantCulture, out float sens))
        { axisEntry.FindPropertyRelative("sensitivity").floatValue = sens; changed++; }

        if (changed == 0)
            return new UCAFResult { success = false, message = "No axis parameters provided. Valid: positiveButton, negativeButton, type, sensitivity" };

        so.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.SaveAssets();
        return new UCAFResult { success = true, message = $"Input axis '{axisName}' updated" };
    }

    // ── Input Action (Input System asset) ────────────────────────────────────

    static UCAFResult CmdAddInputAction(UCAFCommand cmd)
    {
        string assetPath = cmd.GetParam("asset_path", "");
        string mapName   = cmd.GetParam("map", "Player");
        string actionName = cmd.GetParam("action", "");
        string binding   = cmd.GetParam("binding", "");

        if (string.IsNullOrEmpty(assetPath))
            return new UCAFResult { success = false, message = "asset_path required (path to .inputactions asset)" };
        if (string.IsNullOrEmpty(actionName))
            return new UCAFResult { success = false, message = "action required" };

        // Load as text asset and edit JSON directly (InputActionAsset uses its own serialization)
        string fullPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), assetPath);
        if (!File.Exists(fullPath))
            return new UCAFResult { success = false, message = $".inputactions file not found: {assetPath}" };

        // We do a minimal JSON edit to add the action — avoiding dependency on InputSystem package
        string json = File.ReadAllText(fullPath);
        // Find the action map
        int mapIdx = json.IndexOf($"\"name\": \"{mapName}\"", StringComparison.Ordinal);
        if (mapIdx < 0)
            mapIdx = json.IndexOf($"\"name\":\"{mapName}\"", StringComparison.Ordinal);
        if (mapIdx < 0)
            return new UCAFResult { success = false, message = $"Action map '{mapName}' not found in {assetPath}" };

        // Check if action already exists
        if (json.IndexOf($"\"name\": \"{actionName}\"", mapIdx, StringComparison.Ordinal) > 0 ||
            json.IndexOf($"\"name\":\"{actionName}\"", mapIdx, StringComparison.Ordinal) > 0)
            return new UCAFResult { success = true, message = $"Action '{actionName}' already exists in map '{mapName}'", data_json = "{\"skipped\":true}" };

        // Find the "actions" array in this map and append
        int actionsIdx = json.IndexOf("\"actions\":", mapIdx);
        if (actionsIdx < 0)
            return new UCAFResult { success = false, message = $"Could not find 'actions' array in map '{mapName}'" };
        int arrStart = json.IndexOf('[', actionsIdx);
        if (arrStart < 0)
            return new UCAFResult { success = false, message = "Malformed .inputactions JSON" };

        string newAction = $"{{ \"name\": \"{actionName}\", \"type\": \"Button\", \"id\": \"{Guid.NewGuid()}\", \"expectedControlType\": \"Button\", \"processors\": \"\", \"interactions\": \"\" }}";
        int insertPos = arrStart + 1;
        string needsComma = json.Substring(insertPos).TrimStart().StartsWith("{") ? "," : "";
        json = json.Insert(insertPos, "\n                " + newAction + needsComma);

        File.WriteAllText(fullPath, json);
        AssetDatabase.Refresh();

        string bindingNote = string.IsNullOrEmpty(binding) ? "" : $" (binding: {binding} — set via Inspector)";
        return new UCAFResult { success = true, message = $"Input action '{actionName}' added to map '{mapName}'{bindingNote}" };
    }

    // ── Utility: set SerializedProperty by string value ──────────────────────

    static bool SetSerializedPropertyValue(SerializedProperty prop, string value)
    {
        switch (prop.propertyType)
        {
            case SerializedPropertyType.Float:
                if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                { prop.floatValue = f; return true; }
                return false;
            case SerializedPropertyType.Integer:
                if (int.TryParse(value, out int i))
                { prop.intValue = i; return true; }
                return false;
            case SerializedPropertyType.Boolean:
                prop.boolValue = value == "true" || value == "1";
                return true;
            case SerializedPropertyType.String:
                prop.stringValue = value;
                return true;
            case SerializedPropertyType.Enum:
                if (int.TryParse(value, out int ei))
                { prop.enumValueIndex = ei; return true; }
                // Try by name
                var names = prop.enumNames;
                for (int n = 0; n < names.Length; n++)
                    if (names[n].Equals(value, StringComparison.OrdinalIgnoreCase))
                    { prop.enumValueIndex = n; return true; }
                return false;
            default:
                return false;
        }
    }
}
