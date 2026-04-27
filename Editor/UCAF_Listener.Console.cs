using UnityEngine;
using UnityEditor;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

public static partial class UCAF_Listener
{
    static UCAFResult CmdGetConsole(UCAFCommand cmd)
    {
        string sinceStr = cmd.GetParam("since", "");
        string severity = cmd.GetParam("severity", "").ToLower();
        int max = int.TryParse(cmd.GetParam("max", "200"), out int m) ? m : 200;

        DateTime since = DateTime.MinValue;
        if (!string.IsNullOrEmpty(sinceStr))
            DateTime.TryParse(sinceStr, null, DateTimeStyles.RoundtripKind, out since);

        var snapshot = SnapshotLogBuffer();
        var result = new UCAFLogQueryResult { total_in_buffer = snapshot.Count };

        foreach (var e in snapshot)
        {
            if (since != DateTime.MinValue)
            {
                if (DateTime.TryParse(e.timestamp, null, DateTimeStyles.RoundtripKind, out DateTime t))
                    if (t < since) continue;
            }
            if (severity == "error"   && !(e.type == "Error" || e.type == "Exception" || e.type == "Assert")) continue;
            if (severity == "warning" && e.type != "Warning") continue;
            if (severity == "log"     && e.type != "Log")     continue;
            result.entries.Add(e);
        }

        if (result.entries.Count > max)
            result.entries = result.entries.GetRange(result.entries.Count - max, max);

        return new UCAFResult {
            success = true,
            message = $"{result.entries.Count} entries (total in buffer: {result.total_in_buffer})",
            data_json = JsonUtility.ToJson(result)
        };
    }

    static UCAFResult CmdClearConsole(UCAFCommand cmd)
    {
        ClearLogBuffer();

        try { if (File.Exists(LogFilePath)) File.WriteAllText(LogFilePath, ""); }
        catch { }

        // also clear Unity's built-in console
        try
        {
            var type = Type.GetType("UnityEditor.LogEntries, UnityEditor.dll")
                       ?? Type.GetType("UnityEditor.LogEntries, UnityEditor");
            var clear = type?.GetMethod("Clear",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            clear?.Invoke(null, null);
        }
        catch { }

        return new UCAFResult { success = true, message = "Console cleared." };
    }
}
