using UnityEngine;
using UnityEditor;
using UnityEditor.Recorder;
using UnityEditor.Recorder.Input;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

public static partial class UCAF_Listener
{
    // ── Video recording + frame extraction (v4.2 Phase F, FR-186 to FR-191) ──
    //
    // Uses com.unity.recorder (5.x). Recording runs during Play Mode only.
    // PNG sequence is primary format; MP4 is opt-in.
    // Frames extracted by copying relevant PNGs to sessions/frames/ subfolder.

    static RecorderController _recController;
    static RecorderControllerSettings _recControllerSettings;
    static string _recSessionId;
    static string _recOutputDir;
    static int    _recStartFrame;
    static DateTime _recStartTime;
    static string _recFormat;
    static int    _recFps;

    static string RecordingsDir => Path.Combine(WorkspacePath, "recordings");

    // ── recording_start ───────────────────────────────────────────────────────

    static UCAFResult CmdRecordingStart(UCAFCommand cmd)
    {
        string sessionId = cmd.GetParam("session_id", $"rec_{DateTime.UtcNow:yyyyMMdd_HHmmss}");
        string format    = cmd.GetParam("format", "png_sequence");
        int    fps       = int.TryParse(cmd.GetParam("fps", "30"), out int f) ? f : 30;
        int    width     = int.TryParse(cmd.GetParam("width", "1920"), out int w) ? w : 1920;
        int    height    = int.TryParse(cmd.GetParam("height", "1080"), out int h) ? h : 1080;

        string err = StartRecordingInternal(sessionId, format, fps, width, height);
        if (err != null)
            return new UCAFResult { success = false, message = err };

        return new UCAFResult {
            success   = true,
            message   = $"Recording started: {sessionId} ({format}, {fps} fps)",
            data_json = JsonUtility.ToJson(new UCAFRecordingInfo {
                session_id  = sessionId,
                started_at  = _recStartTime.ToString("o"),
                format      = _recFormat,
                fps         = _recFps,
                output_path = _recOutputDir
            })
        };
    }

    // Returns null on success, error message on failure.
    internal static string StartRecordingInternal(string sessionId, string format, int fps, int width, int height)
    {
        if (!EditorApplication.isPlaying)
            return "Play Mode required. Call playmode_enter first.";

        if (_recController != null && _recController.IsRecording())
            return $"Already recording session '{_recSessionId}'. Call recording_stop first.";

        string outputDir = Path.Combine(RecordingsDir, sessionId);
        Directory.CreateDirectory(outputDir);

        try
        {
            _recControllerSettings = ScriptableObject.CreateInstance<RecorderControllerSettings>();
            _recControllerSettings.SetRecordModeToManual();
            _recControllerSettings.FrameRate = fps;

            // MP4 is opt-in and uses a different API per Recorder version — use PNG sequence as primary.
            // For MP4, we configure via reflection to avoid API surface differences.
            if (format == "mp4")
            {
                var movieRec = ScriptableObject.CreateInstance<MovieRecorderSettings>();
                movieRec.name    = "UCAF_Movie";
                movieRec.Enabled = true;
                movieRec.EncoderSettings = new CoreEncoderSettings { Codec = CoreEncoderSettings.OutputCodec.H264 };
                // imageInputSettings property name differs per Recorder version — use reflection
                SetInputSettingsReflection(movieRec, width, height);
                SetRecorderOutputPath(movieRec.FileNameGenerator, outputDir, sessionId);
                _recControllerSettings.AddRecorderSettings(movieRec);
            }
            else // png_sequence
            {
                var imageRec = ScriptableObject.CreateInstance<ImageRecorderSettings>();
                imageRec.name    = "UCAF_PNG";
                imageRec.Enabled = true;
                imageRec.OutputFormat = ImageRecorderSettings.ImageRecorderOutputFormat.PNG;
                imageRec.imageInputSettings = new GameViewInputSettings {
                    OutputWidth  = width,
                    OutputHeight = height
                };
                SetRecorderOutputPath(imageRec.FileNameGenerator, outputDir, "frame_<Frame>");
                _recControllerSettings.AddRecorderSettings(imageRec);
            }

            _recController = new RecorderController(_recControllerSettings);
            _recController.PrepareRecording();
            _recController.StartRecording();

            _recSessionId  = sessionId;
            _recOutputDir  = outputDir;
            _recStartFrame = Time.frameCount;
            _recStartTime  = DateTime.UtcNow;
            _recFormat     = format;
            _recFps        = fps;

            // Persist session info for recording_list
            var info = new UCAFRecordingInfo {
                session_id  = sessionId,
                started_at  = _recStartTime.ToString("o"),
                format      = format,
                fps         = fps,
                output_path = outputDir
            };
            File.WriteAllText(Path.Combine(outputDir, "session_info.json"), JsonUtility.ToJson(info));
            File.WriteAllText(Path.Combine(outputDir, "recording_events.ndjson"), "");

            return null;
        }
        catch (Exception ex)
        {
            _recController = null;
            _recControllerSettings = null;
            return $"recording_start failed: {ex.Message}";
        }
    }

    // Set output path via reflection to avoid OutputPath enum differences across Recorder versions.
    static void SetRecorderOutputPath(FileNameGenerator gen, string dir, string leaf)
    {
        gen.Leaf = leaf;

        var t = gen.GetType();
        // Try to set m_Path (the internal absolute directory field)
        string[] pathCandidates = { "m_Path", "_path", "m_AbsolutePath" };
        bool pathSet = false;
        foreach (var name in pathCandidates)
        {
            var field = t.GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field != null && field.FieldType == typeof(string))
            {
                field.SetValue(gen, dir);
                pathSet = true;
                break;
            }
        }

        // Try to set the Root field to the enum value that represents an absolute/explicit path
        var rootField = t.GetField("m_Root", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (rootField != null)
        {
            // Find the enum value named "Absolute", "AbsolutePath", or highest index (fallback)
            var enumType = rootField.FieldType;
            if (enumType.IsEnum)
            {
                var names = System.Enum.GetNames(enumType);
                var values = System.Enum.GetValues(enumType);
                object chosen = null;
                foreach (var n in new[] { "Absolute", "AbsolutePath", "Specific" })
                {
                    int idx = System.Array.IndexOf(names, n);
                    if (idx >= 0) { chosen = values.GetValue(idx); break; }
                }
                if (chosen == null && values.Length > 0) chosen = values.GetValue(values.Length - 1);
                if (chosen != null) rootField.SetValue(gen, chosen);
            }
        }

        if (!pathSet)
            gen.Leaf = Path.Combine(dir, leaf).Replace("\\", "/");
    }

    // Set imageInputSettings on any RecorderSettings subclass via reflection.
    static void SetInputSettingsReflection(RecorderSettings rec, int width, int height)
    {
        var input = new GameViewInputSettings { OutputWidth = width, OutputHeight = height };
        var t = rec.GetType();
        // Walk up the type hierarchy looking for the input settings property/field
        while (t != null && t != typeof(UnityEngine.Object))
        {
            var prop = t.GetProperty("imageInputSettings",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            if (prop != null && prop.CanWrite)
            {
                prop.SetValue(rec, input);
                return;
            }
            var field = t.GetField("m_ImageInputSettings",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            if (field != null)
            {
                field.SetValue(rec, input);
                return;
            }
            t = t.BaseType;
        }
    }

    // ── recording_stop ────────────────────────────────────────────────────────

    static UCAFResult CmdRecordingStop(UCAFCommand cmd)
    {
        if (_recController == null || !_recController.IsRecording())
            return new UCAFResult { success = false, message = "No active recording. Call recording_start first." };

        var info = StopRecordingInternal();
        return new UCAFResult {
            success   = true,
            message   = $"Recording stopped: {info.session_id} — {info.frame_count} frames, {info.duration_s:F1}s",
            data_json = JsonUtility.ToJson(info)
        };
    }

    internal static UCAFRecordingInfo StopRecordingInternal()
    {
        if (_recController == null) return null;

        _recController.StopRecording();

        int    frameCount = Time.frameCount - _recStartFrame;
        float  durationS  = (float)(DateTime.UtcNow - _recStartTime).TotalSeconds;
        long   sizeBytes  = DirSize(_recOutputDir);

        var info = new UCAFRecordingInfo {
            session_id  = _recSessionId,
            started_at  = _recStartTime.ToString("o"),
            format      = _recFormat,
            fps         = _recFps,
            output_path = _recOutputDir,
            frame_count = frameCount,
            duration_s  = durationS,
            size_bytes  = sizeBytes
        };

        // Update session_info.json with final stats
        try { File.WriteAllText(Path.Combine(_recOutputDir, "session_info.json"), JsonUtility.ToJson(info)); }
        catch { }

        // Cleanup controller
        UnityEngine.Object.DestroyImmediate(_recControllerSettings);
        _recController         = null;
        _recControllerSettings = null;
        _recSessionId          = null;
        _recOutputDir          = null;

        return info;
    }

    // ── recording_extract_frames ──────────────────────────────────────────────

    static UCAFResult CmdRecordingExtractFrames(UCAFCommand cmd)
    {
        string sessionId    = cmd.GetParam("session_id", "");
        string events       = cmd.GetParam("events",     "all");
        int contextFrames   = int.TryParse(cmd.GetParam("context_frames", "5"), out int cf) ? cf : 5;

        if (string.IsNullOrEmpty(sessionId))
            return new UCAFResult { success = false, message = "session_id required." };

        string sessionDir = Path.Combine(RecordingsDir, sessionId);
        if (!Directory.Exists(sessionDir))
            return new UCAFResult { success = false, message = $"Session not found: {sessionId}" };

        // Find all PNG frames in session dir
        var allPngs = Directory.GetFiles(sessionDir, "*.png")
                               .OrderBy(p => p)
                               .ToArray();

        if (allPngs.Length == 0)
            return new UCAFResult { success = false, message = "No PNG frames found in session. Recording may still be in progress or used MP4 format." };

        // Determine which frame indices to extract around
        var centerFrames = new HashSet<int>();

        if (events == "all" || events == "event")
        {
            // Read recording_events.ndjson
            string eventsPath = Path.Combine(sessionDir, "recording_events.ndjson");
            if (File.Exists(eventsPath))
            {
                foreach (var line in File.ReadAllLines(eventsPath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    // Extract frame number from "frame" field
                    int fi = ExtractIntField(line, "frame");
                    if (fi >= 0) centerFrames.Add(fi);
                }
            }
        }

        if (events == "all" || events == "signal")
        {
            // Read runtime_signals.json for any signals emitted during recording
            string sigPath = Path.Combine(WorkspacePath, "commands", "runtime_signals.json");
            if (File.Exists(sigPath))
            {
                string sj = File.ReadAllText(sigPath);
                // Extract frame numbers
                int pos = 0;
                while ((pos = sj.IndexOf("\"frame\":", pos)) >= 0)
                {
                    int start = pos + 8;
                    int end   = sj.IndexOfAny(new[] { ',', '}' }, start);
                    if (end > start && int.TryParse(sj.Substring(start, end - start).Trim(), out int fi))
                        centerFrames.Add(fi);
                    pos = end + 1;
                }
            }
        }

        if (centerFrames.Count == 0)
        {
            // No events — extract first and last N frames as fallback
            for (int i = 0; i < Math.Min(contextFrames, allPngs.Length); i++)
                centerFrames.Add(i);
            centerFrames.Add(allPngs.Length - 1);
        }

        // Collect all frame indices within ±contextFrames of each center
        var indicesToExtract = new SortedSet<int>();
        foreach (int center in centerFrames)
        {
            for (int i = Math.Max(0, center - contextFrames);
                     i <= Math.Min(allPngs.Length - 1, center + contextFrames); i++)
                indicesToExtract.Add(i);
        }

        // Copy selected frames to frames/ subdir
        string framesDir = Path.Combine(sessionDir, "frames");
        Directory.CreateDirectory(framesDir);

        var copiedPaths = new List<string>();
        foreach (int idx in indicesToExtract)
        {
            if (idx >= allPngs.Length) continue;
            string src  = allPngs[idx];
            string dest = Path.Combine(framesDir, Path.GetFileName(src));
            try
            {
                File.Copy(src, dest, overwrite: true);
                copiedPaths.Add(dest);
            }
            catch { }
        }

        var result = new UCAFFrameExtractResult {
            session_id       = sessionId,
            frames_extracted = copiedPaths.Count,
            frame_paths      = copiedPaths
        };

        return new UCAFResult {
            success   = copiedPaths.Count > 0,
            message   = $"Extracted {copiedPaths.Count} frames from session '{sessionId}'",
            data_json = JsonUtility.ToJson(result)
        };
    }

    // ── recording_list ────────────────────────────────────────────────────────

    static UCAFResult CmdRecordingList(UCAFCommand cmd)
    {
        Directory.CreateDirectory(RecordingsDir);
        var list = new UCAFRecordingList();

        foreach (var dir in Directory.GetDirectories(RecordingsDir))
        {
            string infoPath = Path.Combine(dir, "session_info.json");
            if (!File.Exists(infoPath)) continue;
            try
            {
                var info = JsonUtility.FromJson<UCAFRecordingInfo>(File.ReadAllText(infoPath));
                if (info.size_bytes == 0) info.size_bytes = DirSize(dir);
                if (info.frame_count == 0)
                    info.frame_count = Directory.GetFiles(dir, "*.png").Length;
                list.recordings.Add(info);
            }
            catch { }
        }

        list.total = list.recordings.Count;
        return new UCAFResult {
            success   = true,
            message   = $"{list.total} recording(s)",
            data_json = JsonUtility.ToJson(list)
        };
    }

    // ── recording_delete ──────────────────────────────────────────────────────

    static UCAFResult CmdRecordingDelete(UCAFCommand cmd)
    {
        if (cmd.GetParam("confirm", "false") != "true")
            return new UCAFResult { success = false, message = "This permanently deletes recording data. Pass confirm=true to proceed." };

        string sessionId   = cmd.GetParam("session_id", "");
        string olderThanStr = cmd.GetParam("older_than_days", "");
        int deleted = 0;

        if (!string.IsNullOrEmpty(sessionId))
        {
            string dir = Path.Combine(RecordingsDir, sessionId);
            if (!Directory.Exists(dir))
                return new UCAFResult { success = false, message = $"Session not found: {sessionId}" };
            Directory.Delete(dir, recursive: true);
            deleted = 1;
        }
        else if (!string.IsNullOrEmpty(olderThanStr) &&
                 int.TryParse(olderThanStr, out int days))
        {
            DateTime cutoff = DateTime.UtcNow.AddDays(-days);
            foreach (var dir in Directory.GetDirectories(RecordingsDir))
            {
                string infoPath = Path.Combine(dir, "session_info.json");
                if (!File.Exists(infoPath)) continue;
                try
                {
                    var info = JsonUtility.FromJson<UCAFRecordingInfo>(File.ReadAllText(infoPath));
                    if (DateTime.TryParse(info.started_at, null,
                            System.Globalization.DateTimeStyles.RoundtripKind, out DateTime t) &&
                        t < cutoff)
                    {
                        Directory.Delete(dir, recursive: true);
                        deleted++;
                    }
                }
                catch { }
            }
        }
        else
        {
            return new UCAFResult { success = false, message = "Provide session_id or older_than_days." };
        }

        return new UCAFResult { success = true, message = $"Deleted {deleted} recording session(s)." };
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    // Called by PlayMode2 after stopping for record_video=true (FR-191)
    internal static List<string> ExtractFramesAllEvents(string sessionId, int contextFrames)
    {
        var fakeCmd = new UCAFCommand { id = "auto_extract", type = "recording_extract_frames" };
        fakeCmd.params_list.Add(new UCAFParam { key = "session_id",     value = sessionId });
        fakeCmd.params_list.Add(new UCAFParam { key = "events",         value = "all" });
        fakeCmd.params_list.Add(new UCAFParam { key = "context_frames", value = contextFrames.ToString() });
        var result = CmdRecordingExtractFrames(fakeCmd);
        if (!result.success || string.IsNullOrEmpty(result.data_json))
            return new List<string>();
        try
        {
            return JsonUtility.FromJson<UCAFFrameExtractResult>(result.data_json)?.frame_paths
                   ?? new List<string>();
        }
        catch { return new List<string>(); }
    }

    static long DirSize(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        try
        {
            return Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
                            .Sum(f => new FileInfo(f).Length);
        }
        catch { return 0; }
    }

    static int ExtractIntField(string json, string fieldName)
    {
        string key = $"\"{fieldName}\":";
        int idx = json.IndexOf(key, StringComparison.Ordinal);
        if (idx < 0) return -1;
        int start = idx + key.Length;
        while (start < json.Length && json[start] == ' ') start++;
        int end = start;
        while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-')) end++;
        return int.TryParse(json.Substring(start, end - start), out int v) ? v : -1;
    }
}
