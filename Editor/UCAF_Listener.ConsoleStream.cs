using UnityEngine;
using UnityEditor;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

public static partial class UCAF_Listener
{
    // ── Console subscribe / streaming (v4.2 Phase E, FR-179 to FR-182) ──────
    //
    // File-based streaming: each subscription writes matching log entries
    // as NDJSON to ucaf_workspace/streams/{id}.ndjson.
    // A separate Application.logMessageReceived handler is registered lazily
    // on the first subscribe — avoids always-on overhead when no subscribers.

    sealed class ConsoleSubState
    {
        public string level;
        public string pattern;
        public string from_assembly;
        public string stream_path;
        public int    entries_written;
    }

    static readonly Dictionary<string, ConsoleSubState> _consoleStreams
        = new Dictionary<string, ConsoleSubState>();

    static readonly object _streamLock = new object();
    static bool _streamHandlerRegistered;

    static void EnsureStreamHandlerRegistered()
    {
        if (_streamHandlerRegistered) return;
        Application.logMessageReceived += OnLogReceivedStream;
        _streamHandlerRegistered = true;
    }

    static void OnLogReceivedStream(string condition, string stackTrace, LogType logType)
    {
        if (_consoleStreams.Count == 0) return;

        var entry = new UCAFLogEntry {
            timestamp   = DateTime.UtcNow.ToString("o"),
            type        = logType.ToString(),
            message     = condition ?? "",
            stack_trace = stackTrace ?? ""
        };
        string line = JsonUtility.ToJson(entry) + "\n";

        lock (_streamLock)
        {
            foreach (var kv in _consoleStreams)
            {
                if (!StreamMatchesFilter(entry, kv.Value)) continue;
                try
                {
                    File.AppendAllText(kv.Value.stream_path, line);
                    kv.Value.entries_written++;
                }
                catch { }
            }
        }
    }

    static bool StreamMatchesFilter(UCAFLogEntry entry, ConsoleSubState sub)
    {
        if (!string.IsNullOrEmpty(sub.level) && sub.level != "All")
        {
            bool levelOk = sub.level switch {
                "Error"   => entry.type == "Error" || entry.type == "Exception" || entry.type == "Assert",
                "Warning" => entry.type == "Warning",
                "Log"     => entry.type == "Log",
                _         => true
            };
            if (!levelOk) return false;
        }

        if (!string.IsNullOrEmpty(sub.pattern))
        {
            try { if (!Regex.IsMatch(entry.message, sub.pattern)) return false; }
            catch { }
        }

        if (!string.IsNullOrEmpty(sub.from_assembly) &&
            !entry.stack_trace.Contains(sub.from_assembly))
            return false;

        return true;
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    static UCAFResult CmdConsoleSubscribe(UCAFCommand cmd)
    {
        EnsureStreamHandlerRegistered();

        string id           = $"sub_{DateTime.UtcNow.Ticks}";
        string level        = cmd.GetParam("level",        "All");
        string pattern      = cmd.GetParam("pattern",      "");
        string fromAssembly = cmd.GetParam("from_assembly","");

        string streamsDir = Path.Combine(WorkspacePath, "streams");
        Directory.CreateDirectory(streamsDir);
        string streamPath = Path.Combine(streamsDir, $"{id}.ndjson");
        File.WriteAllText(streamPath, "");

        lock (_streamLock)
        {
            _consoleStreams[id] = new ConsoleSubState {
                level        = level,
                pattern      = pattern,
                from_assembly = fromAssembly,
                stream_path  = streamPath
            };
        }

        return new UCAFResult {
            success   = true,
            message   = $"Console subscription active. Stream: {streamPath}",
            data_json = JsonUtility.ToJson(new UCAFConsoleSubscription {
                subscription_id = id,
                stream_path     = streamPath,
                level           = level,
                pattern         = pattern,
                from_assembly   = fromAssembly
            })
        };
    }

    static UCAFResult CmdConsoleUnsubscribe(UCAFCommand cmd)
    {
        string id = cmd.GetParam("subscription_id", "");
        if (string.IsNullOrEmpty(id))
            return new UCAFResult { success = false, message = "subscription_id required." };

        lock (_streamLock)
        {
            if (!_consoleStreams.TryGetValue(id, out var sub))
                return new UCAFResult { success = false, message = $"No active subscription '{id}'." };

            _consoleStreams.Remove(id);
            return new UCAFResult {
                success   = true,
                message   = $"Unsubscribed. {sub.entries_written} entries in {sub.stream_path}",
                data_json = $"{{\"entries_written\":{sub.entries_written},\"stream_path\":\"{EscJ(sub.stream_path)}\"}}"
            };
        }
    }

    // ── Internal helpers for PlayMode2 auto-subscribe (FR-182) ───────────────

    internal static string ConsoleAutoSubscribe(string sessionId)
    {
        EnsureStreamHandlerRegistered();
        string id         = $"pm_auto_{sessionId}";
        string streamsDir = Path.Combine(WorkspacePath, "streams");
        Directory.CreateDirectory(streamsDir);
        string streamPath = Path.Combine(streamsDir, $"{id}.ndjson");
        File.WriteAllText(streamPath, "");

        lock (_streamLock)
        {
            _consoleStreams[id] = new ConsoleSubState {
                level       = "All",
                stream_path = streamPath
            };
        }
        return streamPath;
    }

    internal static string ConsoleAutoUnsubscribe(string sessionId)
    {
        string id = $"pm_auto_{sessionId}";
        lock (_streamLock)
        {
            if (_consoleStreams.TryGetValue(id, out var sub))
            {
                _consoleStreams.Remove(id);
                return sub.stream_path;
            }
        }
        return null;
    }
}
