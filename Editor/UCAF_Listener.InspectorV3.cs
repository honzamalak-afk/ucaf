using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

public static partial class UCAF_Listener
{
    // ── append_array_element ────────────────────────────────────────────
    //
    // Append a new element to a serialized array/list. Optionally seed it
    // with `value` (uses the same string format as set_field).

    static UCAFResult CmdAppendArrayElement(UCAFCommand cmd)
    {
        if (!TryResolveSerializedTarget(cmd, out var so, out var msg))
            return new UCAFResult { success = false, message = msg };

        string fieldPath = cmd.GetParam("field", "");
        if (string.IsNullOrEmpty(fieldPath))
            return new UCAFResult { success = false, message = "field required" };

        var prop = so.FindProperty(fieldPath);
        if (prop == null)
            return new UCAFResult { success = false, message = $"Field not found: {fieldPath}" };
        if (!prop.isArray)
            return new UCAFResult { success = false, message = $"Field is not an array: {fieldPath}" };

        int newIndex = prop.arraySize;
        prop.arraySize = newIndex + 1;
        so.ApplyModifiedProperties();

        string value = cmd.GetParam("value", "");
        bool hasValue = cmd.HasParam("value");
        if (hasValue)
        {
            so.Update();
            var element = so.FindProperty($"{fieldPath}.Array.data[{newIndex}]");
            if (element == null)
                return new UCAFResult { success = false, message = $"Could not access new element at index {newIndex}" };
            if (!TryDeserializeInto(element, value, out string err))
                return new UCAFResult { success = false, message = $"Append OK but value set failed: {err}" };
            so.ApplyModifiedProperties();
        }

        EditorUtility.SetDirty(so.targetObject);
        if (so.targetObject is Component comp && comp != null)
            EditorSceneManager.MarkSceneDirty(comp.gameObject.scene);

        var payload = new UCAFArrayAppendResult { new_index = newIndex, new_size = newIndex + 1 };
        return new UCAFResult {
            success = true,
            message = $"Appended at [{newIndex}], new size {newIndex + 1}",
            data_json = JsonUtility.ToJson(payload)
        };
    }

    // ── list_assets ─────────────────────────────────────────────────────
    //
    // Generalized AssetDatabase.FindAssets wrapper. `type` is a Unity type
    // filter (e.g. "Prefab", "Texture2D", "AudioClip", "Mesh", "Material",
    // "ScriptableObject", or any concrete class name). `folder` defaults
    // to "Assets". `name_contains` further filters by file name.

    static UCAFResult CmdListAssets(UCAFCommand cmd)
    {
        string type         = cmd.GetParam("type", "");
        string folder       = cmd.GetParam("folder", "Assets");
        string nameContains = cmd.GetParam("name_contains", "");
        int max = int.TryParse(cmd.GetParam("max", "500"), out int m) ? m : 500;

        string filter = string.IsNullOrEmpty(type) ? "" : $"t:{type}";
        if (!string.IsNullOrEmpty(nameContains))
            filter = (filter.Length > 0 ? filter + " " : "") + nameContains;

        string[] guids;
        try
        {
            guids = string.IsNullOrEmpty(folder)
                ? AssetDatabase.FindAssets(filter)
                : AssetDatabase.FindAssets(filter, new[] { folder });
        }
        catch (System.Exception ex)
        {
            return new UCAFResult { success = false, message = $"FindAssets failed: {ex.Message}" };
        }

        var list = new UCAFStringList();
        for (int i = 0; i < guids.Length && list.items.Count < max; i++)
            list.items.Add(AssetDatabase.GUIDToAssetPath(guids[i]));

        return new UCAFResult {
            success = true,
            message = $"Found {list.items.Count} asset(s) (filter='{filter}', folder='{folder}')",
            data_json = JsonUtility.ToJson(list)
        };
    }
}
