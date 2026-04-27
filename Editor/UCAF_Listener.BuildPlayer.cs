using UnityEngine;
using UnityEditor;
using UnityEditor.Build.Reporting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

public static partial class UCAF_Listener
{
    // ── Build Player & run (v4.2 Phase C, FR-134 to FR-135) ──────────────

    // FR-134: build_player
    // Synchronous — BuildPipeline.BuildPlayer blocks the Editor during the build.
    static UCAFResult CmdBuildPlayer(UCAFCommand cmd)
    {
        if (cmd.GetParam("confirm", "false") != "true")
            return new UCAFResult {
                success = false,
                message = "build_player requires confirm=true (build may take several minutes and will freeze the Editor)."
            };

        string outputPath = cmd.GetParam("output_path", "");
        if (string.IsNullOrEmpty(outputPath))
            return new UCAFResult { success = false, message = "output_path is required (e.g. Builds/Windows/MyGame.exe)." };

        if (!Path.IsPathRooted(outputPath))
            outputPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), outputPath);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath));

        var scenes = new List<string>();
        foreach (var scene in EditorBuildSettings.scenes)
            if (scene.enabled) scenes.Add(scene.path);

        if (scenes.Count == 0)
            return new UCAFResult {
                success = false,
                message = "No enabled scenes in Build Settings. Add scenes first with set_build_scenes."
            };

        var options = new BuildPlayerOptions {
            scenes             = scenes.ToArray(),
            locationPathName   = outputPath,
            target             = EditorUserBuildSettings.activeBuildTarget,
            targetGroup        = BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget),
            options            = BuildOptions.None
        };

        if (cmd.GetParam("development", "false") == "true")
            options.options |= BuildOptions.Development;

        var sw = Stopwatch.StartNew();
        BuildReport report = BuildPipeline.BuildPlayer(options);
        sw.Stop();

        var summary = report.summary;
        bool success = summary.result == BuildResult.Succeeded;

        var errors   = new List<string>();
        var warnings = new List<string>();
        foreach (var step in report.steps)
            foreach (var msg in step.messages)
            {
                if (msg.type == LogType.Error || msg.type == LogType.Exception)
                    errors.Add(msg.content);
                else if (msg.type == LogType.Warning)
                    warnings.Add(msg.content);
            }

        long sizeBytes = GetPathSize(outputPath);

        var payload = new UCAFBuildPlayerResult {
            success        = success,
            output_path    = outputPath,
            duration_s     = (float)sw.Elapsed.TotalSeconds,
            size_bytes     = sizeBytes,
            error_count    = errors.Count,
            warning_count  = warnings.Count,
            summary_result = summary.result.ToString(),
            errors         = errors,
            warnings       = warnings
        };

        return new UCAFResult {
            success   = success,
            message   = success
                ? $"Build succeeded in {sw.Elapsed.TotalSeconds:F1}s → {outputPath} ({sizeBytes / 1024 / 1024} MB)"
                : $"Build failed ({errors.Count} error(s)). Check data_json for details.",
            data_json = JsonUtility.ToJson(payload)
        };
    }

    static long GetPathSize(string path)
    {
        try
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            if (Directory.Exists(path))
            {
                long total = 0;
                foreach (var f in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                    try { total += new FileInfo(f).Length; } catch { }
                return total;
            }
        }
        catch { }
        return 0;
    }

    // FR-135: run_player
    static UCAFResult CmdRunPlayer(UCAFCommand cmd)
    {
        string path = cmd.GetParam("path", "");
        if (string.IsNullOrEmpty(path))
            return new UCAFResult { success = false, message = "path is required (path to the built executable)." };

        if (!Path.IsPathRooted(path))
            path = Path.Combine(Path.GetDirectoryName(Application.dataPath), path);

        if (!File.Exists(path))
            return new UCAFResult { success = false, message = $"Executable not found: {path}" };

        try
        {
            Process.Start(path);
            return new UCAFResult { success = true, message = $"Player launched: {path}" };
        }
        catch (Exception ex)
        {
            return new UCAFResult { success = false, message = $"Failed to launch player: {ex.Message}" };
        }
    }
}
