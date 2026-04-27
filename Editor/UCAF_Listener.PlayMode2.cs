using UnityEngine;
using UnityEditor;
using System;
using System.Globalization;
using System.IO;

public static partial class UCAF_Listener
{
    // ── PlayMode loop (v4.2 Phase B, FR-104 to FR-110) ────────────────────
    //
    // Async pattern: all playmode_enter/exit/run commands return null (deferred).
    // SessionState persists across domain reloads. State machine:
    //   entering → (isPlaying) → waiting → (condition met) → exiting → done

    internal const string SS_PmEnterCmdId    = "UCAF_PM_EnterCmdId";
    internal const string SS_PmEnterStart    = "UCAF_PM_EnterStart";
    internal const string SS_PmSessionId     = "UCAF_PM_SessionId";

    internal const string SS_PmExitCmdId     = "UCAF_PM_ExitCmdId";
    internal const string SS_PmExitStart     = "UCAF_PM_ExitStart";
    internal const string SS_PmExitSessionId = "UCAF_PM_ExitSessionId";
    internal const string SS_PmExitFrames    = "UCAF_PM_ExitFrames";

    internal const string SS_PmRunCmdId      = "UCAF_PM_RunCmdId";
    internal const string SS_PmRunPhase      = "UCAF_PM_RunPhase";   // entering|waiting|exiting
    internal const string SS_PmRunSessionId  = "UCAF_PM_RunSessionId";
    internal const string SS_PmRunWaitFor    = "UCAF_PM_RunWaitFor";
    internal const string SS_PmRunStartTime  = "UCAF_PM_RunStartTime";
    internal const string SS_PmRunEntryFrame = "UCAF_PM_RunEntryFrame";
    internal const string SS_PmRunFrames     = "UCAF_PM_RunFrames";

    // FR-182 console auto-subscribe, FR-191 record_video
    internal const string SS_PmRunRecordVideo  = "UCAF_PM_RunRecordVideo";
    internal const string SS_PmRunRecSessionId = "UCAF_PM_RunRecSessionId";

    // Async proxy for runtime_get/set/call (avoids Thread.Sleep deadlock)
    internal const string SS_PmProxyOuterCmdId = "UCAF_PM_ProxyOuterCmdId";
    internal const string SS_PmProxyDoneFile   = "UCAF_PM_ProxyDoneFile";
    internal const string SS_PmProxyStartTime  = "UCAF_PM_ProxyStartTime";
    internal const string SS_PmRunTimeout    = "UCAF_PM_RunTimeout";

    // ── playmode_enter ─────────────────────────────────────────────────────

    static UCAFResult CmdPlayModeEnter(UCAFCommand cmd)
    {
        if (EditorApplication.isPlaying)
            return new UCAFResult { success = false, message = "Already in Play Mode. Call playmode_exit first." };
        if (!string.IsNullOrEmpty(SessionState.GetString(SS_PmEnterCmdId, "")))
            return new UCAFResult { success = false, message = "Another playmode_enter is already pending." };

        string sessionId = $"pm_{cmd.id}";
        SessionState.SetString(SS_PmEnterCmdId, cmd.id);
        SessionState.SetString(SS_PmEnterStart,  DateTime.UtcNow.ToString("o"));
        SessionState.SetString(SS_PmSessionId,   sessionId);

        EditorApplication.delayCall += () => { EditorApplication.isPlaying = true; };
        return null;
    }

    internal static void CheckPendingPlayModeEnter()
    {
        string id = SessionState.GetString(SS_PmEnterCmdId, "");
        if (string.IsNullOrEmpty(id)) return;

        if (!EditorApplication.isPlaying)
        {
            // Check if enough time has passed without entering — user may have cancelled
            string startStr2 = SessionState.GetString(SS_PmEnterStart, "");
            if (DateTime.TryParse(startStr2, null, DateTimeStyles.RoundtripKind, out DateTime t2) &&
                (DateTime.UtcNow - t2).TotalSeconds > 15.0)
            {
                SessionState.EraseString(SS_PmEnterCmdId);
                SessionState.EraseString(SS_PmEnterStart);
                WriteResult(DonePath, id, new UCAFResult { success = false, message = "playmode_enter timed out (15s) — play mode never became active." });
            }
            return; // still waiting to enter
        }

        string startStr = SessionState.GetString(SS_PmEnterStart, "");
        DateTime.TryParse(startStr, null, DateTimeStyles.RoundtripKind, out DateTime start);
        float elapsed = start != DateTime.MinValue ? (float)(DateTime.UtcNow - start).TotalSeconds : 0f;
        string sessionId = SessionState.GetString(SS_PmSessionId, id);

        SessionState.EraseString(SS_PmEnterCmdId);
        SessionState.EraseString(SS_PmEnterStart);

        var payload = new UCAFPlayModeResult {
            playmode_session_id = sessionId,
            frames              = Time.frameCount,
            elapsed_seconds     = elapsed
        };
        WriteResult(DonePath, id, new UCAFResult {
            success   = true,
            message   = $"Entered Play Mode (session: {sessionId})",
            data_json = JsonUtility.ToJson(payload)
        });
    }

    // ── playmode_exit ──────────────────────────────────────────────────────

    static UCAFResult CmdPlayModeExit(UCAFCommand cmd)
    {
        if (!EditorApplication.isPlaying)
            return new UCAFResult { success = false, message = "Not in Play Mode." };
        if (!string.IsNullOrEmpty(SessionState.GetString(SS_PmExitCmdId, "")))
            return new UCAFResult { success = false, message = "Another playmode_exit is already pending." };

        SessionState.SetString(SS_PmExitCmdId,     cmd.id);
        SessionState.SetString(SS_PmExitStart,      DateTime.UtcNow.ToString("o"));
        SessionState.SetString(SS_PmExitSessionId,  SessionState.GetString(SS_PmSessionId, "unknown"));
        SessionState.SetInt   (SS_PmExitFrames,     Time.frameCount);

        EditorApplication.delayCall += () => { EditorApplication.isPlaying = false; };
        return null;
    }

    internal static void CheckPendingPlayModeExit()
    {
        string id = SessionState.GetString(SS_PmExitCmdId, "");
        if (string.IsNullOrEmpty(id)) return;

        if (EditorApplication.isPlaying) return; // still exiting

        string startStr = SessionState.GetString(SS_PmExitStart, "");
        DateTime.TryParse(startStr, null, DateTimeStyles.RoundtripKind, out DateTime start);
        float elapsed   = start != DateTime.MinValue ? (float)(DateTime.UtcNow - start).TotalSeconds : 0f;
        int   frames    = Time.frameCount - SessionState.GetInt(SS_PmExitFrames, 0);
        string sessionId = SessionState.GetString(SS_PmExitSessionId, "unknown");

        SessionState.EraseString(SS_PmExitCmdId);
        SessionState.EraseString(SS_PmExitStart);
        SessionState.EraseString(SS_PmExitSessionId);
        SessionState.EraseInt   (SS_PmExitFrames);
        SessionState.EraseString(SS_PmSessionId);

        var payload = new UCAFPlayModeResult {
            playmode_session_id = sessionId,
            frames              = frames,
            elapsed_seconds     = elapsed
        };
        WriteResult(DonePath, id, new UCAFResult {
            success   = true,
            message   = $"Exited Play Mode ({frames} frames, {elapsed:F1}s)",
            data_json = JsonUtility.ToJson(payload)
        });
    }

    // ── playmode_run ───────────────────────────────────────────────────────
    // High-level: enter + wait_for + exit in one command.
    // wait_for formats: "time:5", "frames:120", "event:NAME", "predicate:OBJ.COMP:FIELD>=VALUE"

    static UCAFResult CmdPlayModeRun(UCAFCommand cmd)
    {
        if (EditorApplication.isPlaying)
            return new UCAFResult { success = false, message = "Already in Play Mode. Exit first or use playmode_enter/exit directly." };
        if (!string.IsNullOrEmpty(SessionState.GetString(SS_PmRunCmdId, "")))
            return new UCAFResult { success = false, message = "Another playmode_run is already pending." };

        string waitFor  = cmd.GetParam("wait_for", "time:5");
        float timeoutS  = float.TryParse(cmd.GetParam("timeout_seconds", "60"),
                              NumberStyles.Float, CultureInfo.InvariantCulture, out float t) ? t : 60f;
        bool recordVideo = cmd.GetParam("record_video", "false") == "true";
        string sessionId = $"run_{cmd.id}";

        SessionState.SetString(SS_PmRunCmdId,       cmd.id);
        SessionState.SetString(SS_PmRunPhase,        "entering");
        SessionState.SetString(SS_PmRunSessionId,    sessionId);
        SessionState.SetString(SS_PmRunWaitFor,      waitFor);
        SessionState.SetString(SS_PmRunStartTime,    DateTime.UtcNow.ToString("o"));
        SessionState.SetInt   (SS_PmRunEntryFrame,   Time.frameCount);
        SessionState.SetFloat (SS_PmRunTimeout,      timeoutS);
        SessionState.SetInt   (SS_PmRunFrames,       0);
        SessionState.SetString(SS_PmRunRecordVideo,  recordVideo ? "true" : "false");
        SessionState.SetString(SS_PmRunRecSessionId, "");

        SessionState.SetString(SS_PmSessionId, sessionId);

        // FR-182: always create a console auto-subscription for this run
        ConsoleAutoSubscribe(sessionId);

        EditorApplication.delayCall += () => { EditorApplication.isPlaying = true; };
        return null;
    }

    internal static void CheckPendingPlayModeRun()
    {
        string id = SessionState.GetString(SS_PmRunCmdId, "");
        if (string.IsNullOrEmpty(id)) return;

        string phase    = SessionState.GetString(SS_PmRunPhase, "");
        string waitFor  = SessionState.GetString(SS_PmRunWaitFor, "time:5");
        string startStr = SessionState.GetString(SS_PmRunStartTime, "");
        float  timeout  = SessionState.GetFloat(SS_PmRunTimeout, 60f);
        DateTime.TryParse(startStr, null, DateTimeStyles.RoundtripKind, out DateTime start);
        float elapsed   = start != DateTime.MinValue ? (float)(DateTime.UtcNow - start).TotalSeconds : 0f;

        if (elapsed > timeout)
        {
            // Timeout — force exit
            if (EditorApplication.isPlaying)
                EditorApplication.isPlaying = false;
            if (!EditorApplication.isPlaying)
                FinishPlayModeRun(id, conditionMet: false, timedOut: true, elapsed);
            return;
        }

        switch (phase)
        {
            case "entering":
                if (EditorApplication.isPlaying)
                {
                    SessionState.SetString(SS_PmRunPhase,      "waiting");
                    SessionState.SetInt   (SS_PmRunEntryFrame, Time.frameCount);

                    // FR-191: start recording if requested
                    if (SessionState.GetString(SS_PmRunRecordVideo, "") == "true")
                    {
                        string sId = SessionState.GetString(SS_PmRunSessionId, id);
                        string recId = $"rec_{sId}";
                        string recErr = StartRecordingInternal(recId, "png_sequence", 30, 1920, 1080);
                        if (recErr == null)
                            SessionState.SetString(SS_PmRunRecSessionId, recId);
                        else
                            Debug.LogWarning($"[UCAF] recording_start failed: {recErr}");
                    }
                }
                break;

            case "waiting":
                if (EvaluateWaitFor(waitFor, elapsed, id))
                {
                    SessionState.SetString(SS_PmRunPhase, "exiting");
                    EditorApplication.isPlaying = false;
                }
                break;

            case "exiting":
                if (!EditorApplication.isPlaying)
                    FinishPlayModeRun(id, conditionMet: true, timedOut: false, elapsed);
                break;
        }
    }

    static bool EvaluateWaitFor(string waitFor, float elapsed, string cmdId)
    {
        if (string.IsNullOrEmpty(waitFor)) return true;

        int colonIdx = waitFor.IndexOf(':');
        if (colonIdx < 0) return true;

        string kind  = waitFor.Substring(0, colonIdx).ToLowerInvariant();
        string param = waitFor.Substring(colonIdx + 1);

        switch (kind)
        {
            case "time":
                return float.TryParse(param, NumberStyles.Float, CultureInfo.InvariantCulture, out float secs)
                    && elapsed >= secs;

            case "frames":
            {
                int entryFrame = SessionState.GetInt(SS_PmRunEntryFrame, 0);
                return int.TryParse(param, out int targetFrames)
                    && (Time.frameCount - entryFrame) >= targetFrames;
            }

            case "event":
                return CheckRuntimeSignal(param);

            case "predicate":
                return EvaluateRuntimePredicate(param);

            default:
                return true;
        }
    }

    static bool CheckRuntimeSignal(string signalName)
    {
        string signalLogPath = Path.Combine(WorkspacePath, "commands", "runtime_signals.json");
        if (!File.Exists(signalLogPath)) return false;
        try
        {
            string json = File.ReadAllText(signalLogPath);
            return json.Contains($"\"name\":\"{signalName}\"") ||
                   json.Contains($"\"name\": \"{signalName}\"");
        }
        catch { return false; }
    }

    static bool EvaluateRuntimePredicate(string predicate)
    {
        // Format: "ObjName/Component:field OP value" e.g. "Player/PlayerHealth:health<=0"
        // We issue a runtime_get and check the result synchronously (file already written)
        // This is best-effort: returns false if runtime bridge hasn't responded yet
        string runtimeDone = Path.Combine(WorkspacePath, "commands", "runtime_done");
        string requestPath = Path.Combine(WorkspacePath, "commands", "runtime_pending");

        // Parse: find operator
        string[] operators = { "<=", ">=", "==", "!=", "<", ">" };
        string op = null; int opIdx = -1;
        foreach (string o in operators)
        {
            int i = predicate.IndexOf(o, StringComparison.Ordinal);
            if (i > 0 && (opIdx < 0 || i < opIdx)) { op = o; opIdx = i; }
        }
        if (op == null || opIdx < 0) return false;

        string lhs = predicate.Substring(0, opIdx).Trim();
        string rhs = predicate.Substring(opIdx + op.Length).Trim();

        // Parse lhs: ObjPath:ComponentField  or  ObjPath/Component:field
        int colonPos = lhs.LastIndexOf(':');
        if (colonPos < 0) return false;
        string objComp = lhs.Substring(0, colonPos);
        string fieldName = lhs.Substring(colonPos + 1);
        int slashPos = objComp.LastIndexOf('/');
        string objPath   = slashPos >= 0 ? objComp.Substring(0, slashPos) : objComp;
        string compName  = slashPos >= 0 ? objComp.Substring(slashPos + 1) : objComp;

        // Write a runtime_get request
        string pollId   = $"pred_{DateTime.UtcNow.Ticks}";
        string pollFile = Path.Combine(requestPath, $"{pollId}.json");
        string doneFile = Path.Combine(runtimeDone, $"{pollId}.json");

        // Use manual JSON — JsonUtility.ToJson() does not serialize anonymous types
        string reqJson = $"{{\"id\":\"{pollId}\",\"type\":\"runtime_get\"," +
                         $"\"obj_path\":\"{EscJ(objPath)}\",\"component\":\"{EscJ(compName)}\"," +
                         $"\"field\":\"{EscJ(fieldName)}\",\"value\":\"\",\"method\":\"\",\"args_json\":\"\"}}";
        File.WriteAllText(pollFile, reqJson);

        // Wait up to 100ms for response (we're on main thread, just spin for a few ticks)
        // In practice the bridge runs on the same main thread so it can't respond this tick.
        // We return false here and the next tick will find the result.
        if (!File.Exists(doneFile)) return false;

        try
        {
            string resultJson = File.ReadAllText(doneFile);
            File.Delete(doneFile);
            // Extract data field (minimal JSON parse)
            int valueIdx = resultJson.IndexOf("\"value\":", StringComparison.Ordinal);
            if (valueIdx < 0) return false;
            int start  = resultJson.IndexOf('"', valueIdx + 8) + 1;
            int end    = resultJson.IndexOf('"', start);
            string val = resultJson.Substring(start, end - start);

            return EvalComparison(val, op, rhs);
        }
        catch { return false; }
    }

    static bool EvalComparison(string lhs, string op, string rhs)
    {
        if (float.TryParse(lhs, NumberStyles.Float, CultureInfo.InvariantCulture, out float lf) &&
            float.TryParse(rhs, NumberStyles.Float, CultureInfo.InvariantCulture, out float rf))
        {
            return op switch {
                "<=" => lf <= rf,
                ">=" => lf >= rf,
                "==" => Math.Abs(lf - rf) < 1e-6f,
                "!=" => Math.Abs(lf - rf) >= 1e-6f,
                "<"  => lf < rf,
                ">"  => lf > rf,
                _    => false
            };
        }
        // String comparison
        int cmp = string.Compare(lhs, rhs, StringComparison.Ordinal);
        return op switch { "==" => cmp == 0, "!=" => cmp != 0, "<" => cmp < 0, ">" => cmp > 0, "<=" => cmp <= 0, ">=" => cmp >= 0, _ => false };
    }

    static void FinishPlayModeRun(string id, bool conditionMet, bool timedOut, float elapsed)
    {
        int frames   = Time.frameCount - SessionState.GetInt(SS_PmRunEntryFrame, 0);
        string sId   = SessionState.GetString(SS_PmRunSessionId, id);
        string waitFor = SessionState.GetString(SS_PmRunWaitFor, "");

        // Read signals emitted
        string signalLogPath = Path.Combine(WorkspacePath, "commands", "runtime_signals.json");
        var signalNames = new System.Collections.Generic.List<string>();
        try
        {
            if (File.Exists(signalLogPath))
            {
                string sj = File.ReadAllText(signalLogPath);
                // Minimal: extract "name" values
                int pos = 0;
                while ((pos = sj.IndexOf("\"name\":", pos)) >= 0)
                {
                    int s = sj.IndexOf('"', pos + 7) + 1;
                    int e = sj.IndexOf('"', s);
                    if (s > 0 && e > s) signalNames.Add(sj.Substring(s, e - s));
                    pos = e + 1;
                }
            }
        }
        catch { }

        // FR-191: stop recording and extract frames
        string recSessionId = SessionState.GetString(SS_PmRunRecSessionId, "");
        var recordingFrames = new System.Collections.Generic.List<string>();
        if (!string.IsNullOrEmpty(recSessionId) && _recController != null && _recController.IsRecording())
        {
            StopRecordingInternal();
            recordingFrames = ExtractFramesAllEvents(recSessionId, 5);
        }

        // FR-182: close console auto-subscription
        string consolePath = ConsoleAutoUnsubscribe(sId);

        SessionState.EraseString(SS_PmRunCmdId);
        SessionState.EraseString(SS_PmRunPhase);
        SessionState.EraseString(SS_PmRunSessionId);
        SessionState.EraseString(SS_PmRunWaitFor);
        SessionState.EraseString(SS_PmRunStartTime);
        SessionState.EraseInt   (SS_PmRunEntryFrame);
        SessionState.EraseFloat (SS_PmRunTimeout);
        SessionState.EraseString(SS_PmRunRecordVideo);
        SessionState.EraseString(SS_PmRunRecSessionId);
        SessionState.EraseString(SS_PmSessionId);

        var payload = new UCAFPlayModeRunResult {
            playmode_session_id  = sId,
            frames               = frames,
            elapsed_seconds      = elapsed,
            condition_met        = conditionMet,
            wait_for             = waitFor,
            signals_emitted      = signalNames,
            console_stream_path  = consolePath ?? "",
            recording_session_id = recSessionId,
            recording_frames     = recordingFrames
        };

        string msg = timedOut
            ? $"PlayMode run timed out after {elapsed:F1}s ({frames} frames)"
            : $"PlayMode run complete: {elapsed:F1}s, {frames} frames, condition_met={conditionMet}";

        WriteResult(DonePath, id, new UCAFResult {
            success   = conditionMet,
            message   = msg,
            data_json = JsonUtility.ToJson(payload)
        });
    }

    // ── playmode_runtime_get / set / call (proxy to RuntimeBridge) ──────────
    // Writes a command to runtime_pending/, waits for result in runtime_done/.
    // Claude calls these while in Play Mode to inspect/mutate live objects.

    static UCAFResult CmdPlayModeRuntimeGet(UCAFCommand cmd)
        => ProxyRuntimeCommand("runtime_get", cmd);

    static UCAFResult CmdPlayModeRuntimeSet(UCAFCommand cmd)
        => ProxyRuntimeCommand("runtime_set", cmd);

    static UCAFResult CmdPlayModeRuntimeCall(UCAFCommand cmd)
        => ProxyRuntimeCommand("runtime_call", cmd);

    static UCAFResult ProxyRuntimeCommand(string runtimeType, UCAFCommand cmd)
    {
        if (!EditorApplication.isPlaying)
            return new UCAFResult { success = false, message = "Not in Play Mode. Call playmode_enter first." };

        if (!string.IsNullOrEmpty(SessionState.GetString(SS_PmProxyOuterCmdId, "")))
            return new UCAFResult { success = false, message = "Another runtime proxy command is already pending." };

        string requestDir = Path.Combine(WorkspacePath, "commands", "runtime_pending");
        string doneDir    = Path.Combine(WorkspacePath, "commands", "runtime_done");
        Directory.CreateDirectory(requestDir);
        Directory.CreateDirectory(doneDir);

        string objPath   = cmd.GetParam("obj_path", "");
        string component = cmd.GetParam("component", "");
        string field     = cmd.GetParam("field", "");
        string value     = cmd.GetParam("value", "");
        string method    = cmd.GetParam("method", "");
        string argsJson  = cmd.GetParam("args_json", "");

        string rtJson = $"{{\"id\":\"{cmd.id}\",\"type\":\"{runtimeType}\"," +
                        $"\"obj_path\":\"{EscJ(objPath)}\",\"component\":\"{EscJ(component)}\"," +
                        $"\"field\":\"{EscJ(field)}\",\"value\":\"{EscJ(value)}\"," +
                        $"\"method\":\"{EscJ(method)}\",\"args_json\":\"{EscJ(argsJson)}\"}}";

        File.WriteAllText(Path.Combine(requestDir, $"{cmd.id}.json"), rtJson);

        // Async — RuntimeBridge.Update() processes on same main thread, can't spin-wait here.
        // CheckPendingRuntimeProxy polls for the done file each Editor tick.
        string doneFile = Path.Combine(doneDir, $"{cmd.id}.json");
        SessionState.SetString(SS_PmProxyOuterCmdId, cmd.id);
        SessionState.SetString(SS_PmProxyDoneFile,   doneFile);
        SessionState.SetString(SS_PmProxyStartTime,  DateTime.UtcNow.ToString("o"));
        return null;
    }

    internal static void CheckPendingRuntimeProxy()
    {
        string outerId = SessionState.GetString(SS_PmProxyOuterCmdId, "");
        if (string.IsNullOrEmpty(outerId)) return;

        // 5-second timeout
        string startStr = SessionState.GetString(SS_PmProxyStartTime, "");
        if (DateTime.TryParse(startStr, null, DateTimeStyles.RoundtripKind, out DateTime start) &&
            (DateTime.UtcNow - start).TotalSeconds > 5.0)
        {
            SessionState.EraseString(SS_PmProxyOuterCmdId);
            SessionState.EraseString(SS_PmProxyDoneFile);
            SessionState.EraseString(SS_PmProxyStartTime);
            WriteResult(DonePath, outerId, new UCAFResult {
                success = false,
                message = "RuntimeBridge did not respond within 5s. Is it running? (Enter Play Mode first)"
            });
            return;
        }

        string doneFile = SessionState.GetString(SS_PmProxyDoneFile, "");
        if (!File.Exists(doneFile)) return;

        SessionState.EraseString(SS_PmProxyOuterCmdId);
        SessionState.EraseString(SS_PmProxyDoneFile);
        SessionState.EraseString(SS_PmProxyStartTime);

        try
        {
            string resultJson = File.ReadAllText(doneFile);
            File.Delete(doneFile);
            bool success  = resultJson.Contains("\"success\":true");
            int msgStart  = resultJson.IndexOf("\"message\":\"") + 11;
            int msgEnd    = resultJson.IndexOf('"', msgStart);
            string msg    = msgStart > 11 && msgEnd > msgStart ? resultJson.Substring(msgStart, msgEnd - msgStart) : "";
            int dataStart = resultJson.IndexOf("\"data\":\"") + 8;
            int dataEnd   = resultJson.IndexOf('"', dataStart);
            string data   = dataStart > 8 && dataEnd > dataStart ? resultJson.Substring(dataStart, dataEnd - dataStart) : "";
            WriteResult(DonePath, outerId, new UCAFResult { success = success, message = msg, data_json = data });
        }
        catch (Exception ex)
        {
            WriteResult(DonePath, outerId, new UCAFResult { success = false, message = $"Failed to read RuntimeBridge result: {ex.Message}" });
        }
    }

    static string EscJ(string s) => s?.Replace("\\", "\\\\").Replace("\"", "\\\"") ?? "";

    // ── playmode_signal_subscribe ──────────────────────────────────────────

    static UCAFResult CmdPlayModeSignalSubscribe(UCAFCommand cmd)
    {
        if (!EditorApplication.isPlaying)
            return new UCAFResult { success = false, message = "Must be in Play Mode to subscribe to signals." };

        string signalLogPath = Path.Combine(WorkspacePath, "commands", "runtime_signals.json");
        // Clear existing signal log so we start fresh for this subscription
        File.WriteAllText(signalLogPath, "{\"signals\":[]}");

        return new UCAFResult {
            success   = true,
            message   = $"Signal log cleared and ready. Use UCAF_RuntimeBridge.Signal(\"name\") in gameplay code.",
            data_json = $"{{\"signal_log_path\":\"{signalLogPath.Replace("\\", "\\\\")}\"}}",
        };
    }
}
