using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

public static partial class UCAF_Listener
{
    // ── find_objects ────────────────────────────────────────────────────
    //
    // Filters (all optional, AND-combined):
    //   component_type, tag, layer (name or int), name_contains, include_inactive

    static UCAFResult CmdFindObjects(UCAFCommand cmd)
    {
        string componentType = cmd.GetParam("component_type", "");
        string tag           = cmd.GetParam("tag", "");
        string layerStr      = cmd.GetParam("layer", "");
        string nameContains  = cmd.GetParam("name_contains", "");
        bool includeInactive = cmd.GetParam("include_inactive", "false") == "true";
        int max = int.TryParse(cmd.GetParam("max", "500"), out int m) ? m : 500;

        int? layerFilter = null;
        if (!string.IsNullOrEmpty(layerStr))
        {
            if (int.TryParse(layerStr, out int li)) layerFilter = li;
            else
            {
                int ln = LayerMask.NameToLayer(layerStr);
                if (ln >= 0) layerFilter = ln;
            }
        }

        IEnumerable<GameObject> candidates;
        if (!string.IsNullOrEmpty(componentType))
        {
            var type = UCAF_Tools.FindTypeByName(componentType);
            if (type == null)
                return new UCAFResult { success = false, message = $"Component type not found: {componentType}" };
            var inactiveMode = includeInactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude;
            var components = UnityEngine.Object.FindObjectsByType(type, inactiveMode);
            candidates = components.OfType<Component>().Select(c => c.gameObject).Distinct();
        }
        else
        {
            var scene = EditorSceneManager.GetActiveScene();
            var bag = new List<GameObject>();
            foreach (var root in scene.GetRootGameObjects())
                CollectAll(root.transform, bag, includeInactive);
            candidates = bag;
        }

        var matches = new List<GameObject>();
        foreach (var go in candidates)
        {
            if (go == null) continue;
            if (!includeInactive && !go.activeInHierarchy) continue;
            if (!string.IsNullOrEmpty(tag) && go.tag != tag) continue;
            if (layerFilter.HasValue && go.layer != layerFilter.Value) continue;
            if (!string.IsNullOrEmpty(nameContains) &&
                go.name.IndexOf(nameContains, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
            matches.Add(go);
            if (matches.Count >= max) break;
        }

        var list = new UCAFObjectRefList();
        foreach (var go in matches)
            list.items.Add(new UCAFObjectRef { path = GetPath(go), name = go.name });

        return new UCAFResult {
            success = true,
            message = $"Found {list.items.Count} object(s)",
            data_json = JsonUtility.ToJson(list)
        };
    }

    static void CollectAll(Transform t, List<GameObject> bag, bool includeInactive)
    {
        if (t == null) return;
        var go = t.gameObject;
        if (includeInactive || go.activeInHierarchy) bag.Add(go);
        for (int i = 0; i < t.childCount; i++)
            CollectAll(t.GetChild(i), bag, includeInactive);
    }

    // ── get_object_info ─────────────────────────────────────────────────

    static UCAFResult CmdGetObjectInfo(UCAFCommand cmd)
    {
        var obj = ResolveObject(cmd, out string err);
        if (obj == null) return new UCAFResult { success = false, message = err };

        var info = new UCAFObjectInfo {
            path = GetPath(obj),
            name = obj.name,
            active = obj.activeSelf,
            tag = obj.tag,
            layer = obj.layer,
            position = VecStrInv(obj.transform.position),
            rotation = VecStrInv(obj.transform.eulerAngles),
            scale = VecStrInv(obj.transform.localScale),
            is_prefab_instance = PrefabUtility.IsPartOfPrefabInstance(obj)
        };
        if (info.is_prefab_instance)
        {
            var src = PrefabUtility.GetCorrespondingObjectFromSource(obj);
            if (src != null) info.prefab_asset_path = AssetDatabase.GetAssetPath(src);
        }
        foreach (var c in obj.GetComponents<Component>())
            info.components.Add(c == null ? "<missing>" : c.GetType().Name);

        return new UCAFResult {
            success = true,
            message = $"Info for {info.path}",
            data_json = JsonUtility.ToJson(info)
        };
    }

    static string VecStrInv(Vector3 v)
        => string.Join(",", new[] {
            v.x.ToString("R", CultureInfo.InvariantCulture),
            v.y.ToString("R", CultureInfo.InvariantCulture),
            v.z.ToString("R", CultureInfo.InvariantCulture)
        });
}
