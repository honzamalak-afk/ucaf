using UnityEngine;
using UnityEditor;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

public static partial class UCAF_Listener
{
    // ── Bug ledger (v4.1, FR-89 to FR-103) ──────────────────────────────
    //
    // Append-only ndjson at ucaf_workspace/memory/bugs.ndjson + denormalized
    // index at bugs_index.json. Every record write appends to ndjson AND
    // refreshes the index snapshot. Updates are appended as new lines with
    // a "patch" marker; the index always holds the latest state.

    // MemoryDir, MemoryArchive, BugsNdjson, BugsIndex declared in UCAF_Listener.cs (init order)
    internal static readonly string BugsNdjson;
    internal static readonly string BugsIndex;

    const long BugLedgerMaxSizeBytes = 5L * 1024 * 1024;
    const float BugSimilarityThreshold = 0.7f;
    const int BugSimilarityTopN = 10;

    static void EnsureMemoryDirs()
    {
        try
        {
            Directory.CreateDirectory(MemoryDir);
            Directory.CreateDirectory(MemoryArchive);
        }
        catch { /* listener-safe */ }
    }

    static UCAFBugIndex LoadIndex()
    {
        EnsureMemoryDirs();
        if (!File.Exists(BugsIndex)) return RebuildIndexFromNdjson();
        try
        {
            string json = File.ReadAllText(BugsIndex);
            if (string.IsNullOrWhiteSpace(json)) return new UCAFBugIndex();
            var idx = JsonUtility.FromJson<UCAFBugIndex>(json) ?? new UCAFBugIndex();
            if (idx.bugs == null) idx.bugs = new List<UCAFBugRecord>();
            return idx;
        }
        catch { return RebuildIndexFromNdjson(); }
    }

    static void SaveIndex(UCAFBugIndex idx)
    {
        EnsureMemoryDirs();
        File.WriteAllText(BugsIndex, JsonUtility.ToJson(idx, true));
    }

    static UCAFBugIndex RebuildIndexFromNdjson()
    {
        var idx = new UCAFBugIndex();
        if (!File.Exists(BugsNdjson)) return idx;
        var dict = new Dictionary<string, UCAFBugRecord>();
        int maxId = 0;
        foreach (var line in File.ReadAllLines(BugsNdjson))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            UCAFBugRecord rec = null;
            try { rec = JsonUtility.FromJson<UCAFBugRecord>(line); } catch { }
            if (rec == null || string.IsNullOrEmpty(rec.bug_id)) continue;
            dict[rec.bug_id] = rec;
            if (rec.bug_id.StartsWith("BUG-") && int.TryParse(rec.bug_id.Substring(4), out int n))
                if (n > maxId) maxId = n;
        }
        idx.bugs = dict.Values.OrderBy(b => b.bug_id, StringComparer.Ordinal).ToList();
        idx.next_id = maxId + 1;
        return idx;
    }

    static void AppendNdjson(UCAFBugRecord rec)
    {
        EnsureMemoryDirs();
        RotateIfTooLarge();
        File.AppendAllText(BugsNdjson, JsonUtility.ToJson(rec) + "\n");
    }

    static void RotateIfTooLarge()
    {
        try
        {
            if (!File.Exists(BugsNdjson)) return;
            var fi = new FileInfo(BugsNdjson);
            if (fi.Length < BugLedgerMaxSizeBytes) return;
            string archive = Path.Combine(MemoryArchive,
                $"bugs_{DateTime.UtcNow:yyyy-MM-dd_HH-mm-ss}.ndjson");
            File.Move(BugsNdjson, archive);
        }
        catch { }
    }

    static string IsoNow() => DateTime.UtcNow.ToString("o");

    // ── Helpers: reading list params (CSV) ──────────────────────────────

    static List<string> ParseCsv(string s)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(s)) return list;
        foreach (var p in s.Split(','))
        {
            var t = p.Trim();
            if (!string.IsNullOrEmpty(t)) list.Add(t);
        }
        return list;
    }

    // ── log_bug ─────────────────────────────────────────────────────────

    static UCAFResult CmdLogBug(UCAFCommand cmd)
    {
        string title       = cmd.GetParam("title", "");
        string symptom     = cmd.GetParam("symptom", "");
        string rootCause   = cmd.GetParam("root_cause", "");
        string fix         = cmd.GetParam("fix", "");

        if (string.IsNullOrWhiteSpace(title) ||
            string.IsNullOrWhiteSpace(symptom) ||
            string.IsNullOrWhiteSpace(rootCause) ||
            string.IsNullOrWhiteSpace(fix))
        {
            return new UCAFResult {
                success = false,
                message = "log_bug requires: title, symptom, root_cause, fix"
            };
        }

        var idx = LoadIndex();
        var rec = new UCAFBugRecord {
            bug_id        = $"BUG-{idx.next_id:D4}",
            created_at    = IsoNow(),
            updated_at    = IsoNow(),
            status        = cmd.GetParam("status", "fixed"),
            title         = title,
            symptom       = symptom,
            root_cause    = rootCause,
            fix           = fix,
            fix_undo_group = cmd.GetParam("fix_undo_group", ""),
            introduced_by = cmd.GetParam("introduced_by", ""),
            lessons       = cmd.GetParam("lessons", ""),
            occurrences   = 1,
        };
        rec.scope.files      = ParseCsv(cmd.GetParam("scope_files", ""));
        rec.scope.scenes     = ParseCsv(cmd.GetParam("scope_scenes", ""));
        rec.scope.components = ParseCsv(cmd.GetParam("scope_components", ""));
        rec.tags             = ParseCsv(cmd.GetParam("tags", ""));
        rec.fix_commits      = ParseCsv(cmd.GetParam("fix_commits", ""));
        rec.verification.tests = ParseCsv(cmd.GetParam("verification_tests", ""));
        rec.verification.manual = cmd.GetParam("verification_manual", "");

        AppendNdjson(rec);
        idx.bugs.Add(rec);
        idx.next_id++;
        SaveIndex(idx);

        return new UCAFResult {
            success = true,
            message = $"Logged {rec.bug_id}: {rec.title}",
            data_json = JsonUtility.ToJson(rec)
        };
    }

    // ── update_bug ──────────────────────────────────────────────────────

    static UCAFResult CmdUpdateBug(UCAFCommand cmd)
    {
        string bugId = cmd.GetParam("bug_id", "");
        if (string.IsNullOrEmpty(bugId))
            return new UCAFResult { success = false, message = "bug_id required" };

        var idx = LoadIndex();
        var rec = idx.bugs.FirstOrDefault(b => b.bug_id == bugId);
        if (rec == null)
            return new UCAFResult { success = false, message = $"Bug not found: {bugId}" };

        // patch — only fields explicitly provided
        if (cmd.HasParam("status"))      rec.status = cmd.GetParam("status", rec.status);
        if (cmd.HasParam("title"))       rec.title = cmd.GetParam("title", rec.title);
        if (cmd.HasParam("symptom"))     rec.symptom = cmd.GetParam("symptom", rec.symptom);
        if (cmd.HasParam("root_cause"))  rec.root_cause = cmd.GetParam("root_cause", rec.root_cause);
        if (cmd.HasParam("fix"))         rec.fix = cmd.GetParam("fix", rec.fix);
        if (cmd.HasParam("lessons"))     rec.lessons = cmd.GetParam("lessons", rec.lessons);
        if (cmd.HasParam("fix_undo_group")) rec.fix_undo_group = cmd.GetParam("fix_undo_group", rec.fix_undo_group);
        if (cmd.HasParam("verification_manual")) rec.verification.manual = cmd.GetParam("verification_manual", "");
        if (cmd.HasParam("tags"))        rec.tags = ParseCsv(cmd.GetParam("tags", ""));
        if (cmd.HasParam("scope_files")) rec.scope.files = ParseCsv(cmd.GetParam("scope_files", ""));
        if (cmd.HasParam("scope_components")) rec.scope.components = ParseCsv(cmd.GetParam("scope_components", ""));
        if (cmd.HasParam("scope_scenes"))rec.scope.scenes = ParseCsv(cmd.GetParam("scope_scenes", ""));
        if (cmd.HasParam("fix_commits")) rec.fix_commits = ParseCsv(cmd.GetParam("fix_commits", ""));
        if (cmd.HasParam("verification_tests")) rec.verification.tests = ParseCsv(cmd.GetParam("verification_tests", ""));

        // occurrences=+1 syntax
        string occ = cmd.GetParam("occurrences", "");
        if (!string.IsNullOrEmpty(occ))
        {
            if (occ.StartsWith("+") && int.TryParse(occ.Substring(1), out int delta))
                rec.occurrences += delta;
            else if (int.TryParse(occ, out int abs))
                rec.occurrences = abs;
        }

        rec.updated_at = IsoNow();

        AppendNdjson(rec);
        SaveIndex(idx);

        return new UCAFResult {
            success = true,
            message = $"Updated {rec.bug_id} (occurrences={rec.occurrences}, status={rec.status})",
            data_json = JsonUtility.ToJson(rec)
        };
    }

    // ── query_bugs ──────────────────────────────────────────────────────

    static UCAFResult CmdQueryBugs(UCAFCommand cmd)
    {
        var idx = LoadIndex();
        IEnumerable<UCAFBugRecord> q = idx.bugs;

        string status = cmd.GetParam("status", "");
        if (!string.IsNullOrEmpty(status))
            q = q.Where(b => string.Equals(b.status, status, StringComparison.OrdinalIgnoreCase));

        var tagsAny = ParseCsv(cmd.GetParam("tags_any", ""));
        if (tagsAny.Count > 0)
            q = q.Where(b => b.tags != null && b.tags.Any(t => tagsAny.Contains(t, StringComparer.OrdinalIgnoreCase)));

        var tagsAll = ParseCsv(cmd.GetParam("tags_all", ""));
        if (tagsAll.Count > 0)
            q = q.Where(b => b.tags != null && tagsAll.All(t => b.tags.Contains(t, StringComparer.OrdinalIgnoreCase)));

        string scopeFile = cmd.GetParam("scope_file", "");
        if (!string.IsNullOrEmpty(scopeFile))
            q = q.Where(b => b.scope != null && b.scope.files != null &&
                             b.scope.files.Any(f => f.IndexOf(scopeFile, StringComparison.OrdinalIgnoreCase) >= 0));

        string scopeComponent = cmd.GetParam("scope_component", "");
        if (!string.IsNullOrEmpty(scopeComponent))
            q = q.Where(b => b.scope != null && b.scope.components != null &&
                             b.scope.components.Contains(scopeComponent, StringComparer.OrdinalIgnoreCase));

        string sinceStr = cmd.GetParam("since", "");
        if (!string.IsNullOrEmpty(sinceStr) &&
            DateTime.TryParse(sinceStr, null, DateTimeStyles.RoundtripKind, out DateTime since))
        {
            q = q.Where(b => DateTime.TryParse(b.updated_at, null, DateTimeStyles.RoundtripKind, out var u) && u >= since);
        }

        string untilStr = cmd.GetParam("until", "");
        if (!string.IsNullOrEmpty(untilStr) &&
            DateTime.TryParse(untilStr, null, DateTimeStyles.RoundtripKind, out DateTime until))
        {
            q = q.Where(b => DateTime.TryParse(b.updated_at, null, DateTimeStyles.RoundtripKind, out var u) && u <= until);
        }

        string text = cmd.GetParam("text", "");
        if (!string.IsNullOrEmpty(text))
        {
            string needle = text.ToLowerInvariant();
            q = q.Where(b => Contains(b.title, needle) || Contains(b.symptom, needle) ||
                             Contains(b.root_cause, needle) || Contains(b.lessons, needle) ||
                             Contains(b.fix, needle));
        }

        int limit  = int.TryParse(cmd.GetParam("limit", "50"), out int lim) ? Mathf.Max(1, lim) : 50;
        int offset = int.TryParse(cmd.GetParam("offset", "0"),  out int off) ? Mathf.Max(0, off) : 0;

        var arr = q.ToList();
        var page = arr.Skip(offset).Take(limit).ToList();

        var payload = new UCAFBugList { total = arr.Count, bugs = page };
        return new UCAFResult {
            success = true,
            message = $"Returned {page.Count}/{arr.Count} bug(s)",
            data_json = JsonUtility.ToJson(payload)
        };
    }

    static bool Contains(string s, string needleLower)
        => !string.IsNullOrEmpty(s) && s.ToLowerInvariant().Contains(needleLower);

    // ── find_similar_bugs ───────────────────────────────────────────────
    //
    // TF-IDF / token overlap MVP. Symptom + title + lessons combined.

    static UCAFResult CmdFindSimilarBugs(UCAFCommand cmd)
    {
        string symptom = cmd.GetParam("symptom", "");
        if (string.IsNullOrWhiteSpace(symptom))
            return new UCAFResult { success = false, message = "symptom required" };

        var idx = LoadIndex();
        var queryTokens = Tokenize(symptom);
        if (queryTokens.Count == 0)
            return new UCAFResult { success = false, message = "symptom has no usable tokens" };

        var tagsFilter = ParseCsv(cmd.GetParam("tags", ""));
        string scopeComp = cmd.GetParam("scope_component", "");
        int topN = int.TryParse(cmd.GetParam("top_n", BugSimilarityTopN.ToString()), out int n)
                   ? Mathf.Clamp(n, 1, 50) : BugSimilarityTopN;
        float threshold = float.TryParse(cmd.GetParam("threshold",
                                          BugSimilarityThreshold.ToString(CultureInfo.InvariantCulture)),
                                          NumberStyles.Float, CultureInfo.InvariantCulture,
                                          out float th) ? Mathf.Clamp01(th) : BugSimilarityThreshold;

        // IDF computed on the corpus (idx.bugs)
        var corpus = idx.bugs.Select(b => Tokenize(BugSearchText(b))).ToList();
        var idfMap = ComputeIdf(corpus);

        var matches = new List<UCAFBugMatch>();
        for (int i = 0; i < idx.bugs.Count; i++)
        {
            var b = idx.bugs[i];
            if (tagsFilter.Count > 0 && (b.tags == null ||
                !b.tags.Any(t => tagsFilter.Contains(t, StringComparer.OrdinalIgnoreCase))))
                continue;
            if (!string.IsNullOrEmpty(scopeComp) && (b.scope == null || b.scope.components == null ||
                !b.scope.components.Contains(scopeComp, StringComparer.OrdinalIgnoreCase)))
                continue;

            float score = CosineTfIdf(queryTokens, corpus[i], idfMap);
            if (score >= threshold)
                matches.Add(new UCAFBugMatch { bug = b, similarity_score = score });
        }

        var sorted = matches.OrderByDescending(m => m.similarity_score).Take(topN).ToList();
        var payload = new UCAFBugMatchList { total = sorted.Count, matches = sorted };
        return new UCAFResult {
            success = true,
            message = $"Found {sorted.Count} similar bug(s) (threshold={threshold:F2})",
            data_json = JsonUtility.ToJson(payload)
        };
    }

    static string BugSearchText(UCAFBugRecord b)
        => string.Join(" ",
            b.title ?? "",
            b.symptom ?? "",
            b.root_cause ?? "",
            b.lessons ?? "",
            string.Join(" ", b.tags ?? new List<string>()));

    static readonly Regex TokenRegex = new Regex(@"[a-zA-Z][a-zA-Z0-9_]+", RegexOptions.Compiled);
    static readonly HashSet<string> StopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        "the","a","an","and","or","of","in","on","to","is","are","was","were","be","with",
        "for","by","at","as","it","this","that","its",
        "je","se","na","do","od","po","při","kdy","co","jak","že","ale","ani","tak","ten","tu",
        "the","a","by","si","ze","z","v","u"
    };

    static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(text)) return tokens;
        foreach (Match m in TokenRegex.Matches(text))
        {
            var t = m.Value.ToLowerInvariant();
            if (t.Length < 3) continue;
            if (StopWords.Contains(t)) continue;
            tokens.Add(t);
        }
        return tokens;
    }

    static Dictionary<string, float> ComputeIdf(List<List<string>> corpus)
    {
        var dfMap = new Dictionary<string, int>();
        foreach (var doc in corpus)
        {
            foreach (var t in doc.Distinct())
                dfMap[t] = dfMap.TryGetValue(t, out int v) ? v + 1 : 1;
        }
        var idfMap = new Dictionary<string, float>();
        int n = Mathf.Max(1, corpus.Count);
        foreach (var kv in dfMap)
            idfMap[kv.Key] = Mathf.Log(1f + (float)n / (1 + kv.Value));
        return idfMap;
    }

    static float CosineTfIdf(List<string> q, List<string> d, Dictionary<string, float> idfMap)
    {
        if (q.Count == 0 || d.Count == 0) return 0f;

        var qTf = new Dictionary<string, float>();
        foreach (var t in q) qTf[t] = qTf.TryGetValue(t, out float v) ? v + 1 : 1;
        var dTf = new Dictionary<string, float>();
        foreach (var t in d) dTf[t] = dTf.TryGetValue(t, out float v) ? v + 1 : 1;

        float dot = 0, qNorm = 0, dNorm = 0;
        foreach (var kv in qTf)
        {
            float idf = idfMap.TryGetValue(kv.Key, out float i) ? i : 1f;
            float qw = kv.Value * idf;
            qNorm += qw * qw;
            if (dTf.TryGetValue(kv.Key, out float dt))
                dot += qw * (dt * idf);
        }
        foreach (var kv in dTf)
        {
            float idf = idfMap.TryGetValue(kv.Key, out float i) ? i : 1f;
            float dw = kv.Value * idf;
            dNorm += dw * dw;
        }
        if (qNorm <= 0 || dNorm <= 0) return 0f;
        return dot / (Mathf.Sqrt(qNorm) * Mathf.Sqrt(dNorm));
    }

    // ── get_bug ─────────────────────────────────────────────────────────

    static UCAFResult CmdGetBug(UCAFCommand cmd)
    {
        string bugId = cmd.GetParam("bug_id", "");
        if (string.IsNullOrEmpty(bugId))
            return new UCAFResult { success = false, message = "bug_id required" };
        var idx = LoadIndex();
        var rec = idx.bugs.FirstOrDefault(b => b.bug_id == bugId);
        if (rec == null) return new UCAFResult { success = false, message = $"Bug not found: {bugId}" };
        return new UCAFResult {
            success = true,
            message = $"{rec.bug_id}: {rec.title}",
            data_json = JsonUtility.ToJson(rec)
        };
    }

    // ── close_bug ───────────────────────────────────────────────────────

    static UCAFResult CmdCloseBug(UCAFCommand cmd)
    {
        string bugId = cmd.GetParam("bug_id", "");
        string resolution = cmd.GetParam("resolution", "fixed");
        if (string.IsNullOrEmpty(bugId))
            return new UCAFResult { success = false, message = "bug_id required" };
        var idx = LoadIndex();
        var rec = idx.bugs.FirstOrDefault(b => b.bug_id == bugId);
        if (rec == null) return new UCAFResult { success = false, message = $"Bug not found: {bugId}" };
        rec.status = resolution;
        if (resolution == "duplicate")
        {
            string dup = cmd.GetParam("duplicate_of", "");
            if (string.IsNullOrEmpty(dup))
                return new UCAFResult { success = false, message = "duplicate_of required when resolution=duplicate" };
            rec.duplicate_of = dup;
        }
        rec.updated_at = IsoNow();
        AppendNdjson(rec);
        SaveIndex(idx);
        return new UCAFResult { success = true, message = $"Closed {rec.bug_id} as {resolution}" };
    }

    // ── purge_bug (admin) ───────────────────────────────────────────────

    static UCAFResult CmdPurgeBug(UCAFCommand cmd)
    {
        string bugId = cmd.GetParam("bug_id", "");
        string reason = cmd.GetParam("reason", "");
        if (string.IsNullOrEmpty(bugId) || string.IsNullOrEmpty(reason))
            return new UCAFResult { success = false, message = "bug_id and reason required" };
        var idx = LoadIndex();
        var rec = idx.bugs.FirstOrDefault(b => b.bug_id == bugId);
        if (rec == null) return new UCAFResult { success = false, message = $"Bug not found: {bugId}" };
        idx.bugs.Remove(rec);
        SaveIndex(idx);
        try
        {
            string auditLine = JsonUtility.ToJson(new UCAFBugRecord {
                bug_id = bugId,
                status = "purged",
                lessons = $"PURGED: {reason}",
                updated_at = IsoNow()
            });
            string auditFile = Path.Combine(MemoryDir, "purge_audit.ndjson");
            File.AppendAllText(auditFile, auditLine + "\n");
        }
        catch { }
        Debug.LogWarning($"[UCAF] purge_bug {bugId} — reason: {reason}");
        return new UCAFResult { success = true, message = $"Purged {bugId}" };
    }
}
