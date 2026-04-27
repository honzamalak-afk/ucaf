using UnityEngine;
using UnityEditor;
using UnityEngine.VFX;
using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Globalization;

public static partial class UCAF_Listener
{
    // ── ShaderGraph JSON manipulation (v4.2 Phase D, FR-160–162) ────────

    // FR-160: get_shadergraph_properties
    static UCAFResult CmdGetShaderGraphProperties(UCAFCommand cmd)
    {
        string assetPath = cmd.GetParam("asset_path", "");
        if (string.IsNullOrEmpty(assetPath))
            return new UCAFResult { success = false, message = "asset_path required (.shadergraph file)." };

        string fullPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), assetPath);
        if (!File.Exists(fullPath))
            return new UCAFResult { success = false, message = $"File not found: {assetPath}" };

        string json = File.ReadAllText(fullPath);
        var result  = new UCAFShaderGraphPropertyList { asset_path = assetPath };

        var typeMatches = Regex.Matches(json,
            @"""\$type""\s*:\s*""UnityEditor\.ShaderGraph\.(\w+ShaderProperty)[^""]*""");

        foreach (Match typeMatch in typeMatches)
        {
            int bs = json.LastIndexOf('{', typeMatch.Index);
            if (bs < 0) continue;
            int depth = 0, be = -1;
            for (int i = bs; i < json.Length; i++)
            {
                if      (json[i] == '{') depth++;
                else if (json[i] == '}') { if (--depth == 0) { be = i; break; } }
            }
            if (be < 0) continue;

            string block    = json.Substring(bs, be - bs + 1);
            string typeName = typeMatch.Groups[1].Value.Replace("ShaderProperty", "");
            string refName  = SgExtractString(block, "m_DefaultReferenceName");
            string ovr      = SgExtractString(block, "m_OverrideReferenceName");

            result.properties.Add(new UCAFShaderGraphProperty {
                name          = string.IsNullOrEmpty(ovr) ? refName : ovr,
                reference     = refName,
                type          = typeName,
                default_value = SgExtractValue(block, typeName)
            });
        }

        result.count = result.properties.Count;
        return new UCAFResult {
            success   = true,
            message   = $"Found {result.count} ShaderGraph properties in '{assetPath}'.",
            data_json = JsonUtility.ToJson(result)
        };
    }

    // FR-161: set_shadergraph_property
    static UCAFResult CmdSetShaderGraphProperty(UCAFCommand cmd)
    {
        string assetPath = cmd.GetParam("asset_path", "");
        string propName  = cmd.GetParam("property",   "");
        string newValue  = cmd.GetParam("value",      "");
        if (string.IsNullOrEmpty(assetPath) || string.IsNullOrEmpty(propName))
            return new UCAFResult { success = false, message = "asset_path and property required." };

        string fullPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), assetPath);
        if (!File.Exists(fullPath))
            return new UCAFResult { success = false, message = $"File not found: {assetPath}" };

        string json = File.ReadAllText(fullPath);
        int blockStart = -1, blockEnd = -1;
        string typeName = "";

        var typeMatches = Regex.Matches(json,
            @"""\$type""\s*:\s*""UnityEditor\.ShaderGraph\.(\w+ShaderProperty)[^""]*""");

        foreach (Match typeMatch in typeMatches)
        {
            int bs = json.LastIndexOf('{', typeMatch.Index);
            if (bs < 0) continue;
            int depth = 0, be = -1;
            for (int i = bs; i < json.Length; i++)
            {
                if      (json[i] == '{') depth++;
                else if (json[i] == '}') { if (--depth == 0) { be = i; break; } }
            }
            if (be < 0) continue;

            string block = json.Substring(bs, be - bs + 1);
            string refName = SgExtractString(block, "m_DefaultReferenceName");
            string ovr     = SgExtractString(block, "m_OverrideReferenceName");
            string display = string.IsNullOrEmpty(ovr) ? refName : ovr;

            if (display == propName || refName == propName)
            {
                blockStart = bs;
                blockEnd   = be;
                typeName   = typeMatch.Groups[1].Value.Replace("ShaderProperty", "");
                break;
            }
        }

        if (blockStart < 0)
            return new UCAFResult { success = false, message = $"Property '{propName}' not found in '{assetPath}'." };

        string block2 = json.Substring(blockStart, blockEnd - blockStart + 1);
        string updated = SgSetValue(block2, typeName, newValue);
        if (updated == null)
            return new UCAFResult { success = false, message = $"Cannot set value '{newValue}' for type '{typeName}'." };

        string updatedJson = json.Substring(0, blockStart) + updated + json.Substring(blockEnd + 1);
        File.WriteAllText(fullPath, updatedJson);
        AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

        return new UCAFResult { success = true, message = $"ShaderGraph property '{propName}' = '{newValue}' in '{assetPath}'." };
    }

    // FR-162: get_shadergraph_info  — returns shader name, keywords, subshader count
    static UCAFResult CmdGetShaderGraphInfo(UCAFCommand cmd)
    {
        string assetPath = cmd.GetParam("asset_path", "");
        if (string.IsNullOrEmpty(assetPath))
            return new UCAFResult { success = false, message = "asset_path required." };

        var shader = AssetDatabase.LoadAssetAtPath<Shader>(assetPath);
        if (shader == null)
            return new UCAFResult { success = false, message = $"Shader not found (or not yet compiled): {assetPath}" };

        return new UCAFResult {
            success = true,
            message = $"Shader '{shader.name}': {shader.GetPropertyCount()} properties, " +
                      $"pass count={shader.passCount}, render queue={shader.renderQueue}."
        };
    }

    // ── ShaderGraph text helpers ─────────────────────────────────────────

    static string SgExtractString(string json, string field)
    {
        var m = Regex.Match(json, $@"""{Regex.Escape(field)}""\s*:\s*""([^""]*)""");
        return m.Success ? m.Groups[1].Value : "";
    }

    static string SgExtractValue(string json, string propType)
    {
        switch (propType)
        {
            case "Vector1":
            {
                var m = Regex.Match(json, @"""m_Value""\s*:\s*(-?[\d.eE+\-]+)");
                return m.Success ? m.Groups[1].Value : "0";
            }
            case "Vector2": case "Vector3": case "Vector4":
            {
                var m = Regex.Match(json, @"""m_Value""\s*:\s*(\{[^}]+\})");
                return m.Success ? m.Groups[1].Value : "{}";
            }
            case "Color":
            {
                var m = Regex.Match(json, @"""m_Value""\s*:\s*(\{[^}]+\})");
                if (!m.Success) return "{}";
                string blk = m.Groups[1].Value;
                string R(string k) { var x = Regex.Match(blk, $@"""{k}""\s*:\s*([\d.]+)"); return x.Success ? x.Groups[1].Value : "0"; }
                return $"rgba({R("r")},{R("g")},{R("b")},{R("a")})";
            }
            case "Boolean":
            {
                var m = Regex.Match(json, @"""m_Value""\s*:\s*(\d+)");
                return m.Success ? (m.Groups[1].Value != "0" ? "true" : "false") : "false";
            }
            default: return "(complex)";
        }
    }

    static string SgSetValue(string json, string propType, string newValue)
    {
        string Fmt(float v) => v.ToString("R", CultureInfo.InvariantCulture);

        switch (propType)
        {
            case "Vector1":
                if (!float.TryParse(newValue, NumberStyles.Float, CultureInfo.InvariantCulture, out float fv)) return null;
                return Regex.Replace(json, @"""m_Value""\s*:\s*-?[\d.eE+\-]+",
                    $@"""m_Value"": {Fmt(fv)}");

            case "Boolean":
                int bv = (newValue == "true" || newValue == "1") ? 1 : 0;
                return Regex.Replace(json, @"""m_Value""\s*:\s*\d+", $@"""m_Value"": {bv}");

            case "Color":
                if (!TryParseColorSg(newValue, out Color c)) return null;
                return Regex.Replace(json, @"""m_Value""\s*:\s*\{[^}]+\}",
                    $@"""m_Value"": {{""r"":{Fmt(c.r)},""g"":{Fmt(c.g)},""b"":{Fmt(c.b)},""a"":{Fmt(c.a)}}}");

            case "Vector2": case "Vector3": case "Vector4":
            {
                var parts = newValue.Split(',');
                float x = 0, y = 0, z = 0, w = 0;
                if (parts.Length >= 1) float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out x);
                if (parts.Length >= 2) float.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out y);
                if (parts.Length >= 3) float.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out z);
                if (parts.Length >= 4) float.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out w);
                string vec = propType == "Vector2"
                    ? $@"{{""x"":{Fmt(x)},""y"":{Fmt(y)}}}"
                    : propType == "Vector3"
                        ? $@"{{""x"":{Fmt(x)},""y"":{Fmt(y)},""z"":{Fmt(z)}}}"
                        : $@"{{""x"":{Fmt(x)},""y"":{Fmt(y)},""z"":{Fmt(z)},""w"":{Fmt(w)}}}";
                return Regex.Replace(json, @"""m_Value""\s*:\s*\{[^}]+\}", $@"""m_Value"": {vec}");
            }
            default: return null;
        }
    }

    static bool TryParseColorSg(string s, out Color color)
    {
        color = Color.white;
        if (ColorUtility.TryParseHtmlString(s, out color)) return true;
        var p = s.Split(',');
        if (p.Length < 3) return false;
        if (float.TryParse(p[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float r) &&
            float.TryParse(p[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float g) &&
            float.TryParse(p[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float b))
        {
            float a = 1f;
            if (p.Length >= 4) float.TryParse(p[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out a);
            color = new Color(r, g, b, a);
            return true;
        }
        return false;
    }

    // ── VFX Graph (v4.2 Phase D, FR-163–164) ────────────────────────────

    // FR-163: set_vfx_property
    static UCAFResult CmdSetVFXProperty(UCAFCommand cmd)
    {
        var obj = ResolveObject(cmd, out string err);
        if (obj == null) return new UCAFResult { success = false, message = err };

        var vfx = obj.GetComponent<VisualEffect>();
        if (vfx == null)
            return new UCAFResult { success = false, message = $"No VisualEffect component on '{obj.name}'." };

        string propName = cmd.GetParam("property", "");
        string typeName = cmd.GetParam("type",     "").ToLowerInvariant();
        string value    = cmd.GetParam("value",    "");
        if (string.IsNullOrEmpty(propName))
            return new UCAFResult { success = false, message = "property required." };

        try
        {
            switch (typeName)
            {
                case "float":
                    if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float fv))
                        vfx.SetFloat(propName, fv);
                    break;
                case "int":
                    if (int.TryParse(value, out int iv)) vfx.SetInt(propName, iv);
                    break;
                case "bool":
                    vfx.SetBool(propName, value == "true" || value == "1");
                    break;
                case "vector2":
                {
                    var p = value.Split(',');
                    float x = 0, y = 0;
                    if (p.Length >= 1) float.TryParse(p[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out x);
                    if (p.Length >= 2) float.TryParse(p[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out y);
                    vfx.SetVector2(propName, new Vector2(x, y));
                    break;
                }
                case "vector3":
                    if (TryParseVector3(value, out Vector3 v3)) vfx.SetVector3(propName, v3);
                    break;
                case "vector4":
                {
                    var p = value.Split(',');
                    float x = 0, y = 0, z = 0, w = 0;
                    if (p.Length >= 1) float.TryParse(p[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out x);
                    if (p.Length >= 2) float.TryParse(p[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out y);
                    if (p.Length >= 3) float.TryParse(p[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out z);
                    if (p.Length >= 4) float.TryParse(p[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out w);
                    vfx.SetVector4(propName, new Vector4(x, y, z, w));
                    break;
                }
                case "color":
                    if (TryParseColorSg(value, out Color col)) vfx.SetVector4(propName, (Vector4)col);
                    break;
                case "texture":
                {
                    var tex = AssetDatabase.LoadAssetAtPath<Texture>(value);
                    if (tex == null) return new UCAFResult { success = false, message = $"Texture not found: {value}" };
                    vfx.SetTexture(propName, tex);
                    break;
                }
                default:
                    if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float dfv))
                        vfx.SetFloat(propName, dfv);
                    else
                        return new UCAFResult { success = false, message = $"Unknown type '{typeName}'. Use: float, int, bool, vector2, vector3, vector4, color, texture." };
                    break;
            }
            EditorUtility.SetDirty(obj);
            return new UCAFResult { success = true, message = $"VFX property '{propName}' = '{value}' on '{obj.name}'." };
        }
        catch (Exception ex)
        {
            return new UCAFResult { success = false, message = $"VFX set failed: {ex.Message}" };
        }
    }

    // FR-164: get_vfx_properties
    static UCAFResult CmdGetVFXProperties(UCAFCommand cmd)
    {
        string assetPath = cmd.GetParam("asset_path", "");
        VisualEffectAsset asset;
        string resolvedPath;

        if (!string.IsNullOrEmpty(assetPath))
        {
            asset = AssetDatabase.LoadAssetAtPath<VisualEffectAsset>(assetPath);
            if (asset == null) return new UCAFResult { success = false, message = $"VisualEffectAsset not found: {assetPath}" };
            resolvedPath = assetPath;
        }
        else
        {
            var go = ResolveObject(cmd, out string err);
            if (go == null) return new UCAFResult { success = false, message = err };
            var vfx = go.GetComponent<VisualEffect>();
            if (vfx == null) return new UCAFResult { success = false, message = $"No VisualEffect on '{go.name}'." };
            asset = vfx.visualEffectAsset;
            if (asset == null) return new UCAFResult { success = false, message = "VisualEffect has no asset assigned." };
            resolvedPath = AssetDatabase.GetAssetPath(asset);
        }

        var exposed = new List<VFXExposedProperty>();
        asset.GetExposedProperties(exposed);

        var result = new UCAFVFXPropertyList { obj_path = resolvedPath };
        foreach (var p in exposed)
            result.properties.Add(new UCAFVFXProperty { name = p.name, type = p.type.Name, value = "" });

        return new UCAFResult {
            success   = true,
            message   = $"Found {result.properties.Count} exposed VFX properties.",
            data_json = JsonUtility.ToJson(result)
        };
    }
}
