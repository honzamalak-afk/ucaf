using UnityEngine;
using UnityEditor;
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

public static partial class UCAF_Listener
{
    // ── edit_file (v4.1, FR-71 to FR-75) ────────────────────────────────
    //
    // Modes:
    //   replace — old_string + new_string (+ all=true|false)
    //   anchor  — anchor_regex + insert (+ position=before|after)
    //   patch   — not implemented in MVP; returns clear error
    //
    // Optional: compile=true → schedules compile_and_wait; result returns
    // sync field counts plus deferred compile state. We follow the existing
    // compile_and_wait deferred pattern so the caller gets one final result.

    static UCAFResult CmdEditFile(UCAFCommand cmd)
    {
        string assetPath = cmd.GetParam("path", cmd.GetParam("asset_path", ""));
        string mode      = cmd.GetParam("mode", "replace").ToLowerInvariant();
        bool dryRun      = cmd.GetParam("dry_run", "false") == "true";
        bool compile     = cmd.GetParam("compile", "false") == "true";

        if (string.IsNullOrEmpty(assetPath))
            return new UCAFResult { success = false, message = "path (asset_path) required" };

        if (!TryResolveSandboxPath(assetPath, out string absPath, out string err))
            return new UCAFResult { success = false, message = err };
        if (!File.Exists(absPath))
            return new UCAFResult { success = false, message = $"File not found: {assetPath}" };

        string original;
        try { original = File.ReadAllText(absPath); }
        catch (Exception ex)
        { return new UCAFResult { success = false, message = $"Read failed: {ex.Message}" }; }

        string updated;
        int replacements;

        try
        {
            switch (mode)
            {
                case "replace":
                    updated = ApplyReplace(cmd, original, out replacements, out string rerr);
                    if (rerr != null) return new UCAFResult { success = false, message = rerr };
                    break;
                case "anchor":
                    updated = ApplyAnchor(cmd, original, out replacements, out string aerr);
                    if (aerr != null) return new UCAFResult { success = false, message = aerr };
                    break;
                case "patch":
                    return new UCAFResult {
                        success = false,
                        message = "edit_file mode=patch not implemented in v4.1 MVP — use mode=replace or mode=anchor"
                    };
                default:
                    return new UCAFResult {
                        success = false,
                        message = $"Unknown mode: {mode} (use replace|anchor)"
                    };
            }
        }
        catch (Exception ex)
        {
            return new UCAFResult { success = false, message = $"edit_file failed: {ex.Message}" };
        }

        var payload = new UCAFEditFileResult {
            asset_path        = assetPath,
            mode              = mode,
            replacements      = replacements,
            line_count_before = CountLines(original),
            line_count_after  = CountLines(updated),
        };

        if (dryRun)
        {
            return new UCAFResult {
                success = true,
                message = $"[dry_run] would change {assetPath}: {replacements} replacement(s), " +
                          $"lines {payload.line_count_before} → {payload.line_count_after}",
                data_json = JsonUtility.ToJson(payload)
            };
        }

        try
        {
            File.WriteAllText(absPath, updated);
            bool inAssets = assetPath.Replace('\\', '/').StartsWith("Assets/", StringComparison.OrdinalIgnoreCase);
            if (inAssets) AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
        }
        catch (Exception ex)
        {
            return new UCAFResult { success = false, message = $"Write failed: {ex.Message}" };
        }

        if (compile)
        {
            // chain into the existing compile_and_wait deferred path; we synthesize
            // a fake command so the standard machinery owns the result file.
            var fake = new UCAFCommand { id = cmd.id, type = "compile_and_wait" };
            fake.params_list.Add(new UCAFParam { key = "edit_file_payload", value = JsonUtility.ToJson(payload) });
            return CmdCompileAndWaitForEdit(fake, payload);
        }

        return new UCAFResult {
            success = true,
            message = $"Edited {assetPath}: {replacements} replacement(s)",
            data_json = JsonUtility.ToJson(payload)
        };
    }

    static string ApplyReplace(UCAFCommand cmd, string text, out int count, out string error)
    {
        error = null; count = 0;
        string oldStr = cmd.GetParam("old_string", "");
        string newStr = cmd.GetParam("new_string", "");
        bool all = cmd.GetParam("all", "false") == "true";

        if (string.IsNullOrEmpty(oldStr))
        { error = "old_string required for mode=replace"; return text; }

        if (all)
        {
            int idx = 0;
            var sb = new System.Text.StringBuilder();
            while (true)
            {
                int found = text.IndexOf(oldStr, idx, StringComparison.Ordinal);
                if (found < 0) { sb.Append(text, idx, text.Length - idx); break; }
                sb.Append(text, idx, found - idx);
                sb.Append(newStr);
                idx = found + oldStr.Length;
                count++;
            }
            if (count == 0) { error = $"old_string not found in file"; return text; }
            return sb.ToString();
        }
        else
        {
            int first = text.IndexOf(oldStr, StringComparison.Ordinal);
            if (first < 0) { error = "old_string not found"; return text; }
            int second = text.IndexOf(oldStr, first + oldStr.Length, StringComparison.Ordinal);
            if (second >= 0)
            { error = "old_string is ambiguous (occurs >1×); use all=true or include more context"; return text; }
            count = 1;
            return text.Substring(0, first) + newStr + text.Substring(first + oldStr.Length);
        }
    }

    static string ApplyAnchor(UCAFCommand cmd, string text, out int count, out string error)
    {
        error = null; count = 0;
        string anchor = cmd.GetParam("anchor_regex", "");
        string insert = cmd.GetParam("insert", "");
        string position = cmd.GetParam("position", "after").ToLowerInvariant();

        if (string.IsNullOrEmpty(anchor))
        { error = "anchor_regex required for mode=anchor"; return text; }
        if (string.IsNullOrEmpty(insert))
        { error = "insert required for mode=anchor"; return text; }

        Regex re;
        try { re = new Regex(anchor, RegexOptions.Multiline); }
        catch (Exception ex) { error = $"Bad regex: {ex.Message}"; return text; }

        var match = re.Match(text);
        if (!match.Success) { error = $"anchor_regex matched nothing"; return text; }
        count = 1;

        if (position == "before")
            return text.Substring(0, match.Index) + insert + text.Substring(match.Index);
        // default: after
        int afterIdx = match.Index + match.Length;
        return text.Substring(0, afterIdx) + insert + text.Substring(afterIdx);
    }

    static int CountLines(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        int c = 1;
        for (int i = 0; i < s.Length; i++) if (s[i] == '\n') c++;
        return c;
    }

    // Mirror compile_and_wait dispatch but stash the edit_file payload so the
    // final result merges both edit metadata + compile result.
    static UCAFResult CmdCompileAndWaitForEdit(UCAFCommand cmd, UCAFEditFileResult payload)
    {
        SessionState.SetString("UCAF_PendingEditPayload", JsonUtility.ToJson(payload));
        return CmdCompileAndWait(cmd);
    }
}
