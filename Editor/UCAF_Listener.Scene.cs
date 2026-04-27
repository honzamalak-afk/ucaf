using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

public static partial class UCAF_Listener
{
    // ── Scene lifecycle ──────────────────────────────────────────────────

    static UCAFResult CmdCreateScene(UCAFCommand cmd)
    {
        string name = cmd.GetParam("name", "NewScene");
        string ifExists = cmd.GetParam("if_exists", "error").ToLowerInvariant();

        string scenesDir = Path.Combine(Application.dataPath, "Scenes");
        Directory.CreateDirectory(scenesDir);
        string scenePath = $"Assets/Scenes/{name}.unity";
        string projectRoot = Path.GetDirectoryName(Application.dataPath);
        string absScenePath = Path.Combine(projectRoot, scenePath);

        if (File.Exists(absScenePath))
        {
            switch (ifExists)
            {
                case "skip":
                    return new UCAFResult {
                        success = true,
                        message = $"Skipped: {scenePath} already exists",
                        data_json = "{\"skipped\":true}"
                    };
                case "replace":
                    AssetDatabase.DeleteAsset(scenePath);
                    break;
                case "rename":
                {
                    int n = 1;
                    string newName;
                    do { n++; newName = $"{name} ({n})"; }
                    while (File.Exists(Path.Combine(projectRoot, $"Assets/Scenes/{newName}.unity")));
                    name = newName;
                    scenePath = $"Assets/Scenes/{name}.unity";
                    break;
                }
                case "error":
                default:
                    return new UCAFResult {
                        success = false,
                        message = $"Scene already exists: {scenePath} (use if_exists=skip|replace|rename)"
                    };
            }
        }

        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        EditorSceneManager.SaveScene(scene, scenePath);
        return new UCAFResult { success = true, message = $"Scene created: {scenePath}" };
    }

    static UCAFResult CmdOpenScene(UCAFCommand cmd)
    {
        string name = cmd.GetParam("name", "");
        string path = $"Assets/Scenes/{name}.unity";
        string absPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), path);
        if (!File.Exists(absPath))
            return new UCAFResult { success = false, message = $"Scene not found: {path}" };
        EditorSceneManager.OpenScene(path);
        return new UCAFResult { success = true, message = $"Opened: {path}" };
    }

    static UCAFResult CmdSaveScene(UCAFCommand cmd)
    {
        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.SaveScene(scene);
        return new UCAFResult { success = true, message = $"Saved: {scene.path}" };
    }

    // ── Hierarchy enumeration ───────────────────────────────────────────

    static UCAFResult CmdListScene(UCAFCommand cmd)
    {
        bool includeComponents = cmd.GetParam("include_components", "true") == "true";
        int maxDepth = int.TryParse(cmd.GetParam("max_depth", "-1"), out int d) ? d : -1;

        var scene = EditorSceneManager.GetActiveScene();
        var tree = new UCAFSceneTree { scene_name = scene.name };
        foreach (var root in scene.GetRootGameObjects())
            tree.roots.Add(BuildNode(root, root.name, includeComponents, maxDepth, 0));

        return new UCAFResult {
            success = true,
            message = $"Enumerated {CountNodes(tree.roots)} objects in '{scene.name}'.",
            data_json = JsonUtility.ToJson(tree)
        };
    }

    static UCAFSceneNode BuildNode(GameObject go, string path, bool includeComponents, int maxDepth, int depth)
    {
        var node = new UCAFSceneNode {
            path = path,
            name = go.name,
            active = go.activeSelf,
            tag = go.tag,
            layer = go.layer
        };
        if (includeComponents)
        {
            foreach (var c in go.GetComponents<Component>())
                node.components.Add(c == null ? "<missing>" : c.GetType().Name);
        }
        if (maxDepth < 0 || depth < maxDepth)
        {
            for (int i = 0; i < go.transform.childCount; i++)
            {
                var child = go.transform.GetChild(i).gameObject;
                node.children.Add(BuildNode(child, $"{path}/{child.name}", includeComponents, maxDepth, depth + 1));
            }
        }
        return node;
    }

    static int CountNodes(List<UCAFSceneNode> nodes)
    {
        int c = 0;
        foreach (var n in nodes) c += 1 + CountNodes(n.children);
        return c;
    }

    // ── Object ops ───────────────────────────────────────────────────────

    static UCAFResult CmdCreateObject(UCAFCommand cmd)
    {
        string name = cmd.GetParam("name", "NewObject");
        string primitive = cmd.GetParam("primitive", "");
        string prefabPath = cmd.GetParam("prefab_path", "");
        string parentPath = cmd.GetParam("parent", "");
        string ifExists = cmd.GetParam("if_exists", "error").ToLowerInvariant();

        // Resolve potential existing target before creating anything
        Transform parentT = null;
        if (!string.IsNullOrEmpty(parentPath))
        {
            var parent = FindByPath(parentPath);
            if (parent == null)
                return new UCAFResult { success = false, message = $"Parent not found: {parentPath}" };
            parentT = parent.transform;
        }

        GameObject existing = null;
        if (parentT != null)
        {
            var t = parentT.Find(name);
            if (t != null) existing = t.gameObject;
        }
        else
        {
            string lookupPath = string.IsNullOrEmpty(parentPath) ? name : $"{parentPath}/{name}";
            existing = FindByPath(lookupPath);
            if (existing == null) existing = GameObject.Find(name);
        }

        if (existing != null)
        {
            switch (ifExists)
            {
                case "skip":
                    return new UCAFResult {
                        success = true,
                        message = $"Skipped: {GetPath(existing)} already exists",
                        data_json = "{\"skipped\":true,\"path\":\"" + GetPath(existing) + "\"}"
                    };
                case "replace":
                    UnityEngine.Object.DestroyImmediate(existing);
                    break;
                case "rename":
                    name = MakeUniqueName(name, parentT);
                    break;
                case "error":
                default:
                    return new UCAFResult {
                        success = false,
                        message = $"Object '{name}' already exists at parent (use if_exists=skip|replace|rename)"
                    };
            }
        }

        GameObject obj;
        if (!string.IsNullOrEmpty(prefabPath))
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
                return new UCAFResult { success = false, message = $"Prefab not found: {prefabPath}" };
            obj = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        }
        else if (!string.IsNullOrEmpty(primitive))
        {
            if (!Enum.TryParse(primitive, true, out PrimitiveType pt))
                return new UCAFResult { success = false, message = $"Unknown primitive: {primitive}" };
            obj = GameObject.CreatePrimitive(pt);
        }
        else
        {
            obj = new GameObject();
        }

        obj.name = name;

        if (parentT != null)
            obj.transform.SetParent(parentT, false);

        ApplyCommonProperties(obj, cmd);
        EditorSceneManager.MarkSceneDirty(obj.scene);
        return new UCAFResult { success = true, message = $"Created: {GetPath(obj)}" };
    }

    static string MakeUniqueName(string baseName, Transform parent)
    {
        int n = 1;
        string candidate = baseName;
        while (true)
        {
            bool taken = parent != null ? parent.Find(candidate) != null
                                        : GameObject.Find(candidate) != null;
            if (!taken) return candidate;
            n++;
            candidate = $"{baseName} ({n})";
        }
    }

    static UCAFResult CmdModifyObject(UCAFCommand cmd)
    {
        var obj = ResolveObject(cmd, out string err);
        if (obj == null) return new UCAFResult { success = false, message = err };

        ApplyCommonProperties(obj, cmd);

        string newName = cmd.GetParam("new_name", "");
        if (!string.IsNullOrEmpty(newName)) obj.name = newName;

        EditorSceneManager.MarkSceneDirty(obj.scene);
        return new UCAFResult { success = true, message = $"Modified: {GetPath(obj)}" };
    }

    static UCAFResult CmdDeleteObject(UCAFCommand cmd)
    {
        var obj = ResolveObject(cmd, out string err);
        if (obj == null) return new UCAFResult { success = false, message = err };
        var scene = obj.scene;
        UnityEngine.Object.DestroyImmediate(obj);
        EditorSceneManager.MarkSceneDirty(scene);
        return new UCAFResult { success = true, message = "Deleted." };
    }

    static UCAFResult CmdReparentObject(UCAFCommand cmd)
    {
        var obj = ResolveObject(cmd, out string err);
        if (obj == null) return new UCAFResult { success = false, message = err };
        string newParentPath = cmd.GetParam("new_parent", "");
        bool keepWorld = cmd.GetParam("keep_world_position", "true") == "true";

        if (string.IsNullOrEmpty(newParentPath))
        {
            obj.transform.SetParent(null, keepWorld);
            EditorSceneManager.MarkSceneDirty(obj.scene);
            return new UCAFResult { success = true, message = $"{obj.name} moved to root" };
        }
        var parent = FindByPath(newParentPath);
        if (parent == null)
            return new UCAFResult { success = false, message = $"Parent not found: {newParentPath}" };
        obj.transform.SetParent(parent.transform, keepWorld);
        EditorSceneManager.MarkSceneDirty(obj.scene);
        return new UCAFResult { success = true, message = $"{obj.name} parented to {parent.name}" };
    }

    static UCAFResult CmdDuplicateObject(UCAFCommand cmd)
    {
        var obj = ResolveObject(cmd, out string err);
        if (obj == null) return new UCAFResult { success = false, message = err };
        var dup = UnityEngine.Object.Instantiate(obj, obj.transform.parent);
        dup.name = cmd.GetParam("new_name", obj.name + "_copy");
        ApplyCommonProperties(dup, cmd);
        EditorSceneManager.MarkSceneDirty(dup.scene);
        return new UCAFResult { success = true, message = $"Duplicated: {GetPath(dup)}" };
    }

    // ── Shared helpers (used across partials) ───────────────────────────

    internal static void ApplyCommonProperties(GameObject obj, UCAFCommand cmd)
    {
        string pos    = cmd.GetParam("position", "");
        string rot    = cmd.GetParam("rotation", "");
        string scl    = cmd.GetParam("scale", "");
        string tag    = cmd.GetParam("tag", "");
        string layer  = cmd.GetParam("layer", "");
        string active = cmd.GetParam("active", "");

        if (!string.IsNullOrEmpty(pos)) obj.transform.position    = ParseVec3(pos);
        if (!string.IsNullOrEmpty(rot)) obj.transform.eulerAngles = ParseVec3(rot);
        if (!string.IsNullOrEmpty(scl)) obj.transform.localScale  = ParseVec3(scl);
        if (!string.IsNullOrEmpty(tag))
        {
            try { obj.tag = tag; }
            catch { /* tag may not be defined in TagManager */ }
        }
        if (!string.IsNullOrEmpty(layer))
        {
            if (int.TryParse(layer, out int li)) obj.layer = li;
            else
            {
                int ln = LayerMask.NameToLayer(layer);
                if (ln >= 0) obj.layer = ln;
            }
        }
        if (!string.IsNullOrEmpty(active))
            obj.SetActive(active.ToLower() == "true" || active == "1");
    }

    internal static GameObject ResolveObject(UCAFCommand cmd, out string error)
    {
        error = null;
        string path = cmd.GetParam("path", "");
        string name = cmd.GetParam("name", "");
        GameObject obj = null;
        if (!string.IsNullOrEmpty(path)) obj = FindByPath(path);
        if (obj == null && !string.IsNullOrEmpty(name)) obj = GameObject.Find(name);
        if (obj == null)
        {
            error = $"Object not found (path='{path}', name='{name}')";
            return null;
        }
        return obj;
    }

    internal static GameObject FindByPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        path = path.Trim('/');
        var parts = path.Split('/');

        var scene = EditorSceneManager.GetActiveScene();
        GameObject current = null;
        foreach (var root in scene.GetRootGameObjects())
            if (root.name == parts[0]) { current = root; break; }
        if (current == null) return null;

        for (int i = 1; i < parts.Length; i++)
        {
            var t = current.transform.Find(parts[i]);
            if (t == null) return null;
            current = t.gameObject;
        }
        return current;
    }

    internal static string GetPath(GameObject obj)
    {
        if (obj == null) return "";
        string path = obj.name;
        var t = obj.transform.parent;
        while (t != null) { path = t.name + "/" + path; t = t.parent; }
        return path;
    }

    internal static Vector3 ParseVec3(string s)
    {
        var a = ParseFloats(s, 3);
        return new Vector3(a[0], a[1], a[2]);
    }

    internal static float[] ParseFloats(string s, int expected)
    {
        s = s.Trim('[', ']', '(', ')');
        var parts = s.Split(',');
        var result = new float[expected];
        for (int i = 0; i < expected && i < parts.Length; i++)
            result[i] = float.Parse(parts[i].Trim(), CultureInfo.InvariantCulture);
        return result;
    }
}
