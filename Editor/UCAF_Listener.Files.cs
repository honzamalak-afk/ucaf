using UnityEngine;
using UnityEditor;
using System;
using System.IO;

public static partial class UCAF_Listener
{
    // ── read_file / delete_file ─────────────────────────────────────────
    //
    // Sandbox: only Assets/, ucaf_workspace/, ProjectSettings/.
    // asset_path is project-relative (e.g. "Assets/Scripts/Player.cs").

    static UCAFResult CmdReadFile(UCAFCommand cmd)
    {
        string assetPath = cmd.GetParam("asset_path", "");
        if (string.IsNullOrEmpty(assetPath))
            return new UCAFResult { success = false, message = "asset_path required" };

        if (!TryResolveSandboxPath(assetPath, out string absPath, out string err))
            return new UCAFResult { success = false, message = err };

        if (!File.Exists(absPath))
            return new UCAFResult { success = false, message = $"File not found: {assetPath}" };

        string content;
        int lineCount;
        try
        {
            content = File.ReadAllText(absPath);
            lineCount = 1;
            for (int i = 0; i < content.Length; i++) if (content[i] == '\n') lineCount++;
            if (content.Length == 0) lineCount = 0;
        }
        catch (Exception ex)
        {
            return new UCAFResult { success = false, message = $"Read failed: {ex.Message}" };
        }

        var payload = new UCAFFileContent {
            asset_path = assetPath,
            content    = content,
            line_count = lineCount
        };
        return new UCAFResult {
            success = true,
            message = $"Read {assetPath} ({lineCount} lines, {content.Length} chars)",
            data_json = JsonUtility.ToJson(payload)
        };
    }

    static UCAFResult CmdDeleteFile(UCAFCommand cmd)
    {
        string assetPath = cmd.GetParam("asset_path", "");
        if (string.IsNullOrEmpty(assetPath))
            return new UCAFResult { success = false, message = "asset_path required" };

        if (!TryResolveSandboxPath(assetPath, out string absPath, out string err))
            return new UCAFResult { success = false, message = err };

        if (!File.Exists(absPath))
            return new UCAFResult { success = false, message = $"File not found: {assetPath}" };

        bool inAssets = assetPath.Replace('\\', '/').StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)
                        || assetPath.Equals("Assets", StringComparison.OrdinalIgnoreCase);

        try
        {
            if (inAssets)
            {
                if (!AssetDatabase.DeleteAsset(assetPath))
                {
                    File.Delete(absPath);
                    string meta = absPath + ".meta";
                    if (File.Exists(meta)) File.Delete(meta);
                }
                AssetDatabase.Refresh();
            }
            else
            {
                File.Delete(absPath);
            }
        }
        catch (Exception ex)
        {
            return new UCAFResult { success = false, message = $"Delete failed: {ex.Message}" };
        }

        return new UCAFResult { success = true, message = $"Deleted: {assetPath}" };
    }

    // Returns absolute path if assetPath lies inside one of the allowed roots.
    internal static bool TryResolveSandboxPath(string assetPath, out string absPath, out string error)
    {
        absPath = null;
        error = null;
        if (string.IsNullOrEmpty(assetPath))
        { error = "Empty path"; return false; }

        string projectRoot = Path.GetDirectoryName(Application.dataPath);
        string normalized = assetPath.Replace('\\', '/').TrimStart('/');

        bool allowed =
            normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("Assets", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("ProjectSettings/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("ucaf_workspace/", StringComparison.OrdinalIgnoreCase);

        if (!allowed)
        {
            error = $"Path outside sandbox (allowed: Assets/, ProjectSettings/, ucaf_workspace/): {assetPath}";
            return false;
        }

        string candidate = Path.GetFullPath(Path.Combine(projectRoot, normalized));
        string rootFull = Path.GetFullPath(projectRoot);
        if (!candidate.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            error = $"Path escapes project root: {assetPath}";
            return false;
        }

        absPath = candidate;
        return true;
    }
}
