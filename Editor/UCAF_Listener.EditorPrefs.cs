using UnityEngine;
using UnityEditor;
using System;
using System.IO;

public static partial class UCAF_Listener
{
    // ── Editor Layout + Preferences (v4.2 Phase E, FR-183 to FR-185) ────────

    static UCAFResult CmdApplyEditorLayout(UCAFCommand cmd)
    {
        if (cmd.GetParam("confirm", "false") != "true")
            return new UCAFResult { success = false, message = "Applying a layout changes the Unity window arrangement. Pass confirm=true to proceed." };

        string path = cmd.GetParam("layout_path", "");
        if (string.IsNullOrEmpty(path))
            return new UCAFResult { success = false, message = "layout_path required (absolute or relative to project root)." };

        if (!Path.IsPathRooted(path))
            path = Path.Combine(Path.GetDirectoryName(Application.dataPath), path);

        if (!File.Exists(path))
            return new UCAFResult { success = false, message = $"Layout file not found: {path}" };

        try
        {
            var type   = Type.GetType("UnityEditor.WindowLayout, UnityEditor")
                      ?? Type.GetType("UnityEditor.WindowLayout, UnityEditor.dll");
            var method = type?.GetMethod("LoadWindowLayout",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                null, new[] { typeof(string), typeof(bool) }, null);

            if (method == null)
                return new UCAFResult { success = false, message = "WindowLayout.LoadWindowLayout not available in this Unity version." };

            bool ok = (bool)method.Invoke(null, new object[] { path, false });
            return ok
                ? new UCAFResult { success = true,  message = $"Layout applied: {Path.GetFileName(path)}" }
                : new UCAFResult { success = false, message = "LoadWindowLayout returned false — layout may be incompatible with this Unity version." };
        }
        catch (Exception ex)
        {
            return new UCAFResult { success = false, message = $"apply_editor_layout failed: {ex.Message}" };
        }
    }

    static UCAFResult CmdSetEditorPref(UCAFCommand cmd)
    {
        string key   = cmd.GetParam("key",   "");
        string value = cmd.GetParam("value", "");
        string type  = cmd.GetParam("type",  "string").ToLower();

        if (string.IsNullOrEmpty(key))
            return new UCAFResult { success = false, message = "key required." };

        try
        {
            switch (type)
            {
                case "int":
                    if (!int.TryParse(value, out int iv))
                        return new UCAFResult { success = false, message = $"Cannot parse '{value}' as int." };
                    EditorPrefs.SetInt(key, iv);
                    break;

                case "float":
                    if (!float.TryParse(value,
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float fv))
                        return new UCAFResult { success = false, message = $"Cannot parse '{value}' as float." };
                    EditorPrefs.SetFloat(key, fv);
                    break;

                case "bool":
                    EditorPrefs.SetBool(key, value == "true" || value == "1");
                    break;

                default: // string
                    EditorPrefs.SetString(key, value);
                    break;
            }
            return new UCAFResult { success = true, message = $"EditorPrefs[{key}] = {value} ({type})" };
        }
        catch (Exception ex)
        {
            return new UCAFResult { success = false, message = $"set_editor_pref failed: {ex.Message}" };
        }
    }

    static UCAFResult CmdGetEditorPref(UCAFCommand cmd)
    {
        string key  = cmd.GetParam("key",     "");
        string type = cmd.GetParam("type",    "string").ToLower();
        string def  = cmd.GetParam("default", "");

        if (string.IsNullOrEmpty(key))
            return new UCAFResult { success = false, message = "key required." };

        try
        {
            string value;
            switch (type)
            {
                case "int":
                    int di = int.TryParse(def, out int dip) ? dip : 0;
                    value  = EditorPrefs.GetInt(key, di).ToString();
                    break;

                case "float":
                    float.TryParse(def,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float dfp);
                    value = EditorPrefs.GetFloat(key, dfp)
                                       .ToString(System.Globalization.CultureInfo.InvariantCulture);
                    break;

                case "bool":
                    value = EditorPrefs.GetBool(key, def == "true" || def == "1") ? "true" : "false";
                    break;

                default:
                    value = EditorPrefs.GetString(key, def);
                    break;
            }
            return new UCAFResult {
                success   = true,
                message   = $"EditorPrefs[{key}] = {value}",
                data_json = $"{{\"key\":\"{EscJ(key)}\",\"value\":\"{EscJ(value)}\",\"type\":\"{type}\"}}"
            };
        }
        catch (Exception ex)
        {
            return new UCAFResult { success = false, message = $"get_editor_pref failed: {ex.Message}" };
        }
    }
}
