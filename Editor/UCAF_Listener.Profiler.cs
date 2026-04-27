using UnityEngine;
using UnityEditor;
using Unity.Profiling;
using System;
using System.IO;
using System.Collections.Generic;
using System.Globalization;

public static partial class UCAF_Listener
{
    // ── Profiler (v4.2 Phase D, FR-151–153) ─────────────────────────────

    const string SS_PendingProfilerId       = "UCAF_PendingProfilerId";
    const string SS_PendingProfilerTarget   = "UCAF_PendingProfilerTarget";
    const string SS_PendingProfilerCount    = "UCAF_PendingProfilerCount";
    const string SS_PendingProfilerMetrics  = "UCAF_PendingProfilerMetrics";
    const string SS_PendingProfilerSaveAs   = "UCAF_PendingProfilerSaveAs";

    const string SS_PendingRecordId     = "UCAF_PendingRecordId";
    const string SS_PendingRecordTarget = "UCAF_PendingRecordTarget";
    const string SS_PendingRecordCount  = "UCAF_PendingRecordCount";
    const string SS_PendingRecordOutput = "UCAF_PendingRecordOutput";

    // Not in SessionState — lost on domain reload; null-checked before use
    static List<ProfilerRecorder> _profilerRecorders;
    static List<string>           _profilerRecorderNames;

    // FR-151: profiler_snapshot  — async, returns null
    static UCAFResult CmdProfilerSnapshot(UCAFCommand cmd)
    {
        string id     = cmd.id;
        int    frames = int.TryParse(cmd.GetParam("frames", "10"), out int f) ? Math.Max(1, f) : 10;
        string metrics = cmd.GetParam("metrics", "CPU,Memory.TotalReserved,Render.DrawCalls");
        string saveAs  = cmd.GetParam("save_as", "");

        _profilerRecorders     = new List<ProfilerRecorder>();
        _profilerRecorderNames = new List<string>();

        foreach (var rawName in metrics.Split(','))
        {
            string name = rawName.Trim();
            var (cat, stat, _) = ResolveProfilerMetric(name);
            var rec = ProfilerRecorder.StartNew(cat, stat, frames);
            _profilerRecorders.Add(rec);
            _profilerRecorderNames.Add(name);
        }

        string profilerDir = Path.Combine(WorkspacePath, "profiler");
        Directory.CreateDirectory(profilerDir);

        SessionState.SetString(SS_PendingProfilerId,      id);
        SessionState.SetInt   (SS_PendingProfilerTarget,  frames);
        SessionState.SetInt   (SS_PendingProfilerCount,   0);
        SessionState.SetString(SS_PendingProfilerMetrics, metrics);
        SessionState.SetString(SS_PendingProfilerSaveAs,  saveAs);

        return null; // deferred
    }

    internal static void CheckPendingProfilerSnapshot()
    {
        string id = SessionState.GetString(SS_PendingProfilerId, "");
        if (string.IsNullOrEmpty(id)) return;

        int count  = SessionState.GetInt(SS_PendingProfilerCount, 0) + 1;
        int target = SessionState.GetInt(SS_PendingProfilerTarget, 10);
        SessionState.SetInt(SS_PendingProfilerCount, count);
        if (count < target) return;

        // Done collecting
        SessionState.EraseString(SS_PendingProfilerId);

        if (_profilerRecorders == null)
        {
            WriteResult(DonePath, id, new UCAFResult {
                success = false,
                message = "Profiler recorders were lost to a domain reload. Re-issue profiler_snapshot."
            });
            return;
        }

        string saveAs = SessionState.GetString(SS_PendingProfilerSaveAs, "");
        var snapshot = new UCAFProfilerSnapshot {
            snapshot_id = string.IsNullOrEmpty(saveAs) ? id : saveAs,
            timestamp   = DateTime.UtcNow.ToString("o"),
            frames      = target
        };

        for (int i = 0; i < _profilerRecorders.Count; i++)
        {
            var rec = _profilerRecorders[i];
            if (rec.Valid && rec.Count > 0)
            {
                var (_, _, unit) = ResolveProfilerMetric(_profilerRecorderNames[i]);
                double raw = rec.LastValue;
                double val = unit == "ms" ? raw / 1_000_000.0 :
                             unit == "MB" ? raw / (1024.0 * 1024.0) : raw;
                snapshot.metrics.Add(new UCAFProfilerMetric { name = _profilerRecorderNames[i], value = val, unit = unit });
            }
            rec.Dispose();
        }
        _profilerRecorders     = null;
        _profilerRecorderNames = null;

        if (!string.IsNullOrEmpty(saveAs))
        {
            string snapshotPath = Path.Combine(WorkspacePath, "profiler", saveAs + ".json");
            File.WriteAllText(snapshotPath, JsonUtility.ToJson(snapshot));
        }

        WriteResult(DonePath, id, new UCAFResult {
            success   = true,
            message   = $"Profiler snapshot: {snapshot.metrics.Count} metrics over {target} frames.",
            data_json = JsonUtility.ToJson(snapshot)
        });
    }

    // FR-152: profiler_compare
    static UCAFResult CmdProfilerCompare(UCAFCommand cmd)
    {
        string idA = cmd.GetParam("snapshot_a", "");
        string idB = cmd.GetParam("snapshot_b", "");
        if (string.IsNullOrEmpty(idA) || string.IsNullOrEmpty(idB))
            return new UCAFResult { success = false, message = "snapshot_a and snapshot_b required (IDs used in save_as)." };

        string profilerDir = Path.Combine(WorkspacePath, "profiler");
        string pathA = Path.Combine(profilerDir, idA + ".json");
        string pathB = Path.Combine(profilerDir, idB + ".json");
        if (!File.Exists(pathA)) return new UCAFResult { success = false, message = $"Snapshot '{idA}' not found." };
        if (!File.Exists(pathB)) return new UCAFResult { success = false, message = $"Snapshot '{idB}' not found." };

        var snapA = JsonUtility.FromJson<UCAFProfilerSnapshot>(File.ReadAllText(pathA));
        var snapB = JsonUtility.FromJson<UCAFProfilerSnapshot>(File.ReadAllText(pathB));

        var dictA = new Dictionary<string, double>();
        foreach (var m in snapA.metrics) dictA[m.name] = m.value;

        var compare = new UCAFProfilerCompare { snapshot_a = idA, snapshot_b = idB };
        foreach (var mB in snapB.metrics)
        {
            dictA.TryGetValue(mB.name, out double valA);
            double delta = mB.value - valA;
            double pct   = valA != 0 ? delta / valA * 100.0 : 0;
            compare.diffs.Add(new UCAFProfilerDiff {
                name = mB.name, value_a = valA, value_b = mB.value, delta = delta, delta_pct = pct
            });
        }

        return new UCAFResult {
            success   = true,
            message   = $"Compared '{idA}' vs '{idB}': {compare.diffs.Count} metrics.",
            data_json = JsonUtility.ToJson(compare)
        };
    }

    // FR-153: profiler_record  — saves accumulated ProfilerRecorder data to JSON
    // Note: Profiler.logFile/.enableBinaryLog are deprecated in Unity 6.
    // We instead record via ProfilerRecorder and flush to our own JSON format.
    static UCAFResult CmdProfilerRecord(UCAFCommand cmd)
    {
        string outputPath = cmd.GetParam("output_path", "");
        if (string.IsNullOrEmpty(outputPath))
            return new UCAFResult { success = false, message = "output_path required." };
        int frames  = int.TryParse(cmd.GetParam("frames",  "300"), out int f) ? Math.Max(1, f) : 300;
        string metrics = cmd.GetParam("metrics", "CPU,Memory.TotalReserved,Render.DrawCalls,Render.Triangles");

        if (!Path.IsPathRooted(outputPath))
            outputPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), outputPath);
        string outDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

        // Re-use the snapshot mechanism with per-frame accumulation
        _profilerRecorders     = new List<ProfilerRecorder>();
        _profilerRecorderNames = new List<string>();
        foreach (var rawName in metrics.Split(','))
        {
            string name = rawName.Trim();
            var (cat, stat, _) = ResolveProfilerMetric(name);
            _profilerRecorders.Add(ProfilerRecorder.StartNew(cat, stat, frames));
            _profilerRecorderNames.Add(name);
        }

        SessionState.SetString(SS_PendingRecordId,     cmd.id);
        SessionState.SetInt   (SS_PendingRecordTarget, frames);
        SessionState.SetInt   (SS_PendingRecordCount,  0);
        SessionState.SetString(SS_PendingRecordOutput, outputPath);

        return null; // deferred
    }

    internal static void CheckPendingProfilerRecord()
    {
        string id = SessionState.GetString(SS_PendingRecordId, "");
        if (string.IsNullOrEmpty(id)) return;

        int count  = SessionState.GetInt(SS_PendingRecordCount, 0) + 1;
        int target = SessionState.GetInt(SS_PendingRecordTarget, 300);
        SessionState.SetInt(SS_PendingRecordCount, count);
        if (count < target) return;

        SessionState.EraseString(SS_PendingRecordId);
        string outputPath = SessionState.GetString(SS_PendingRecordOutput, "");

        if (_profilerRecorders == null)
        {
            WriteResult(DonePath, id, new UCAFResult { success = false, message = "Profiler recorders lost to domain reload. Re-issue profiler_record." });
            return;
        }

        // Build per-frame sample lines (NDJSON)
        var lines = new System.Text.StringBuilder();
        for (int frame = 0; frame < target; frame++)
        {
            var snap = new UCAFProfilerSnapshot { snapshot_id = id, timestamp = "", frames = 1 };
            for (int i = 0; i < _profilerRecorders.Count; i++)
            {
                var rec = _profilerRecorders[i];
                if (!rec.Valid || rec.Count == 0) continue;
                int sampleIdx = rec.Count - target + frame;
                if (sampleIdx < 0 || sampleIdx >= rec.Count) continue;
                var (_, _, unit) = ResolveProfilerMetric(_profilerRecorderNames[i]);
                double raw = rec.GetSample(sampleIdx).Value;
                double val = unit == "ms" ? raw / 1_000_000.0 : unit == "MB" ? raw / (1024.0 * 1024.0) : raw;
                snap.metrics.Add(new UCAFProfilerMetric { name = _profilerRecorderNames[i], value = val, unit = unit });
            }
            lines.AppendLine(JsonUtility.ToJson(snap));
        }

        foreach (var rec in _profilerRecorders) rec.Dispose();
        _profilerRecorders     = null;
        _profilerRecorderNames = null;

        File.WriteAllText(outputPath, lines.ToString());
        long size = new FileInfo(outputPath).Length;

        WriteResult(DonePath, id, new UCAFResult {
            success = true,
            message = $"Profiler recording saved: {outputPath} ({size / 1024} KB, {target} frames, NDJSON)."
        });
    }

    static (ProfilerCategory category, string statName, string unit) ResolveProfilerMetric(string name)
    {
        switch (name)
        {
            case "CPU":                  return (ProfilerCategory.Internal, "Main Thread",               "ms");
            case "GPU":                  return (ProfilerCategory.Render,   "GPU Frame Time",            "ms");
            case "Memory.TotalReserved": return (ProfilerCategory.Memory,   "Total Reserved Memory",     "MB");
            case "Memory.GCAlloc":       return (ProfilerCategory.Memory,   "GC Allocated In Frame",     "MB");
            case "Render.DrawCalls":     return (ProfilerCategory.Render,   "Draw Calls Count",          "count");
            case "Render.Batches":       return (ProfilerCategory.Render,   "Batches Count",             "count");
            case "Render.Triangles":     return (ProfilerCategory.Render,   "Triangles Count",           "count");
            case "Render.Vertices":      return (ProfilerCategory.Render,   "Vertices Count",            "count");
            case "Physics.Active":       return (ProfilerCategory.Physics,  "Active Rigidbodies",        "count");
            default:                     return (ProfilerCategory.Internal, name,                        "value");
        }
    }
}
