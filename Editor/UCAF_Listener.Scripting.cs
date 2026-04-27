using UnityEngine;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

public static partial class UCAF_Listener
{
    // ── Script file ops ─────────────────────────────────────────────────

    static UCAFResult CmdCreateScript(UCAFCommand cmd)
    {
        string className = cmd.GetParam("class_name", "NewScript");
        string content   = cmd.GetParam("content", "");
        string relDir    = cmd.GetParam("folder", "Scripts");

        string dir = Path.Combine(Application.dataPath, relDir);
        Directory.CreateDirectory(dir);
        string filePath = Path.Combine(dir, $"{className}.cs");
        File.WriteAllText(filePath, content);
        AssetDatabase.Refresh();
        return new UCAFResult { success = true, message = $"Script written: Assets/{relDir}/{className}.cs" };
    }

    static UCAFResult CmdAttachScript(UCAFCommand cmd)
    {
        var obj = ResolveObject(cmd, out string err);
        if (obj == null) return new UCAFResult { success = false, message = err };
        string className = cmd.GetParam("class_name", "");
        var type = UCAF_Tools.FindTypeByName(className);
        if (type == null)
            return new UCAFResult { success = false, message = $"Script type not found: {className}. Run compile_and_wait first?" };
        obj.AddComponent(type);
        EditorSceneManager.MarkSceneDirty(obj.scene);
        return new UCAFResult { success = true, message = $"Attached {className} to {obj.name}" };
    }

    static UCAFResult CmdCompileCheck(UCAFCommand cmd)
    {
        CompilationPipeline.RequestScriptCompilation();
        return new UCAFResult { success = true, message = "Compilation requested (fire-and-forget; use compile_and_wait for errors)." };
    }

    static UCAFResult CmdAssetRefresh(UCAFCommand cmd)
    {
        AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
        return new UCAFResult { success = true, message = "AssetDatabase refreshed." };
    }

    // ── compile_and_wait ────────────────────────────────────────────────
    //
    // Async pattern: command returns null (deferred). SessionState holds the
    // pending command ID across domain reload. CheckPendingCompile runs every
    // editor tick; when compilation finishes it writes the result file.

    static UCAFResult CmdCompileAndWait(UCAFCommand cmd)
    {
        string existing = SessionState.GetString(SS_PendingCompileId, "");
        if (!string.IsNullOrEmpty(existing))
            return new UCAFResult {
                success = false,
                message = $"Another compile_and_wait already in progress (id={existing})"
            };

        long offset = 0;
        try { if (File.Exists(LogFilePath)) offset = new FileInfo(LogFilePath).Length; } catch { }

        int timeoutSec = int.TryParse(cmd.GetParam("timeout", "45"), out int t) ? t : 45;
        bool verbose = cmd.GetParam("verbose", "false") == "true";

        SessionState.SetString(SS_PendingCompileId,    cmd.id);
        SessionState.SetString(SS_PendingCompileStart, DateTime.UtcNow.ToString("o"));
        SessionState.SetInt   (SS_PendingTimeout,      timeoutSec);
        SessionState.SetString(SS_PendingLogOffset,    offset.ToString(CultureInfo.InvariantCulture));
        SessionState.SetBool  ("UCAF_PendingCompileVerbose", verbose);

        // Defer the actual request so Unity is not mid-poll when it starts
        EditorApplication.delayCall += () => {
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            CompilationPipeline.RequestScriptCompilation();
        };
        return null;
    }

    static void CheckPendingCompile()
    {
        string pendingId = SessionState.GetString(SS_PendingCompileId, "");
        if (string.IsNullOrEmpty(pendingId)) return;

        if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;

        string startStr = SessionState.GetString(SS_PendingCompileStart, "");
        int timeoutSec  = SessionState.GetInt(SS_PendingTimeout, 45);
        DateTime start  = DateTime.MinValue;
        DateTime.TryParse(startStr, null, DateTimeStyles.RoundtripKind, out start);

        double elapsed = start != DateTime.MinValue
            ? (DateTime.UtcNow - start).TotalSeconds : 0;

        // Grace period: isCompiling may be false for a moment right after
        // RequestScriptCompilation before Unity actually begins.
        if (elapsed < 1.5) return;

        bool timedOut = elapsed > timeoutSec;
        FinishPendingCompile(pendingId, start, timedOut);
    }

    static void FinishPendingCompile(string id, DateTime start, bool timedOut)
    {
        long offset = 0;
        long.TryParse(SessionState.GetString(SS_PendingLogOffset, "0"),
                      NumberStyles.Integer, CultureInfo.InvariantCulture, out offset);

        var errors   = new List<UCAFLogEntry>();
        var warnings = new List<UCAFLogEntry>();

        try
        {
            if (File.Exists(LogFilePath))
            {
                using (var fs = new FileStream(LogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (offset < fs.Length) fs.Position = offset;
                    using (var sr = new StreamReader(fs))
                    {
                        while (!sr.EndOfStream)
                        {
                            string line = sr.ReadLine();
                            if (string.IsNullOrEmpty(line)) continue;
                            UCAFLogEntry e;
                            try { e = JsonUtility.FromJson<UCAFLogEntry>(line); }
                            catch { continue; }
                            if (e == null) continue;
                            if (e.type == "Error" || e.type == "Exception" || e.type == "Assert")
                                errors.Add(e);
                            else if (e.type == "Warning")
                                warnings.Add(e);
                        }
                    }
                }
            }
        }
        catch { /* don't let log-read errors block result delivery */ }

        int elapsedMs = 0;
        if (start != DateTime.MinValue) elapsedMs = (int)(DateTime.UtcNow - start).TotalMilliseconds;

        bool verbose = SessionState.GetBool("UCAF_PendingCompileVerbose", false);
        SessionState.EraseBool("UCAF_PendingCompileVerbose");

        // By default only emit first 3 warnings — full list via verbose=true
        var warningsSample = verbose ? warnings : warnings.Take(3).ToList();

        var payload = new UCAFCompileResult {
            has_errors    = errors.Count > 0,
            error_count   = errors.Count,
            warning_count = warnings.Count,
            errors        = errors,
            warnings      = warningsSample,
            elapsed_ms    = elapsedMs,
        };

        var result = new UCAFResult {
            success = !timedOut && errors.Count == 0,
            message = timedOut
                ? $"Compile timed out after {elapsedMs}ms"
                : (errors.Count == 0
                    ? $"Compiled OK ({warnings.Count} warnings, {elapsedMs}ms)"
                    : $"Compile failed: {errors.Count} errors, {warnings.Count} warnings ({elapsedMs}ms)"),
            data_json = JsonUtility.ToJson(payload)
        };

        // v4.1: edit_file compile=true merges the edit payload into the final result
        string editPayloadJson = SessionState.GetString("UCAF_PendingEditPayload", "");
        if (!string.IsNullOrEmpty(editPayloadJson))
        {
            try
            {
                var editPayload = JsonUtility.FromJson<UCAFEditFileResult>(editPayloadJson);
                editPayload.compiled = true;
                editPayload.compile_has_errors = payload.has_errors;
                editPayload.compile_error_count = payload.error_count;
                editPayload.compile_warning_count = payload.warning_count;
                result.data_json = JsonUtility.ToJson(editPayload);
                if (payload.has_errors)
                    result.message = $"Edited {editPayload.asset_path} ({editPayload.replacements} replacement(s)) — {result.message}";
                else
                    result.message = $"Edited {editPayload.asset_path} ({editPayload.replacements} replacement(s)), compiled OK ({elapsedMs}ms)";
            }
            catch { }
            SessionState.EraseString("UCAF_PendingEditPayload");
        }

        try { File.WriteAllText(Path.Combine(DonePath, $"{id}.json"), JsonUtility.ToJson(result)); }
        catch (Exception ex)
        {
            Debug.LogError($"[UCAF] Failed to write compile result: {ex.Message}");
        }

        SessionState.EraseString(SS_PendingCompileId);
        SessionState.EraseString(SS_PendingCompileStart);
        SessionState.EraseInt(SS_PendingTimeout);
        SessionState.EraseString(SS_PendingLogOffset);
    }
}
