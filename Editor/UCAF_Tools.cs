using UnityEngine;
using UnityEditor;
using System;
using System.IO;
using System.Linq;

public static class UCAF_Tools
{
    private static readonly string WorkspacePath = Path.Combine(
        Path.GetDirectoryName(Application.dataPath), "ucaf_workspace");

    public static Type FindTypeByName(string className)
    {
        return AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
            .FirstOrDefault(t => t.Name == className);
    }

    [MenuItem("UCAF/Take Screenshot")]
    public static string TakeScreenshot()
    {
        string screenshotsDir = Path.Combine(WorkspacePath, "screenshots");
        string archiveDir     = Path.Combine(screenshotsDir, "archive");
        Directory.CreateDirectory(archiveDir);

        string timestamp   = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        string latestPath  = Path.Combine(screenshotsDir, "latest.png");
        string archivePath = Path.Combine(archiveDir, $"{timestamp}.png");

        ScreenCapture.CaptureScreenshot(latestPath);
        if (File.Exists(latestPath))
            File.Copy(latestPath, archivePath, overwrite: true);

        Debug.Log($"[UCAF] Screenshot saved: {latestPath}");
        return latestPath;
    }

    [MenuItem("UCAF/Show Workspace Status")]
    public static void ShowStatus()
    {
        string pendingPath = Path.Combine(WorkspacePath, "commands", "pending");
        string donePath    = Path.Combine(WorkspacePath, "commands", "done");
        string errorsPath  = Path.Combine(WorkspacePath, "commands", "errors");

        int pending = Directory.Exists(pendingPath) ? Directory.GetFiles(pendingPath, "*.json").Length : 0;
        int done    = Directory.Exists(donePath)    ? Directory.GetFiles(donePath,    "*.json").Length : 0;
        int errors  = Directory.Exists(errorsPath)  ? Directory.GetFiles(errorsPath,  "*.json").Length : 0;

        Debug.Log($"[UCAF] Status — Pending: {pending} | Done: {done} | Errors: {errors}");
        EditorUtility.DisplayDialog("UCAF Status",
            $"Workspace: {WorkspacePath}\n\nPending commands: {pending}\nCompleted: {done}\nErrors: {errors}",
            "OK");
    }

    [MenuItem("UCAF/Clear Done Commands")]
    public static void ClearDone()
    {
        string donePath = Path.Combine(WorkspacePath, "commands", "done");
        if (!Directory.Exists(donePath)) return;
        foreach (var f in Directory.GetFiles(donePath, "*.json"))
            File.Delete(f);
        Debug.Log("[UCAF] Done commands cleared.");
    }

    [MenuItem("UCAF/Open Workspace Folder")]
    public static void OpenWorkspace()
    {
        EditorUtility.RevealInFinder(WorkspacePath);
    }
}
