using UnityEngine;
using UnityEditor;
using System;
using System.Globalization;
using System.IO;

public static partial class UCAF_Listener
{
    // ── select_object / focus_scene_view ────────────────────────────────

    static UCAFResult CmdSelectObject(UCAFCommand cmd)
    {
        var obj = ResolveObject(cmd, out string err);
        if (obj == null) return new UCAFResult { success = false, message = err };
        Selection.activeGameObject = obj;
        EditorGUIUtility.PingObject(obj);
        return new UCAFResult { success = true, message = $"Selected: {GetPath(obj)}" };
    }

    static UCAFResult CmdFocusSceneView(UCAFCommand cmd)
    {
        var view = SceneView.lastActiveSceneView;
        if (view == null)
        {
            var all = SceneView.sceneViews;
            if (all != null && all.Count > 0) view = all[0] as SceneView;
        }
        if (view == null)
            return new UCAFResult { success = false, message = "No SceneView available" };

        if (Selection.activeGameObject == null)
        {
            var obj = ResolveObject(cmd, out string err);
            if (obj == null) return new UCAFResult { success = false, message = $"No selection and {err}" };
            Selection.activeGameObject = obj;
        }

        view.FrameSelected();
        view.Repaint();
        return new UCAFResult { success = true, message = $"Framed selection in SceneView" };
    }

    // ── take_screenshot (async) ─────────────────────────────────────────
    //
    // ScreenCapture.CaptureScreenshot writes the file 1-N frames later.
    // Pattern mirrors compile_and_wait: command returns null (deferred);
    // CheckPendingScreenshot tick polls for file existence and writes the
    // result file only once the PNG is on disk.

    static UCAFResult CmdTakeScreenshot(UCAFCommand cmd)
    {
        string existing = SessionState.GetString(SS_PendingScreenshotId, "");
        if (!string.IsNullOrEmpty(existing))
            return new UCAFResult {
                success = false,
                message = $"Another take_screenshot already in progress (id={existing})"
            };

        string view = cmd.GetParam("view", "game").ToLowerInvariant();
        string screenshotsDir = Path.Combine(WorkspacePath, "screenshots");
        string archiveDir     = Path.Combine(screenshotsDir, "archive");
        Directory.CreateDirectory(archiveDir);

        string latestPath = Path.Combine(screenshotsDir, "latest.png");
        try { if (File.Exists(latestPath)) File.Delete(latestPath); } catch { }

        if (view == "scene")
        {
            // Scene View capture is synchronous — render the SceneView's
            // camera into a RenderTexture and write the PNG directly. No
            // need for the deferred file-watch path.
            if (!TryCaptureSceneView(cmd, latestPath, out string err))
                return new UCAFResult { success = false, message = err };

            try
            {
                string archivePath = Path.Combine(archiveDir,
                    $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png");
                File.Copy(latestPath, archivePath, overwrite: true);
            }
            catch { /* archive copy is best-effort */ }

            return new UCAFResult {
                success = true,
                message = "Scene View screenshot saved",
                screenshot_path = latestPath
            };
        }

        SessionState.SetString(SS_PendingScreenshotId,    cmd.id);
        SessionState.SetString(SS_PendingScreenshotPath,  latestPath);
        SessionState.SetString(SS_PendingScreenshotStart, DateTime.UtcNow.ToString("o"));

        try
        {
            ScreenCapture.CaptureScreenshot(latestPath);
        }
        catch (Exception ex)
        {
            ClearPendingScreenshotState();
            return new UCAFResult { success = false, message = $"CaptureScreenshot failed: {ex.Message}" };
        }

        return null; // deferred — CheckPendingScreenshot will write the result
    }

    internal static bool TryCaptureSceneViewPublic(UCAFCommand cmd, string outPath, out string error)
        => TryCaptureSceneView(cmd, outPath, out error);

    static bool TryCaptureSceneView(UCAFCommand cmd, string outPath, out string error)
    {
        error = null;
        var sv = SceneView.lastActiveSceneView;
        if (sv == null)
        {
            var all = SceneView.sceneViews;
            if (all != null && all.Count > 0) sv = all[0] as SceneView;
        }
        if (sv == null) { error = "No SceneView available"; return false; }

        var cam = sv.camera;
        if (cam == null) { error = "SceneView has no camera"; return false; }

        int width  = int.TryParse(cmd.GetParam("width",  "1280"), out int w) ? w : 1280;
        int height = int.TryParse(cmd.GetParam("height", "720"),  out int h) ? h : 720;
        if (width  < 16) width  = 16;
        if (height < 16) height = 16;

        var rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
        var prev = cam.targetTexture;
        var prevActive = RenderTexture.active;
        Texture2D tex = null;
        try
        {
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();
            byte[] png = tex.EncodeToPNG();
            File.WriteAllBytes(outPath, png);
        }
        catch (Exception ex)
        {
            error = $"Scene View capture failed: {ex.Message}";
            return false;
        }
        finally
        {
            cam.targetTexture = prev;
            RenderTexture.active = prevActive;
            RenderTexture.ReleaseTemporary(rt);
            if (tex != null) UnityEngine.Object.DestroyImmediate(tex);
        }
        return true;
    }

    static void CheckPendingScreenshot()
    {
        string pendingId = SessionState.GetString(SS_PendingScreenshotId, "");
        if (string.IsNullOrEmpty(pendingId)) return;

        string targetPath = SessionState.GetString(SS_PendingScreenshotPath, "");
        string startStr   = SessionState.GetString(SS_PendingScreenshotStart, "");
        DateTime start    = DateTime.MinValue;
        DateTime.TryParse(startStr, null, DateTimeStyles.RoundtripKind, out start);

        double elapsed = start != DateTime.MinValue
            ? (DateTime.UtcNow - start).TotalSeconds : 0;

        bool fileReady = !string.IsNullOrEmpty(targetPath) && File.Exists(targetPath);

        // File exists but may still be mid-write — wait one extra tick after
        // first sighting so the writer flushes.
        if (fileReady)
        {
            try
            {
                using (var fs = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (fs.Length == 0) fileReady = false;
                }
            }
            catch { fileReady = false; }
        }

        if (!fileReady && elapsed < 5.0) return;

        if (!fileReady)
        {
            WriteResult(DonePath, pendingId, new UCAFResult {
                success = false,
                message = $"Screenshot timed out after {elapsed:F1}s; file not on disk: {targetPath}"
            });
            ClearPendingScreenshotState();
            return;
        }

        try
        {
            string archiveDir = Path.Combine(Path.GetDirectoryName(targetPath), "archive");
            Directory.CreateDirectory(archiveDir);
            string archivePath = Path.Combine(archiveDir,
                $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png");
            File.Copy(targetPath, archivePath, overwrite: true);
        }
        catch { /* archive copy is best-effort */ }

        var result = new UCAFResult {
            success = true,
            message = $"Screenshot saved ({elapsed:F1}s)",
            screenshot_path = targetPath
        };
        WriteResult(DonePath, pendingId, result);
        ClearPendingScreenshotState();
    }

    static void ClearPendingScreenshotState()
    {
        SessionState.EraseString(SS_PendingScreenshotId);
        SessionState.EraseString(SS_PendingScreenshotPath);
        SessionState.EraseString(SS_PendingScreenshotStart);
    }
}
