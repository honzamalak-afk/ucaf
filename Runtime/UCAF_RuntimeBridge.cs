using UnityEngine;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;

// Runtime bridge for UCAF v4.2 (Phase B, FR-107 to FR-109).
// Lives in a non-Editor assembly so it runs during Play Mode.
// Instantiated automatically when Play Mode starts via UCAF_Listener.
// Communicates via file-based IPC using ucaf_workspace/commands/runtime_*/.
[AddComponentMenu("")]
public class UCAF_RuntimeBridge : MonoBehaviour
{
    // ── Singleton ──────────────────────────────────────────────────────────

    public static UCAF_RuntimeBridge Instance { get; private set; }

    static string _runtimePending;
    static string _runtimeDone;
    static string _signalLog;

    // Signals emitted this session (appended by user code via Signal())
    static readonly List<SignalEntry> _signals = new List<SignalEntry>();

    // ── Serializable types (local to this assembly) ───────────────────────

    [Serializable]
    class RuntimeCmd
    {
        public string id;
        public string type;       // runtime_get | runtime_set | runtime_call
        public string obj_path;
        public string component;
        public string field;
        public string value;
        public string method;
        public string args_json;  // JSON array of string args
    }

    [Serializable]
    class RuntimeResult
    {
        public bool   success;
        public string message;
        public string data;
    }

    [Serializable]
    class SignalEntry
    {
        public string name;
        public string timestamp;
        public int    frame;
    }

    [Serializable]
    class SignalLog
    {
        public List<SignalEntry> signals = new List<SignalEntry>();
    }

    // ── MonoBehaviour lifecycle ─────────────────────────────────────────────

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        string workspacePath = Path.Combine(
            Path.GetDirectoryName(Application.dataPath), "ucaf_workspace");
        _runtimePending = Path.Combine(workspacePath, "commands", "runtime_pending");
        _runtimeDone    = Path.Combine(workspacePath, "commands", "runtime_done");
        _signalLog      = Path.Combine(workspacePath, "commands", "runtime_signals.json");

        Directory.CreateDirectory(_runtimePending);
        Directory.CreateDirectory(_runtimeDone);

        // Clear signal log from previous session
        _signals.Clear();
        SaveSignalLog();

        Debug.Log("[UCAF] RuntimeBridge started.");
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    // ── Poll runtime commands in Update ────────────────────────────────────

    void Update()
    {
        if (!Directory.Exists(_runtimePending)) return;
        string[] files;
        try { files = Directory.GetFiles(_runtimePending, "*.json"); }
        catch { return; }

        foreach (string file in files)
        {
            RuntimeCmd cmd = null;
            try
            {
                string json = File.ReadAllText(file);
                cmd = JsonUtility.FromJson<RuntimeCmd>(json);
                File.Delete(file);
                var result = ExecuteRuntimeCmd(cmd);
                WriteRuntimeResult(cmd.id, result);
            }
            catch (Exception ex)
            {
                string id = cmd?.id ?? Path.GetFileNameWithoutExtension(file);
                WriteRuntimeResult(id, new RuntimeResult { success = false, message = ex.Message });
                try { File.Delete(file); } catch { }
            }
        }
    }

    // ── Command execution ──────────────────────────────────────────────────

    RuntimeResult ExecuteRuntimeCmd(RuntimeCmd cmd)
    {
        switch (cmd.type)
        {
            case "runtime_get":   return CmdRuntimeGet(cmd);
            case "runtime_set":   return CmdRuntimeSet(cmd);
            case "runtime_call":  return CmdRuntimeCall(cmd);
            default: return new RuntimeResult { success = false, message = $"Unknown runtime command: {cmd.type}" };
        }
    }

    RuntimeResult CmdRuntimeGet(RuntimeCmd cmd)
    {
        if (!TryResolveComponentField(cmd, out Component comp, out FieldInfo field, out PropertyInfo prop, out string err))
            return new RuntimeResult { success = false, message = err };

        object value = field != null ? field.GetValue(comp) : prop.GetValue(comp);
        string valueStr = ValueToString(value);

        return new RuntimeResult {
            success = true,
            message = $"{cmd.component}.{cmd.field} = {valueStr}",
            data    = JsonUtility.ToJson(new UCAFRuntimeFieldValueMini {
                obj_path  = cmd.obj_path,
                component = cmd.component,
                field     = cmd.field,
                value     = valueStr,
                type_name = value?.GetType().Name ?? "null"
            })
        };
    }

    RuntimeResult CmdRuntimeSet(RuntimeCmd cmd)
    {
        if (!TryResolveComponentField(cmd, out Component comp, out FieldInfo fi, out PropertyInfo pi, out string err))
            return new RuntimeResult { success = false, message = err };

        Type targetType = fi != null ? fi.FieldType : pi.PropertyType;
        object converted;
        try { converted = ConvertValue(cmd.value, targetType); }
        catch (Exception ex) { return new RuntimeResult { success = false, message = $"Cannot convert '{cmd.value}' to {targetType.Name}: {ex.Message}" }; }

        if (fi != null) fi.SetValue(comp, converted);
        else            pi.SetValue(comp, converted);

        return new RuntimeResult { success = true, message = $"Set {cmd.component}.{cmd.field} = {cmd.value}" };
    }

    RuntimeResult CmdRuntimeCall(RuntimeCmd cmd)
    {
        GameObject go = FindObject(cmd.obj_path);
        if (go == null) return new RuntimeResult { success = false, message = $"Object not found: {cmd.obj_path}" };

        Component comp = go.GetComponent(cmd.component);
        if (comp == null) return new RuntimeResult { success = false, message = $"Component '{cmd.component}' not on '{cmd.obj_path}'" };

        MethodInfo method = comp.GetType().GetMethod(cmd.method,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
        if (method == null) return new RuntimeResult { success = false, message = $"Method '{cmd.method}' not found on {cmd.component}" };

        // Parse args — simple types only
        ParameterInfo[] paramInfos = method.GetParameters();
        object[] args = new object[paramInfos.Length];
        string[] rawArgs = ParseArgsJson(cmd.args_json);
        for (int i = 0; i < paramInfos.Length && i < rawArgs.Length; i++)
        {
            try { args[i] = ConvertValue(rawArgs[i], paramInfos[i].ParameterType); }
            catch (Exception ex) { return new RuntimeResult { success = false, message = $"Arg {i} conversion failed: {ex.Message}" }; }
        }

        try
        {
            object ret    = method.Invoke(comp, args);
            string retStr = ret != null ? ValueToString(ret) : "void";
            return new RuntimeResult { success = true, message = $"Called {cmd.method}, returned: {retStr}", data = retStr };
        }
        catch (TargetInvocationException ex)
        {
            return new RuntimeResult { success = false, message = $"Method threw: {ex.InnerException?.Message ?? ex.Message}" };
        }
    }

    // ── Public API for user code ────────────────────────────────────────────

    // Call this from gameplay scripts to emit a named signal that UCAF can listen for.
    // Example: UCAF_RuntimeBridge.Signal("OnPlayerDied");
    public static void Signal(string name)
    {
        var entry = new SignalEntry {
            name      = name,
            timestamp = DateTime.UtcNow.ToString("o"),
            frame     = Time.frameCount
        };
        _signals.Add(entry);
        SaveSignalLog();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    bool TryResolveComponentField(RuntimeCmd cmd,
        out Component comp, out FieldInfo fi, out PropertyInfo pi, out string err)
    {
        comp = null; fi = null; pi = null;

        if (string.IsNullOrEmpty(cmd.obj_path)) { err = "obj_path required"; return false; }
        if (string.IsNullOrEmpty(cmd.component)) { err = "component required"; return false; }
        if (string.IsNullOrEmpty(cmd.field))     { err = "field required"; return false; }

        GameObject go = FindObject(cmd.obj_path);
        if (go == null) { err = $"Object not found: {cmd.obj_path}"; return false; }

        comp = go.GetComponent(cmd.component);
        if (comp == null) { err = $"Component '{cmd.component}' not on '{cmd.obj_path}'"; return false; }

        Type t = comp.GetType();
        fi = t.GetField(cmd.field, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        pi = fi == null ? t.GetProperty(cmd.field, BindingFlags.Public | BindingFlags.Instance) : null;

        if (fi == null && pi == null)
        { err = $"Field/property '{cmd.field}' not found on {cmd.component}"; return false; }

        err = null;
        return true;
    }

    // Find a runtime GameObject by hierarchy path (e.g. "Player/Body/Mesh")
    static GameObject FindObject(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var go = GameObject.Find(path);
        if (go != null) return go;
        // Also search by name (last segment)
        string name = path.Contains("/") ? path.Substring(path.LastIndexOf('/') + 1) : path;
        return GameObject.Find(name);
    }

    static string ValueToString(object value)
    {
        if (value == null) return "null";
        if (value is Vector3 v3) return $"{v3.x.ToString("R", CultureInfo.InvariantCulture)},{v3.y.ToString("R", CultureInfo.InvariantCulture)},{v3.z.ToString("R", CultureInfo.InvariantCulture)}";
        if (value is Vector2 v2) return $"{v2.x.ToString("R", CultureInfo.InvariantCulture)},{v2.y.ToString("R", CultureInfo.InvariantCulture)}";
        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    static object ConvertValue(string raw, Type t)
    {
        if (t == typeof(string)) return raw;
        if (t == typeof(bool))   return raw == "true" || raw == "1";
        if (t == typeof(int))    return int.Parse(raw, CultureInfo.InvariantCulture);
        if (t == typeof(float))  return float.Parse(raw, CultureInfo.InvariantCulture);
        if (t == typeof(double)) return double.Parse(raw, CultureInfo.InvariantCulture);
        if (t == typeof(Vector3))
        {
            string[] parts = raw.Split(',');
            return new Vector3(
                float.Parse(parts[0].Trim(), CultureInfo.InvariantCulture),
                float.Parse(parts[1].Trim(), CultureInfo.InvariantCulture),
                parts.Length > 2 ? float.Parse(parts[2].Trim(), CultureInfo.InvariantCulture) : 0f);
        }
        if (t == typeof(Vector2))
        {
            string[] parts = raw.Split(',');
            return new Vector2(
                float.Parse(parts[0].Trim(), CultureInfo.InvariantCulture),
                float.Parse(parts[1].Trim(), CultureInfo.InvariantCulture));
        }
        if (t.IsEnum) return Enum.Parse(t, raw, ignoreCase: true);
        return Convert.ChangeType(raw, t, CultureInfo.InvariantCulture);
    }

    static string[] ParseArgsJson(string json)
    {
        if (string.IsNullOrEmpty(json)) return Array.Empty<string>();
        // Minimal JSON string array parse: ["a","b","c"]
        json = json.Trim();
        if (!json.StartsWith("[") || !json.EndsWith("]")) return new[] { json };
        json = json.Substring(1, json.Length - 2).Trim();
        if (string.IsNullOrEmpty(json)) return Array.Empty<string>();
        var result = new List<string>();
        foreach (string part in json.Split(','))
        {
            string s = part.Trim().Trim('"');
            result.Add(s);
        }
        return result.ToArray();
    }

    static void WriteRuntimeResult(string id, RuntimeResult result)
    {
        string path = Path.Combine(_runtimeDone, $"{id}.json");
        File.WriteAllText(path, JsonUtility.ToJson(result));
    }

    static void SaveSignalLog()
    {
        if (string.IsNullOrEmpty(_signalLog)) return;
        try
        {
            var log = new SignalLog();
            log.signals.AddRange(_signals);
            File.WriteAllText(_signalLog, JsonUtility.ToJson(log));
        }
        catch { }
    }

    // Minimal struct to avoid depending on Editor assembly types
    [Serializable]
    class UCAFRuntimeFieldValueMini
    {
        public string obj_path;
        public string component;
        public string field;
        public string value;
        public string type_name;
    }
}
