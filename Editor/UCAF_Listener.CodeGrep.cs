using UnityEngine;
using UnityEditor;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

public static partial class UCAF_Listener
{
    // ── find_assets_by_content (v4.1, FR-85 to FR-88) ───────────────────
    //
    // Direct file-system grep. Faster than AssetDatabase for text searches.

    static UCAFResult CmdFindAssetsByContent(UCAFCommand cmd)
    {
        string pattern = cmd.GetParam("pattern", "");
        if (string.IsNullOrEmpty(pattern))
            return new UCAFResult { success = false, message = "pattern required" };

        string globsStr = cmd.GetParam("glob", "*.cs");
        string folder   = cmd.GetParam("folder", "Assets");
        int maxResults  = int.TryParse(cmd.GetParam("max_results", "200"), out int mr) ? Mathf.Max(1, mr) : 200;
        int contextLines = int.TryParse(cmd.GetParam("context", "0"), out int ctx) ? Mathf.Clamp(ctx, 0, 5) : 0;
        bool ignoreCase = cmd.GetParam("ignore_case", "false") == "true";

        if (!TryResolveSandboxPath(folder, out string absFolder, out string err))
            return new UCAFResult { success = false, message = err };
        if (!Directory.Exists(absFolder))
            return new UCAFResult { success = false, message = $"Folder not found: {folder}" };

        Regex re;
        try
        {
            var opts = RegexOptions.Compiled;
            if (ignoreCase) opts |= RegexOptions.IgnoreCase;
            re = new Regex(pattern, opts);
        }
        catch (Exception ex)
        { return new UCAFResult { success = false, message = $"Bad regex: {ex.Message}" }; }

        var globs = globsStr.Split(',').Select(g => g.Trim()).Where(g => !string.IsNullOrEmpty(g)).ToArray();
        if (globs.Length == 0) globs = new[] { "*.cs" };

        var hits = new UCAFCodeHits();
        string projectRoot = Path.GetDirectoryName(Application.dataPath);

        try
        {
            foreach (var g in globs)
            {
                foreach (var path in Directory.EnumerateFiles(absFolder, g, SearchOption.AllDirectories))
                {
                    // Skip Library/, Temp/, obj/ — never search build artifacts
                    string rel = MakeRelative(projectRoot, path);
                    if (ShouldSkip(rel)) continue;

                    string[] lines;
                    try { lines = File.ReadAllLines(path); }
                    catch { continue; }

                    for (int i = 0; i < lines.Length; i++)
                    {
                        var m = re.Match(lines[i]);
                        if (!m.Success) continue;

                        var hit = new UCAFCodeHit {
                            path = rel.Replace('\\', '/'),
                            line = i + 1,
                            match = lines[i].Length > 400 ? lines[i].Substring(0, 400) + "…" : lines[i],
                        };
                        if (contextLines > 0)
                        {
                            int from = Mathf.Max(0, i - contextLines);
                            int to   = Mathf.Min(lines.Length - 1, i + contextLines);
                            hit.context_before = string.Join("\n", lines.Skip(from).Take(i - from));
                            hit.context_after  = string.Join("\n", lines.Skip(i + 1).Take(to - i));
                        }

                        hits.hits.Add(hit);
                        hits.total++;
                        if (hits.hits.Count >= maxResults)
                        {
                            hits.truncated = true;
                            goto done;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        { return new UCAFResult { success = false, message = $"Grep failed: {ex.Message}" }; }

        done:
        return new UCAFResult {
            success = true,
            message = $"{hits.total} hit(s){(hits.truncated ? " (truncated)" : "")}",
            data_json = JsonUtility.ToJson(hits)
        };
    }

    static string MakeRelative(string root, string abs)
    {
        if (abs.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            string rel = abs.Substring(root.Length).TrimStart('\\', '/');
            return rel;
        }
        return abs;
    }

    static bool ShouldSkip(string rel)
    {
        var n = rel.Replace('\\', '/');
        return n.StartsWith("Library/", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("Temp/", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("obj/", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("Build/", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("Logs/", StringComparison.OrdinalIgnoreCase);
    }
}
