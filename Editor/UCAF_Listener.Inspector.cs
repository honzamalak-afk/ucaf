using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

public static partial class UCAF_Listener
{
    // ── Components ──────────────────────────────────────────────────────

    static UCAFResult CmdAddComponent(UCAFCommand cmd)
    {
        var obj = ResolveObject(cmd, out string err);
        if (obj == null) return new UCAFResult { success = false, message = err };
        string typeName = cmd.GetParam("component_type", "");
        var type = UCAF_Tools.FindTypeByName(typeName);
        if (type == null)
            return new UCAFResult { success = false, message = $"Component type not found: {typeName}" };
        if (!typeof(Component).IsAssignableFrom(type))
            return new UCAFResult { success = false, message = $"{typeName} is not a Component" };

        string ifExists = cmd.GetParam("if_exists", "error").ToLowerInvariant();
        var existing = obj.GetComponent(type);
        if (existing != null && ifExists != "error")
        {
            if (ifExists == "skip")
                return new UCAFResult {
                    success = true,
                    message = $"Skipped: {typeName} already on {obj.name}",
                    data_json = "{\"skipped\":true}"
                };
            if (ifExists == "replace")
            {
                UnityEngine.Object.DestroyImmediate(existing);
            }
        }
        else if (existing != null && ifExists == "error")
        {
            return new UCAFResult {
                success = false,
                message = $"{typeName} already on {obj.name} (use if_exists=skip|replace)"
            };
        }

        var comp = obj.AddComponent(type);
        if (comp == null)
            return new UCAFResult { success = false, message = $"Failed to add {typeName}" };
        EditorSceneManager.MarkSceneDirty(obj.scene);
        return new UCAFResult { success = true, message = $"Added {typeName} to {obj.name}" };
    }

    static UCAFResult CmdRemoveComponent(UCAFCommand cmd)
    {
        var obj = ResolveObject(cmd, out string err);
        if (obj == null) return new UCAFResult { success = false, message = err };
        string typeName = cmd.GetParam("component_type", "");
        int index = int.TryParse(cmd.GetParam("index", "0"), out int i) ? i : 0;
        var matches = obj.GetComponents<Component>()
            .Where(c => c != null && c.GetType().Name == typeName).ToArray();
        if (matches.Length == 0)
            return new UCAFResult { success = false, message = $"No {typeName} on {obj.name}" };
        if (index < 0 || index >= matches.Length)
            return new UCAFResult { success = false, message = $"Index {index} out of range (have {matches.Length})" };
        UnityEngine.Object.DestroyImmediate(matches[index]);
        EditorSceneManager.MarkSceneDirty(obj.scene);
        return new UCAFResult { success = true, message = $"Removed {typeName}[{index}] from {obj.name}" };
    }

    static UCAFResult CmdListComponents(UCAFCommand cmd)
    {
        var obj = ResolveObject(cmd, out string err);
        if (obj == null) return new UCAFResult { success = false, message = err };
        var list = new UCAFStringList();
        foreach (var c in obj.GetComponents<Component>())
            list.items.Add(c == null ? "<missing>" : c.GetType().Name);
        return new UCAFResult {
            success = true,
            message = $"{list.items.Count} components on {obj.name}",
            data_json = JsonUtility.ToJson(list)
        };
    }

    // ── Field read/write (SerializedObject bridge) ──────────────────────

    static UCAFResult CmdListFields(UCAFCommand cmd)
    {
        if (!TryResolveSerializedTarget(cmd, out var so, out var msg))
            return new UCAFResult { success = false, message = msg };

        var list = new UCAFFieldList();
        var iter = so.GetIterator();
        bool enterChildren = true;
        while (iter.NextVisible(enterChildren))
        {
            enterChildren = false;
            list.fields.Add(new UCAFFieldInfo {
                name = iter.propertyPath,
                display_name = iter.displayName,
                type = iter.propertyType.ToString() +
                       (string.IsNullOrEmpty(iter.type) ? "" : $" ({iter.type})")
            });
        }
        return new UCAFResult {
            success = true,
            message = $"{list.fields.Count} serialized fields",
            data_json = JsonUtility.ToJson(list)
        };
    }

    static UCAFResult CmdGetField(UCAFCommand cmd)
    {
        if (!TryResolveSerializedTarget(cmd, out var so, out var msg))
            return new UCAFResult { success = false, message = msg };

        string fieldPath = cmd.GetParam("field", "");
        var prop = so.FindProperty(fieldPath);
        if (prop == null)
            return new UCAFResult { success = false, message = $"Field not found: {fieldPath}" };

        var value = new UCAFFieldValue {
            field_name = fieldPath,
            type = prop.propertyType.ToString(),
            value = SerializeProperty(prop)
        };
        return new UCAFResult {
            success = true,
            message = $"Read {fieldPath}",
            data_json = JsonUtility.ToJson(value)
        };
    }

    static UCAFResult CmdSetField(UCAFCommand cmd)
    {
        if (!TryResolveSerializedTarget(cmd, out var so, out var msg))
            return new UCAFResult { success = false, message = msg };

        string fieldPath = cmd.GetParam("field", "");
        string valueStr  = cmd.GetParam("value", "");
        bool dryRun = cmd.GetParam("dry_run", "false") == "true";
        var prop = so.FindProperty(fieldPath);
        if (prop == null)
        {
            // Hint with sibling field names — common cause is a typo
            var hint = new UCAFErrorContext {
                error_code = "MISSING_TARGET",
                field = fieldPath,
                hint = "Use describe_component / list_fields to see available field names"
            };
            return new UCAFResult {
                success = false,
                message = $"Field not found: {fieldPath}",
                data_json = JsonUtility.ToJson(hint)
            };
        }

        if (!TryDeserializeIntoEx(prop, valueStr, out string err, out UCAFErrorContext ctx))
        {
            return new UCAFResult {
                success = false,
                message = err,
                data_json = ctx != null ? JsonUtility.ToJson(ctx) : null
            };
        }

        if (dryRun)
        {
            // capture before/after without applying
            string before = SerializeProperty(prop);
            // prop has already been modified in memory by TryDeserializeIntoEx — discard via revert
            so.Update(); // re-read from target → prop reset to actual stored value
            return new UCAFResult {
                success = true,
                message = $"[dry_run] would set {fieldPath} = {valueStr} (current: {before})",
                data_json = "{\"would_change\":true,\"before\":\"" + EscapeJson(before) +
                            "\",\"after\":\"" + EscapeJson(valueStr) + "\"}"
            };
        }

        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(so.targetObject);
        if (so.targetObject is Component comp && comp != null)
            EditorSceneManager.MarkSceneDirty(comp.gameObject.scene);

        return new UCAFResult { success = true, message = $"Set {fieldPath} = {valueStr}" };
    }

    static string EscapeJson(string s)
        => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

    // Resolves SerializedObject from:
    //   - component on an object (path + component_type [+ index]), OR
    //   - asset at asset_path (ScriptableObject, Material, etc.)
    static bool TryResolveSerializedTarget(UCAFCommand cmd, out SerializedObject so, out string message)
    {
        so = null;
        string assetPath = cmd.GetParam("asset_path", "");
        if (!string.IsNullOrEmpty(assetPath))
        {
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
            if (asset == null) { message = $"Asset not found: {assetPath}"; return false; }
            so = new SerializedObject(asset);
            message = null;
            return true;
        }

        var obj = ResolveObject(cmd, out string err);
        if (obj == null) { message = err; return false; }
        string typeName = cmd.GetParam("component_type", "");
        if (string.IsNullOrEmpty(typeName))
        {
            message = "component_type or asset_path required";
            return false;
        }

        int index = int.TryParse(cmd.GetParam("index", "0"), out int i) ? i : 0;
        var matches = obj.GetComponents<Component>()
            .Where(c => c != null && c.GetType().Name == typeName).ToArray();
        if (matches.Length == 0) { message = $"No {typeName} on {obj.name}"; return false; }
        if (index < 0 || index >= matches.Length)
        { message = $"Index {index} out of range (have {matches.Length})"; return false; }
        so = new SerializedObject(matches[index]);
        message = null;
        return true;
    }

    // ── Property (de)serialization ──────────────────────────────────────

    static string SerializeProperty(SerializedProperty p)
    {
        switch (p.propertyType)
        {
            case SerializedPropertyType.Integer:    return p.intValue.ToString(CultureInfo.InvariantCulture);
            case SerializedPropertyType.Boolean:    return p.boolValue ? "true" : "false";
            case SerializedPropertyType.Float:      return p.floatValue.ToString("R", CultureInfo.InvariantCulture);
            case SerializedPropertyType.String:     return p.stringValue ?? "";
            case SerializedPropertyType.Enum:
                return (p.enumValueIndex >= 0 && p.enumValueIndex < p.enumNames.Length)
                    ? p.enumNames[p.enumValueIndex] : p.enumValueIndex.ToString();
            case SerializedPropertyType.Vector2:    return VecStr(p.vector2Value.x, p.vector2Value.y);
            case SerializedPropertyType.Vector3:    return VecStr(p.vector3Value.x, p.vector3Value.y, p.vector3Value.z);
            case SerializedPropertyType.Vector4:    return VecStr(p.vector4Value.x, p.vector4Value.y, p.vector4Value.z, p.vector4Value.w);
            case SerializedPropertyType.Vector2Int: return VecStr(p.vector2IntValue.x, p.vector2IntValue.y);
            case SerializedPropertyType.Vector3Int: return VecStr(p.vector3IntValue.x, p.vector3IntValue.y, p.vector3IntValue.z);
            case SerializedPropertyType.Quaternion: return VecStr(p.quaternionValue.x, p.quaternionValue.y, p.quaternionValue.z, p.quaternionValue.w);
            case SerializedPropertyType.Color:      return "#" + ColorUtility.ToHtmlStringRGBA(p.colorValue);
            case SerializedPropertyType.Rect:       return VecStr(p.rectValue.x, p.rectValue.y, p.rectValue.width, p.rectValue.height);
            case SerializedPropertyType.ArraySize:  return p.intValue.ToString();
            case SerializedPropertyType.Character:  return ((char)p.intValue).ToString();
            case SerializedPropertyType.ObjectReference:
                if (p.objectReferenceValue == null) return "";
                string ap = AssetDatabase.GetAssetPath(p.objectReferenceValue);
                if (!string.IsNullOrEmpty(ap)) return ap;
                if (p.objectReferenceValue is GameObject go) return "scene:" + GetPath(go);
                if (p.objectReferenceValue is Component c)   return "scene:" + GetPath(c.gameObject) + "#" + c.GetType().Name;
                return p.objectReferenceValue.name;
            case SerializedPropertyType.LayerMask:  return p.intValue.ToString();
            case SerializedPropertyType.Generic:
                if (p.isArray) return $"<array:size={p.arraySize}>";
                return "<generic>";
            default: return $"<{p.propertyType}>";
        }
    }

    static string VecStr(params float[] v)
        => string.Join(",", v.Select(f => f.ToString("R", CultureInfo.InvariantCulture)));

    static bool TryDeserializeIntoEx(SerializedProperty p, string value, out string error, out UCAFErrorContext ctx)
    {
        ctx = null;
        try
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Color:
                    if (!ColorUtility.TryParseHtmlString(value, out _))
                    {
                        ctx = new UCAFErrorContext {
                            error_code = "INVALID_VALUE",
                            field = p.propertyPath,
                            expected_type = "Color",
                            hint = "Use #RRGGBB / #RRGGBBAA hex, or named colors (red, blue, ...)",
                        };
                        ctx.valid_format_examples.Add("#FF8800");
                        ctx.valid_format_examples.Add("#FF8800CC");
                        ctx.valid_format_examples.Add("red");
                        error = $"Cannot parse color: '{value}'";
                        return false;
                    }
                    break;
                case SerializedPropertyType.Enum:
                {
                    int idx = Array.IndexOf(p.enumNames, value);
                    if (idx < 0 && !int.TryParse(value, out _))
                    {
                        ctx = new UCAFErrorContext {
                            error_code = "INVALID_VALUE",
                            field = p.propertyPath,
                            expected_type = "Enum",
                            hint = "Pass enum name or numeric index",
                        };
                        ctx.valid_values_sample.AddRange(p.enumNames ?? new string[0]);
                        error = $"Enum value '{value}' not found";
                        return false;
                    }
                    break;
                }
                case SerializedPropertyType.LayerMask:
                {
                    if (!int.TryParse(value, out _))
                    {
                        ctx = new UCAFErrorContext {
                            error_code = "INVALID_VALUE",
                            field = p.propertyPath,
                            expected_type = "LayerMask (int)",
                            hint = "Pass an integer bitmask. Existing layers listed below.",
                        };
                        for (int i = 0; i < 32; i++)
                        {
                            string ln = LayerMask.LayerToName(i);
                            if (!string.IsNullOrEmpty(ln)) ctx.valid_values_sample.Add($"{i}: {ln}");
                        }
                        error = $"LayerMask must be int: '{value}'";
                        return false;
                    }
                    break;
                }
                case SerializedPropertyType.ObjectReference:
                {
                    if (!string.IsNullOrEmpty(value) && !value.StartsWith("scene:")
                        && AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(value) == null)
                    {
                        ctx = new UCAFErrorContext {
                            error_code = "MISSING_TARGET",
                            field = p.propertyPath,
                            expected_type = "ObjectReference (asset_path or scene:Path[#Component])",
                            hint = "Use list_assets to find an asset path; or 'scene:Root/Child' to reference a scene object",
                        };
                        ctx.valid_format_examples.Add("Assets/Audio/punch.wav");
                        ctx.valid_format_examples.Add("scene:Player");
                        ctx.valid_format_examples.Add("scene:Player#PlayerController");
                        error = $"Asset not found: {value}";
                        return false;
                    }
                    break;
                }
            }
        }
        catch { /* fall through to legacy path which produces a generic error */ }

        return TryDeserializeInto(p, value, out error);
    }

    static bool TryDeserializeInto(SerializedProperty p, string value, out string error)
    {
        error = null;
        try
        {
            switch (p.propertyType)
            {
                case SerializedPropertyType.Integer:
                    p.intValue = int.Parse(value, CultureInfo.InvariantCulture); break;
                case SerializedPropertyType.Boolean:
                    p.boolValue = value.ToLower() == "true" || value == "1"; break;
                case SerializedPropertyType.Float:
                    p.floatValue = float.Parse(value, CultureInfo.InvariantCulture); break;
                case SerializedPropertyType.String:
                    p.stringValue = value; break;
                case SerializedPropertyType.Enum:
                {
                    int idx = Array.IndexOf(p.enumNames, value);
                    if (idx < 0 && int.TryParse(value, out int n)) idx = n;
                    if (idx < 0)
                    {
                        error = $"Enum value '{value}' not found; options: {string.Join(",", p.enumNames)}";
                        return false;
                    }
                    p.enumValueIndex = idx; break;
                }
                case SerializedPropertyType.Vector2:
                { var a = ParseFloats(value, 2); p.vector2Value = new Vector2(a[0], a[1]); break; }
                case SerializedPropertyType.Vector3:
                { var a = ParseFloats(value, 3); p.vector3Value = new Vector3(a[0], a[1], a[2]); break; }
                case SerializedPropertyType.Vector4:
                { var a = ParseFloats(value, 4); p.vector4Value = new Vector4(a[0], a[1], a[2], a[3]); break; }
                case SerializedPropertyType.Vector2Int:
                { var a = ParseFloats(value, 2); p.vector2IntValue = new Vector2Int((int)a[0], (int)a[1]); break; }
                case SerializedPropertyType.Vector3Int:
                { var a = ParseFloats(value, 3); p.vector3IntValue = new Vector3Int((int)a[0], (int)a[1], (int)a[2]); break; }
                case SerializedPropertyType.Quaternion:
                { var a = ParseFloats(value, 4); p.quaternionValue = new Quaternion(a[0], a[1], a[2], a[3]); break; }
                case SerializedPropertyType.Color:
                {
                    if (!ColorUtility.TryParseHtmlString(value, out Color c))
                    { error = $"Cannot parse color: {value}"; return false; }
                    p.colorValue = c; break;
                }
                case SerializedPropertyType.Rect:
                { var a = ParseFloats(value, 4); p.rectValue = new Rect(a[0], a[1], a[2], a[3]); break; }
                case SerializedPropertyType.ArraySize:
                    p.intValue = int.Parse(value, CultureInfo.InvariantCulture); break;
                case SerializedPropertyType.Character:
                    p.intValue = value.Length > 0 ? value[0] : 0; break;
                case SerializedPropertyType.LayerMask:
                    p.intValue = int.Parse(value, CultureInfo.InvariantCulture); break;
                case SerializedPropertyType.ObjectReference:
                {
                    if (string.IsNullOrEmpty(value)) { p.objectReferenceValue = null; break; }
                    UnityEngine.Object target;
                    if (value.StartsWith("scene:"))
                    {
                        string rest = value.Substring(6);
                        string compType = null;
                        int hash = rest.IndexOf('#');
                        if (hash >= 0)
                        {
                            compType = rest.Substring(hash + 1);
                            rest = rest.Substring(0, hash);
                        }
                        var go = FindByPath(rest);
                        if (go == null) { error = $"Scene object not found: {rest}"; return false; }
                        if (compType != null)
                        {
                            var c = go.GetComponents<Component>()
                                .FirstOrDefault(x => x != null && x.GetType().Name == compType);
                            if (c == null) { error = $"Component {compType} not found on {rest}"; return false; }
                            target = c;
                        }
                        else target = go;
                    }
                    else
                    {
                        target = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(value);
                        if (target == null) { error = $"Asset not found: {value}"; return false; }
                    }
                    p.objectReferenceValue = target;
                    break;
                }
                default:
                    error = $"Unsupported property type for set: {p.propertyType}";
                    return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = $"Parse error: {ex.Message}";
            return false;
        }
    }

    // ── ScriptableObjects ───────────────────────────────────────────────

    static UCAFResult CmdCreateScriptable(UCAFCommand cmd)
    {
        string className = cmd.GetParam("class_name", "");
        string assetPath = cmd.GetParam("asset_path", "");
        string ifExists = cmd.GetParam("if_exists", "error").ToLowerInvariant();
        if (string.IsNullOrEmpty(className) || string.IsNullOrEmpty(assetPath))
            return new UCAFResult { success = false, message = "class_name and asset_path required" };

        var type = UCAF_Tools.FindTypeByName(className);
        if (type == null || !typeof(ScriptableObject).IsAssignableFrom(type))
            return new UCAFResult { success = false, message = $"{className} is not a ScriptableObject" };

        string projectRoot = Path.GetDirectoryName(Application.dataPath);
        string dir = Path.GetDirectoryName(assetPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(Path.Combine(projectRoot, dir));

        if (File.Exists(Path.Combine(projectRoot, assetPath)))
        {
            switch (ifExists)
            {
                case "skip":
                    return new UCAFResult {
                        success = true,
                        message = $"Skipped: {assetPath} already exists",
                        data_json = "{\"skipped\":true}"
                    };
                case "replace":
                    AssetDatabase.DeleteAsset(assetPath);
                    break;
                case "rename":
                {
                    string ext = System.IO.Path.GetExtension(assetPath);
                    string baseN = assetPath.Substring(0, assetPath.Length - ext.Length);
                    int n = 1; string candidate;
                    do { n++; candidate = $"{baseN} ({n}){ext}"; }
                    while (File.Exists(Path.Combine(projectRoot, candidate)));
                    assetPath = candidate;
                    break;
                }
                case "error":
                default:
                    return new UCAFResult {
                        success = false,
                        message = $"Asset already exists: {assetPath} (use if_exists=skip|replace|rename)"
                    };
            }
        }

        var so = ScriptableObject.CreateInstance(type);
        AssetDatabase.CreateAsset(so, assetPath);
        AssetDatabase.SaveAssets();
        return new UCAFResult { success = true, message = $"Created ScriptableObject: {assetPath}" };
    }

    static UCAFResult CmdListScriptables(UCAFCommand cmd)
    {
        string className = cmd.GetParam("class_name", "");
        string folder    = cmd.GetParam("folder", "Assets");

        string filter = string.IsNullOrEmpty(className) ? "t:ScriptableObject" : $"t:{className}";
        var guids = AssetDatabase.FindAssets(filter, new[] { folder });
        var list = new UCAFStringList();
        foreach (var g in guids) list.items.Add(AssetDatabase.GUIDToAssetPath(g));
        return new UCAFResult {
            success = true,
            message = $"Found {list.items.Count} ScriptableObject asset(s)",
            data_json = JsonUtility.ToJson(list)
        };
    }

    // ── Materials ───────────────────────────────────────────────────────

    static UCAFResult CmdCreateMaterial(UCAFCommand cmd)
    {
        string assetPath  = cmd.GetParam("asset_path", "");
        string shaderName = cmd.GetParam("shader", "HDRP/Lit");
        string ifExists   = cmd.GetParam("if_exists", "error").ToLowerInvariant();
        if (string.IsNullOrEmpty(assetPath))
            return new UCAFResult { success = false, message = "asset_path required" };
        var shader = Shader.Find(shaderName);
        if (shader == null)
            return new UCAFResult { success = false, message = $"Shader not found: {shaderName}" };

        string projectRoot = Path.GetDirectoryName(Application.dataPath);
        string dir = Path.GetDirectoryName(assetPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(Path.Combine(projectRoot, dir));

        if (File.Exists(Path.Combine(projectRoot, assetPath)))
        {
            switch (ifExists)
            {
                case "skip":
                    return new UCAFResult {
                        success = true,
                        message = $"Skipped: {assetPath} already exists",
                        data_json = "{\"skipped\":true}"
                    };
                case "replace":
                    AssetDatabase.DeleteAsset(assetPath);
                    break;
                case "rename":
                {
                    string ext = System.IO.Path.GetExtension(assetPath);
                    string baseN = assetPath.Substring(0, assetPath.Length - ext.Length);
                    int n = 1; string candidate;
                    do { n++; candidate = $"{baseN} ({n}){ext}"; }
                    while (File.Exists(Path.Combine(projectRoot, candidate)));
                    assetPath = candidate;
                    break;
                }
                case "error":
                default:
                    return new UCAFResult {
                        success = false,
                        message = $"Material already exists: {assetPath} (use if_exists=skip|replace|rename)"
                    };
            }
        }

        var mat = new Material(shader);
        AssetDatabase.CreateAsset(mat, assetPath);
        AssetDatabase.SaveAssets();
        return new UCAFResult { success = true, message = $"Material created: {assetPath}" };
    }

    static UCAFResult CmdAssignMaterial(UCAFCommand cmd)
    {
        var obj = ResolveObject(cmd, out string err);
        if (obj == null) return new UCAFResult { success = false, message = err };
        string assetPath = cmd.GetParam("material_path", "");
        var mat = AssetDatabase.LoadAssetAtPath<Material>(assetPath);
        if (mat == null)
            return new UCAFResult { success = false, message = $"Material not found: {assetPath}" };
        var rend = obj.GetComponent<Renderer>();
        if (rend == null)
            return new UCAFResult { success = false, message = $"No Renderer on {obj.name}" };
        int slot = int.TryParse(cmd.GetParam("slot", "0"), out int s) ? s : 0;
        var mats = rend.sharedMaterials;
        if (slot < 0 || slot >= mats.Length)
            return new UCAFResult { success = false, message = $"Slot {slot} out of range (have {mats.Length})" };
        mats[slot] = mat;
        rend.sharedMaterials = mats;
        EditorUtility.SetDirty(rend);
        EditorSceneManager.MarkSceneDirty(obj.scene);
        return new UCAFResult { success = true, message = $"Assigned {mat.name} to {obj.name}[{slot}]" };
    }

    static UCAFResult CmdSetMaterialProp(UCAFCommand cmd)
    {
        string path = cmd.GetParam("material_path", "");
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null) return new UCAFResult { success = false, message = $"Material not found: {path}" };
        string prop  = cmd.GetParam("property", "");
        string kind  = cmd.GetParam("kind", "color");
        string value = cmd.GetParam("value", "");

        try
        {
            switch (kind)
            {
                case "color":
                    if (!ColorUtility.TryParseHtmlString(value, out Color c))
                        return new UCAFResult { success = false, message = $"Bad color: {value}" };
                    mat.SetColor(prop, c); break;
                case "float":
                    mat.SetFloat(prop, float.Parse(value, CultureInfo.InvariantCulture)); break;
                case "vector":
                {
                    var v = ParseFloats(value, 4);
                    mat.SetVector(prop, new Vector4(v[0], v[1], v[2], v[3])); break;
                }
                case "texture":
                {
                    var tex = AssetDatabase.LoadAssetAtPath<Texture>(value);
                    if (tex == null) return new UCAFResult { success = false, message = $"Texture not found: {value}" };
                    mat.SetTexture(prop, tex); break;
                }
                default:
                    return new UCAFResult { success = false, message = $"Unknown kind: {kind}" };
            }
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();
            return new UCAFResult { success = true, message = $"Set {prop} on {mat.name}" };
        }
        catch (Exception ex)
        {
            return new UCAFResult { success = false, message = $"Set failed: {ex.Message}" };
        }
    }
}
