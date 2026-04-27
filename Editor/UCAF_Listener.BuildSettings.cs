using UnityEngine;
using UnityEditor;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

public static partial class UCAF_Listener
{
    // ── Build Settings + Git (v4.2 Phase A, FR-136, FR-137, FR-171 to FR-174) ──

    internal const string SS_PendingBuildTargetId   = "UCAF_PendingBuildTargetId";
    internal const string SS_PendingBuildTargetName = "UCAF_PendingBuildTargetName";

    // Called from static constructor to resolve a pending build-target switch
    // that survived a domain reload (SwitchActiveBuildTarget triggers reload).
    internal static void CheckPendingBuildTargetOnStartup()
    {
        string id = SessionState.GetString(SS_PendingBuildTargetId, "");
        if (string.IsNullOrEmpty(id)) return;
        string targetName = SessionState.GetString(SS_PendingBuildTargetName, "");
        bool success = EditorUserBuildSettings.activeBuildTarget.ToString()
                           .Equals(targetName, StringComparison.OrdinalIgnoreCase);
        WriteResult(DonePath, id, new UCAFResult {
            success = success,
            message = success
                ? $"Build target switched to {EditorUserBuildSettings.activeBuildTarget}"
                : $"Build target switch may have failed. Current: {EditorUserBuildSettings.activeBuildTarget}, expected: {targetName}"
        });
        SessionState.EraseString(SS_PendingBuildTargetId);
        SessionState.EraseString(SS_PendingBuildTargetName);
    }

    // ── set_build_scenes ───────────────────────────────────────────────────

    static UCAFResult CmdSetBuildScenes(UCAFCommand cmd)
    {
        string scenesParam  = cmd.GetParam("scenes", "");
        string enabledParam = cmd.GetParam("enabled", "");

        if (string.IsNullOrEmpty(scenesParam))
            return new UCAFResult { success = false, message = "scenes required (comma-separated asset paths)" };

        string[] scenePaths  = scenesParam.Split(',');
        string[] enabledVals = enabledParam.Split(',');

        var newScenes = new List<EditorBuildSettingsScene>();
        for (int i = 0; i < scenePaths.Length; i++)
        {
            string path    = scenePaths[i].Trim();
            bool   enabled = enabledVals.Length > i
                ? enabledVals[i].Trim() != "false"
                : true;
            newScenes.Add(new EditorBuildSettingsScene(path, enabled));
        }

        EditorBuildSettings.scenes = newScenes.ToArray();
        return new UCAFResult {
            success = true,
            message = $"Build scenes updated: {newScenes.Count} scene(s)"
        };
    }

    // ── set_build_target ──────────────────────────────────────────────────
    // Domain-reload-safe: stores command ID in SessionState before calling
    // SwitchActiveBuildTarget so CheckPendingBuildTargetOnStartup can write
    // the result after reload if the call doesn't return normally.

    static UCAFResult CmdSetBuildTarget(UCAFCommand cmd)
    {
        string targetStr = cmd.GetParam("target", "");
        if (string.IsNullOrEmpty(targetStr))
            return new UCAFResult { success = false, message = "target required (e.g. StandaloneWindows64, Android, iOS, WebGL)" };

        if (!Enum.TryParse<BuildTarget>(targetStr, true, out BuildTarget target))
            return new UCAFResult { success = false, message = $"Unknown BuildTarget '{targetStr}'. Valid: {string.Join(", ", Enum.GetNames(typeof(BuildTarget)))}" };

        BuildTargetGroup group = BuildPipeline.GetBuildTargetGroup(target);

        SessionState.SetString(SS_PendingBuildTargetId,   cmd.id);
        SessionState.SetString(SS_PendingBuildTargetName, target.ToString());

        // Defer the actual switch so we finish writing state before reload fires
        EditorApplication.delayCall += () => {
            bool ok = EditorUserBuildSettings.SwitchActiveBuildTarget(group, target);
            // If we reach here (no domain reload occurred), write result
            if (!string.IsNullOrEmpty(SessionState.GetString(SS_PendingBuildTargetId, "")))
            {
                WriteResult(DonePath, cmd.id, new UCAFResult {
                    success = ok,
                    message = ok ? $"Build target switched to {target}" : $"Failed to switch to {target}"
                });
                SessionState.EraseString(SS_PendingBuildTargetId);
                SessionState.EraseString(SS_PendingBuildTargetName);
            }
        };

        return null; // deferred
    }

    // ── Git operations ─────────────────────────────────────────────────────

    static UCAFResult CmdGitStatus(UCAFCommand cmd)
    {
        string output = RunGit("status --porcelain", out string error);
        if (output == null)
            return new UCAFResult { success = false, message = $"git error: {error}" };

        var result = new UCAFGitStatusResult();
        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.TrimEnd();
            if (line.Length < 3) continue;
            string status = line.Substring(0, 2);
            string file   = line.Substring(3).Trim().Trim('"');

            if (status == "??" || status.StartsWith("?")) result.untracked.Add(file);
            else if (status.Contains("A")) result.added.Add(file);
            else if (status.Contains("D")) result.deleted.Add(file);
            else result.modified.Add(file);
        }
        result.total = result.modified.Count + result.added.Count + result.deleted.Count + result.untracked.Count;

        return new UCAFResult {
            success   = true,
            message   = $"{result.total} changed file(s)",
            data_json = JsonUtility.ToJson(result)
        };
    }

    static UCAFResult CmdGitDiff(UCAFCommand cmd)
    {
        string path = cmd.GetParam("path", "");
        string args = string.IsNullOrEmpty(path) ? "diff" : $"diff -- \"{path}\"";

        string output = RunGit(args, out string error);
        if (output == null)
            return new UCAFResult { success = false, message = $"git error: {error}" };

        int filesChanged = 0;
        foreach (string line in output.Split('\n'))
            if (line.StartsWith("diff --git")) filesChanged++;

        var result = new UCAFGitDiffResult { patch = output, files_changed = filesChanged };
        return new UCAFResult {
            success   = true,
            message   = $"{filesChanged} file(s) changed",
            data_json = JsonUtility.ToJson(result)
        };
    }

    static UCAFResult CmdGitCommit(UCAFCommand cmd)
    {
        bool confirm = cmd.GetParam("confirm", "false") == "true";
        if (!confirm)
            return new UCAFResult { success = false, message = "git_commit requires confirm=true to proceed" };

        string message = cmd.GetParam("message", "");
        if (string.IsNullOrEmpty(message))
            return new UCAFResult { success = false, message = "message required" };

        string pathsParam = cmd.GetParam("paths", "");
        string stageArgs  = string.IsNullOrEmpty(pathsParam) ? "add Assets/ ProjectSettings/" : $"add {pathsParam}";

        string stageOutput = RunGit(stageArgs, out string stageError);
        if (stageOutput == null)
            return new UCAFResult { success = false, message = $"git add failed: {stageError}" };

        // Escape message for CLI — use --file to avoid shell injection
        string tmpFile = Path.Combine(WorkspacePath, "git_commit_msg.txt");
        File.WriteAllText(tmpFile, message);
        string output = RunGit($"commit --file \"{tmpFile}\"", out string error);
        try { File.Delete(tmpFile); } catch { }

        if (output == null)
            return new UCAFResult { success = false, message = $"git commit failed: {error}" };

        return new UCAFResult { success = true, message = output.Trim() };
    }

    static UCAFResult CmdGitBranch(UCAFCommand cmd)
    {
        string action = cmd.GetParam("action", "list").ToLowerInvariant();
        string name   = cmd.GetParam("name", "");

        switch (action)
        {
            case "list":
            {
                string output = RunGit("branch", out string error);
                if (output == null) return new UCAFResult { success = false, message = $"git error: {error}" };
                return new UCAFResult { success = true, message = output.Trim() };
            }
            case "create":
            {
                if (string.IsNullOrEmpty(name))
                    return new UCAFResult { success = false, message = "name required for action=create" };
                string output = RunGit($"branch \"{name}\"", out string error);
                if (output == null) return new UCAFResult { success = false, message = $"git error: {error}" };
                return new UCAFResult { success = true, message = $"Branch '{name}' created" };
            }
            case "switch":
            case "checkout":
            {
                if (string.IsNullOrEmpty(name))
                    return new UCAFResult { success = false, message = "name required for action=switch" };
                string output = RunGit($"checkout \"{name}\"", out string error);
                if (output == null) return new UCAFResult { success = false, message = $"git error: {error}" };
                return new UCAFResult { success = true, message = $"Switched to branch '{name}'" };
            }
            default:
                return new UCAFResult { success = false, message = $"Unknown action '{action}'. Valid: list, create, switch" };
        }
    }

    // ── Git runner ─────────────────────────────────────────────────────────

    static string RunGit(string arguments, out string errorOutput)
    {
        string projectRoot = Path.GetDirectoryName(Application.dataPath);
        try
        {
            var psi = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory       = projectRoot,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true
            };
            using (var proc = Process.Start(psi))
            {
                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit(10000);
                if (proc.ExitCode != 0 && string.IsNullOrEmpty(stdout))
                {
                    errorOutput = stderr;
                    return null;
                }
                errorOutput = stderr;
                return stdout;
            }
        }
        catch (Exception ex)
        {
            errorOutput = ex.Message;
            return null;
        }
    }
}
