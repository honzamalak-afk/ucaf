using UnityEngine;
using UnityEditor;
using System;
using System.IO;
using System.Collections.Generic;

[InitializeOnLoad]
public static partial class UCAF_Listener
{
    internal static readonly string WorkspacePath = Path.Combine(
        Path.GetDirectoryName(Application.dataPath), "ucaf_workspace");

    // Declared here (not in BugLedger.cs) to guarantee initialization after WorkspacePath
    internal static readonly string MemoryDir     = Path.Combine(WorkspacePath, "memory");
    internal static readonly string MemoryArchive = Path.Combine(WorkspacePath, "memory", "archive");

    internal static readonly string PendingPath;
    internal static readonly string DonePath;
    internal static readonly string ErrorsPath;
    internal static readonly string LogFilePath;

    const double PollInterval = 0.5;
    static double _lastPollTime;

    static readonly List<UCAFLogEntry> _logBuffer = new List<UCAFLogEntry>();
    const int LogBufferMax = 1000;

    internal const string SS_PendingCompileId    = "UCAF_PendingCompileId";
    internal const string SS_PendingCompileStart = "UCAF_PendingCompileStart";
    internal const string SS_PendingTimeout      = "UCAF_PendingCompileTimeout";
    internal const string SS_PendingLogOffset    = "UCAF_PendingCompileLogOffset";

    internal const string SS_PendingScreenshotId    = "UCAF_PendingScreenshotId";
    internal const string SS_PendingScreenshotPath  = "UCAF_PendingScreenshotPath";
    internal const string SS_PendingScreenshotStart = "UCAF_PendingScreenshotStart";

    // v4.2 Phase A — SessionState keys (defined in respective partial files)
    // SS_PendingBuildTargetId / SS_PendingBuildTargetName — in BuildSettings.cs
    // SS_PmPendingId — not used (Package Manager uses static fields only)

    // v4.2 Phase B — PlayMode loop keys defined in PlayMode2.cs
    // SS_Pm*  — PlayMode enter/exit/run
    // SS_PendingTestId — TestRunner.cs

    static UCAF_Listener()
    {
        PendingPath = Path.Combine(WorkspacePath, "commands", "pending");
        DonePath    = Path.Combine(WorkspacePath, "commands", "done");
        ErrorsPath  = Path.Combine(WorkspacePath, "commands", "errors");
        LogFilePath = Path.Combine(WorkspacePath, "logs", "buffer.ndjson");
        BugsNdjson  = Path.Combine(MemoryDir, "bugs.ndjson");
        BugsIndex   = Path.Combine(MemoryDir, "bugs_index.json");

        Directory.CreateDirectory(PendingPath);
        Directory.CreateDirectory(DonePath);
        Directory.CreateDirectory(ErrorsPath);
        Directory.CreateDirectory(Path.GetDirectoryName(LogFilePath));
        Directory.CreateDirectory(MemoryDir);
        Directory.CreateDirectory(MemoryArchive);

        LoadLogBufferFromFile();

        Application.logMessageReceived -= OnLogReceived;
        Application.logMessageReceived += OnLogReceived;

        EditorApplication.update -= Poll;
        EditorApplication.update += Poll;
        EditorApplication.update -= CheckPendingCompile;
        EditorApplication.update += CheckPendingCompile;
        EditorApplication.update -= CheckPendingScreenshot;
        EditorApplication.update += CheckPendingScreenshot;

        // v4.2 Phase A async checkers
        EditorApplication.update -= CheckPendingPackageManager;
        EditorApplication.update += CheckPendingPackageManager;

        // v4.2 Phase B async checkers
        EditorApplication.update -= CheckPendingRuntimeProxy;
        EditorApplication.update += CheckPendingRuntimeProxy;
        EditorApplication.update -= CheckPendingPlayModeEnter;
        EditorApplication.update += CheckPendingPlayModeEnter;
        EditorApplication.update -= CheckPendingPlayModeExit;
        EditorApplication.update += CheckPendingPlayModeExit;
        EditorApplication.update -= CheckPendingPlayModeRun;
        EditorApplication.update += CheckPendingPlayModeRun;
        EditorApplication.update -= CheckPendingTestRun;
        EditorApplication.update += CheckPendingTestRun;
        EditorApplication.update -= CheckPendingTestList;
        EditorApplication.update += CheckPendingTestList;

        // v4.2 Phase D async checkers
        EditorApplication.update -= CheckPendingProfilerSnapshot;
        EditorApplication.update += CheckPendingProfilerSnapshot;
        EditorApplication.update -= CheckPendingProfilerRecord;
        EditorApplication.update += CheckPendingProfilerRecord;

        // Resolve async operations that survived domain reload
        CheckPendingBuildTargetOnStartup();
        CheckPendingPackageManagerOnStartup();
        CheckPendingLightmapOnStartup();

        Debug.Log("[UCAF] Listener v4.2-EF started — Siege of the Blue World.");
    }

    static void OnLogReceived(string condition, string stackTrace, LogType type)
    {
        var entry = new UCAFLogEntry {
            timestamp   = DateTime.UtcNow.ToString("o"),
            type        = type.ToString(),
            message     = condition ?? "",
            stack_trace = stackTrace ?? ""
        };

        lock (_logBuffer)
        {
            _logBuffer.Add(entry);
            while (_logBuffer.Count > LogBufferMax) _logBuffer.RemoveAt(0);
        }

        try
        {
            string line = JsonUtility.ToJson(entry);
            File.AppendAllText(LogFilePath, line + "\n");
        }
        catch { /* don't let logging break the listener */ }
    }

    static void LoadLogBufferFromFile()
    {
        if (!File.Exists(LogFilePath)) return;
        try
        {
            var lines = File.ReadAllLines(LogFilePath);
            int start = Math.Max(0, lines.Length - LogBufferMax);
            lock (_logBuffer)
            {
                _logBuffer.Clear();
                for (int i = start; i < lines.Length; i++)
                {
                    if (string.IsNullOrEmpty(lines[i])) continue;
                    try { _logBuffer.Add(JsonUtility.FromJson<UCAFLogEntry>(lines[i])); } catch { }
                }
            }
        }
        catch { }
    }

    static void Poll()
    {
        if (EditorApplication.timeSinceStartup - _lastPollTime < PollInterval) return;
        _lastPollTime = EditorApplication.timeSinceStartup;

        string[] files;
        try { files = Directory.GetFiles(PendingPath, "*.json"); }
        catch { return; }

        foreach (var file in files)
        {
            UCAFCommand cmd = null;
            try
            {
                var json = File.ReadAllText(file);
                cmd = JsonUtility.FromJson<UCAFCommand>(json);
                File.Delete(file);

                var result = ExecuteCommand(cmd);
                if (result == null) continue; // deferred — command writes its own result

                // v4.1 cross-cutting screenshot_after — only for successful writes
                if (result.success && cmd.HasParam("screenshot_after") &&
                    !string.IsNullOrEmpty(cmd.GetParam("screenshot_after", "")) &&
                    cmd.GetParam("screenshot_after", "false") != "false" &&
                    string.IsNullOrEmpty(result.screenshot_path))
                {
                    TryAttachScreenshot(cmd, result);
                }

                WriteResult(DonePath, cmd.id, result);
            }
            catch (Exception ex)
            {
                string id = cmd != null ? cmd.id : Path.GetFileNameWithoutExtension(file);
                WriteError(ErrorsPath, id, ex.ToString());
                try { File.Delete(file); } catch { }
            }
        }
    }

    // Best-effort sync screenshot for screenshot_after=true|game|scene parameter.
    // We use scene-view sync capture (no async waiting) to keep result inline.
    static void TryAttachScreenshot(UCAFCommand cmd, UCAFResult result)
    {
        try
        {
            string mode = cmd.GetParam("screenshot_after", "true").ToLowerInvariant();
            if (mode == "true") mode = "scene"; // sync path is reliable

            string screenshotsDir = Path.Combine(WorkspacePath, "screenshots");
            Directory.CreateDirectory(screenshotsDir);
            string outPath = Path.Combine(screenshotsDir, "after_" + cmd.id + ".png");

            // Always go via the synchronous Scene View path here; the async game
            // path would require deferring the result write, breaking the contract.
            var fakeCmd = new UCAFCommand { id = cmd.id, type = "take_screenshot" };
            fakeCmd.params_list.Add(new UCAFParam { key = "view", value = "scene" });

            // route directly through TryCaptureSceneView — keep it simple
            if (TryCaptureSceneViewPublic(fakeCmd, outPath, out string err))
                result.screenshot_path = outPath;
            else
                result.message += $" (screenshot_after failed: {err})";
        }
        catch (Exception ex)
        {
            result.message += $" (screenshot_after failed: {ex.Message})";
        }
    }

    internal static UCAFResult ExecuteCommand(UCAFCommand cmd)
    {
        // FR-177: pre-dispatch schema validation — skipped when no schema file exists
        var (schemaValid, schemaIssues, schemaCtx) = ValidateAgainstSchema(cmd);
        if (!schemaValid)
        {
            return new UCAFResult {
                success   = false,
                message   = $"Schema validation failed for '{cmd.type}': {string.Join("; ", schemaIssues)}",
                data_json = JsonUtility.ToJson(schemaCtx)
            };
        }

        switch (cmd.type)
        {
            // scene
            case "create_scene":       return CmdCreateScene(cmd);
            case "open_scene":         return CmdOpenScene(cmd);
            case "save_scene":         return CmdSaveScene(cmd);
            case "list_scene":         return CmdListScene(cmd);

            // object
            case "create_object":      return CmdCreateObject(cmd);
            case "modify_object":      return CmdModifyObject(cmd);
            case "delete_object":      return CmdDeleteObject(cmd);
            case "reparent_object":    return CmdReparentObject(cmd);
            case "duplicate_object":   return CmdDuplicateObject(cmd);

            // object query (v3.0)
            case "find_objects":       return CmdFindObjects(cmd);
            case "get_object_info":    return CmdGetObjectInfo(cmd);

            // inspector bridge
            case "add_component":      return CmdAddComponent(cmd);
            case "remove_component":   return CmdRemoveComponent(cmd);
            case "list_components":    return CmdListComponents(cmd);
            case "list_fields":        return CmdListFields(cmd);
            case "get_field":          return CmdGetField(cmd);
            case "set_field":          return CmdSetField(cmd);
            case "append_array_element": return CmdAppendArrayElement(cmd);

            // scriptable objects
            case "create_scriptable":  return CmdCreateScriptable(cmd);
            case "list_scriptables":   return CmdListScriptables(cmd);
            case "list_assets":        return CmdListAssets(cmd);

            // materials
            case "create_material":    return CmdCreateMaterial(cmd);
            case "assign_material":    return CmdAssignMaterial(cmd);
            case "set_material_prop":  return CmdSetMaterialProp(cmd);

            // scripting
            case "create_script":      return CmdCreateScript(cmd);
            case "attach_script":      return CmdAttachScript(cmd);
            case "compile_check":      return CmdCompileCheck(cmd);
            case "compile_and_wait":   return CmdCompileAndWait(cmd); // async — returns null
            case "asset_refresh":      return CmdAssetRefresh(cmd);
            case "read_file":          return CmdReadFile(cmd);
            case "delete_file":        return CmdDeleteFile(cmd);

            // console (+ streaming FR-179–182)
            case "get_console":           return CmdGetConsole(cmd);
            case "clear_console":         return CmdClearConsole(cmd);
            case "console_subscribe":     return CmdConsoleSubscribe(cmd);
            case "console_unsubscribe":   return CmdConsoleUnsubscribe(cmd);

            // screenshot & view (v3.0)
            case "take_screenshot":    return CmdTakeScreenshot(cmd); // async — returns null
            case "select_object":      return CmdSelectObject(cmd);
            case "focus_scene_view":   return CmdFocusSceneView(cmd);

            // batch (v3.0)
            case "batch":              return CmdBatch(cmd);

            // prefab
            case "create_prefab":      return CmdCreatePrefab(cmd);
            case "apply_prefab":       return CmdApplyPrefab(cmd);
            case "revert_prefab":      return CmdRevertPrefab(cmd);

            // misc
            case "play_mode":          return CmdPlayMode(cmd);
            case "import_asset":       return CmdImportAsset(cmd);
            case "set_lighting":       return CmdSetLighting(cmd);
            case "setup_alien":        return CmdSetupAlien(cmd);
            case "execute_menu_item":  return CmdExecuteMenuItem(cmd);

            // v4.1 — edit_file
            case "edit_file":          return CmdEditFile(cmd);

            // v4.1 — code grep
            case "find_assets_by_content": return CmdFindAssetsByContent(cmd);

            // v4.1 — bug ledger
            case "log_bug":            return CmdLogBug(cmd);
            case "update_bug":         return CmdUpdateBug(cmd);
            case "query_bugs":         return CmdQueryBugs(cmd);
            case "find_similar_bugs":  return CmdFindSimilarBugs(cmd);
            case "get_bug":            return CmdGetBug(cmd);
            case "close_bug":          return CmdCloseBug(cmd);
            case "purge_bug":          return CmdPurgeBug(cmd);

            // v4.1 — protocol
            case "protocol_capabilities": return CmdProtocolCapabilities(cmd);

            // ── v4.2 Phase A — Asset Import Settings ──────────────────────
            case "set_texture_import":       return CmdSetTextureImport(cmd);
            case "set_model_import":         return CmdSetModelImport(cmd);
            case "set_animation_clip_import":return CmdSetAnimationClipImport(cmd);
            case "set_audio_import":         return CmdSetAudioImport(cmd);
            case "validate_imports":         return CmdValidateImports(cmd);

            // v4.2 Phase A — Package Manager (async)
            case "list_packages":            return CmdListPackages(cmd);
            case "search_packages":          return CmdSearchPackages(cmd);
            case "add_package":              return CmdAddPackage(cmd);
            case "remove_package":           return CmdRemovePackage(cmd);
            case "update_package":           return CmdUpdatePackage(cmd);

            // v4.2 Phase A — Project Settings
            case "add_tag":                  return CmdAddTag(cmd);
            case "add_layer":                return CmdAddLayer(cmd);
            case "set_physics_collision":    return CmdSetPhysicsCollision(cmd);
            case "set_physics2d_collision":  return CmdSetPhysics2DCollision(cmd);
            case "set_quality_setting":      return CmdSetQualitySetting(cmd);
            case "set_graphics_setting":     return CmdSetGraphicsSetting(cmd);
            case "set_input_axis":           return CmdSetInputAxis(cmd);
            case "add_input_action":         return CmdAddInputAction(cmd);

            // v4.2 Phase A — Build Settings + Git
            case "set_build_scenes":         return CmdSetBuildScenes(cmd);
            case "set_build_target":         return CmdSetBuildTarget(cmd);
            case "git_status":               return CmdGitStatus(cmd);
            case "git_diff":                 return CmdGitDiff(cmd);
            case "git_commit":               return CmdGitCommit(cmd);
            case "git_branch":               return CmdGitBranch(cmd);

            // ── v4.2 Phase B — PlayMode loop ──────────────────────────────
            case "playmode_enter":           return CmdPlayModeEnter(cmd);
            case "playmode_exit":            return CmdPlayModeExit(cmd);
            case "playmode_run":             return CmdPlayModeRun(cmd);
            case "playmode_runtime_get":     return CmdPlayModeRuntimeGet(cmd);
            case "playmode_runtime_set":     return CmdPlayModeRuntimeSet(cmd);
            case "playmode_runtime_call":    return CmdPlayModeRuntimeCall(cmd);
            case "playmode_signal_subscribe":return CmdPlayModeSignalSubscribe(cmd);

            // v4.2 Phase B — Test Runner
            case "run_tests":                return CmdRunTests(cmd);
            case "list_tests":               return CmdListTests(cmd);
            case "create_test":              return CmdCreateTest(cmd);
            case "register_test_assembly":   return CmdRegisterTestAssembly(cmd);

            // v4.2 Phase B — Input simulation
            case "input_press_key":          return CmdInputPressKey(cmd);
            case "input_move_mouse":         return CmdInputMoveMouse(cmd);
            case "input_gamepad_stick":      return CmdInputGamepadStick(cmd);
            case "input_sequence":           return CmdInputSequence(cmd);

            // ── v4.2 Phase C — Animator Controller ────────────────────────
            case "create_animator_controller": return CmdCreateAnimatorController(cmd);
            case "add_animator_state":         return CmdAddAnimatorState(cmd);
            case "add_animator_transition":    return CmdAddAnimatorTransition(cmd);
            case "add_animator_parameter":     return CmdAddAnimatorParameter(cmd);
            case "add_blend_tree":             return CmdAddBlendTree(cmd);
            case "set_animator_layer":         return CmdSetAnimatorLayer(cmd);
            case "list_animator_states":       return CmdListAnimatorStates(cmd);
            case "validate_animator":          return CmdValidateAnimator(cmd);

            // v4.2 Phase C — Build Player
            case "build_player":               return CmdBuildPlayer(cmd);
            case "run_player":                 return CmdRunPlayer(cmd);

            // v4.2 Phase C — NavMesh
            case "navmesh_bake":               return CmdNavMeshBake(cmd);
            case "navmesh_query_path":         return CmdNavMeshQueryPath(cmd);
            case "navmesh_sample_position":    return CmdNavMeshSamplePosition(cmd);
            case "add_offmesh_link":           return CmdAddOffMeshLink(cmd);

            // v4.2 Phase C — Lightmap
            case "lightmap_bake":              return CmdLightmapBake(cmd);
            case "lightmap_clear":             return CmdLightmapClear(cmd);

            // ── v4.2 Phase D — Profiler ────────────────────────────────────
            case "profiler_snapshot":          return CmdProfilerSnapshot(cmd);
            case "profiler_compare":           return CmdProfilerCompare(cmd);
            case "profiler_record":            return CmdProfilerRecord(cmd);

            // v4.2 Phase D — Timeline + Cinemachine stubs
            case "create_timeline":            return CmdCreateTimeline(cmd);
            case "add_timeline_track":         return CmdAddTimelineTrack(cmd);
            case "add_timeline_clip":          return CmdAddTimelineClip(cmd);
            case "create_cinemachine_camera":  return CmdCreateCinemachineCamera(cmd);
            case "set_vcam_property":          return CmdSetVCamProperty(cmd);
            case "cinemachine_dolly_path":     return CmdCinemachineDollyPath(cmd);

            // v4.2 Phase D — ShaderGraph + VFX
            case "get_shadergraph_properties": return CmdGetShaderGraphProperties(cmd);
            case "set_shadergraph_property":   return CmdSetShaderGraphProperty(cmd);
            case "get_shadergraph_info":       return CmdGetShaderGraphInfo(cmd);
            case "set_vfx_property":           return CmdSetVFXProperty(cmd);
            case "get_vfx_properties":         return CmdGetVFXProperties(cmd);

            // v4.2 Phase D — Terrain
            case "create_terrain":             return CmdCreateTerrain(cmd);
            case "set_terrain_heightmap":      return CmdSetTerrainHeightmap(cmd);
            case "paint_terrain_layer":        return CmdPaintTerrainLayer(cmd);
            case "add_tree_prototype":         return CmdAddTreePrototype(cmd);
            case "paint_trees":                return CmdPaintTrees(cmd);

            // ── v4.2 Phase E — JSON Schema ─────────────────────────────────────
            case "get_command_schema":    return CmdGetCommandSchema(cmd);
            case "validate_command":      return CmdValidateCommand(cmd);

            // ── v4.2 Phase E — Editor Preferences / Layout ─────────────────
            case "apply_editor_layout":   return CmdApplyEditorLayout(cmd);
            case "set_editor_pref":       return CmdSetEditorPref(cmd);
            case "get_editor_pref":       return CmdGetEditorPref(cmd);

            // ── v4.2 Phase F — Recording ───────────────────────────────────
            case "recording_start":          return CmdRecordingStart(cmd);
            case "recording_stop":           return CmdRecordingStop(cmd);
            case "recording_extract_frames": return CmdRecordingExtractFrames(cmd);
            case "recording_list":           return CmdRecordingList(cmd);
            case "recording_delete":         return CmdRecordingDelete(cmd);

            case "ping":               return new UCAFResult { success = true, message = "pong" };
            default:
                return new UCAFResult { success = false, message = $"Unknown command type: {cmd.type}" };
        }
    }

    // ── v4.1: protocol_capabilities (lightweight, no JSON schema files yet) ──

    static UCAFResult CmdProtocolCapabilities(UCAFCommand cmd)
    {
        return new UCAFResult {
            success = true,
            message = "UCAF protocol 4.2",
            data_json = UnityEngine.JsonUtility.ToJson(new UCAFCapabilitiesPayload {
                protocol_version = "4.2-EF",
                listener_label = "Siege of the Blue World",
            })
        };
    }

    internal static void WriteResult(string folder, string id, UCAFResult result)
    {
        File.WriteAllText(Path.Combine(folder, $"{id}.json"), JsonUtility.ToJson(result));
    }

    internal static void WriteError(string folder, string id, string message)
    {
        var err = new UCAFResult { success = false, message = message };
        File.WriteAllText(Path.Combine(folder, $"{id}.json"), JsonUtility.ToJson(err));
    }

    internal static List<UCAFLogEntry> SnapshotLogBuffer()
    {
        lock (_logBuffer) { return new List<UCAFLogEntry>(_logBuffer); }
    }

    internal static void ClearLogBuffer()
    {
        lock (_logBuffer) { _logBuffer.Clear(); }
    }
}
