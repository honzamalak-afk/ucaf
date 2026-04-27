using UnityEngine;
using UnityEditor;
using System;
using System.Collections.Generic;
using System.Globalization;

public static partial class UCAF_Listener
{
    // ── Lightmap bake (v4.2 Phase C, FR-165 to FR-166) ───────────────────

    internal const string SS_PendingLightmapId    = "UCAF_PendingLightmapId";
    internal const string SS_PendingLightmapStart = "UCAF_PendingLightmapStart";

    // Called from static constructor: recovers if domain reload happened during bake.
    internal static void CheckPendingLightmapOnStartup()
    {
        string id = SessionState.GetString(SS_PendingLightmapId, "");
        if (string.IsNullOrEmpty(id)) return;

        if (Lightmapping.isRunning)
        {
            Lightmapping.bakeCompleted -= OnLightmapBakeCompleted;
            Lightmapping.bakeCompleted += OnLightmapBakeCompleted;
            return;
        }

        // Bake was cancelled by domain reload.
        SessionState.EraseString(SS_PendingLightmapId);
        SessionState.EraseString(SS_PendingLightmapStart);
        WriteResult(DonePath, id, new UCAFResult {
            success = false,
            message = "Lightmap bake was interrupted by a domain reload. Re-run lightmap_bake."
        });
    }

    // FR-165: lightmap_bake
    static UCAFResult CmdLightmapBake(UCAFCommand cmd)
    {
        if (!string.IsNullOrEmpty(SessionState.GetString(SS_PendingLightmapId, "")))
            return new UCAFResult { success = false, message = "A lightmap bake is already in progress." };

        if (Lightmapping.isRunning)
            return new UCAFResult { success = false, message = "Lightmapping is already running (started outside UCAF)." };

        SessionState.SetString(SS_PendingLightmapId,    cmd.id);
        SessionState.SetString(SS_PendingLightmapStart, DateTime.UtcNow.ToString("o"));

        Lightmapping.bakeCompleted -= OnLightmapBakeCompleted;
        Lightmapping.bakeCompleted += OnLightmapBakeCompleted;

        bool started = Lightmapping.BakeAsync();
        if (!started)
        {
            SessionState.EraseString(SS_PendingLightmapId);
            SessionState.EraseString(SS_PendingLightmapStart);
            Lightmapping.bakeCompleted -= OnLightmapBakeCompleted;
            return new UCAFResult {
                success = false,
                message = "Lightmapping.BakeAsync() returned false. Ensure the scene has baked lights configured and is saved."
            };
        }

        return null; // deferred — OnLightmapBakeCompleted writes the result
    }

    static void OnLightmapBakeCompleted()
    {
        Lightmapping.bakeCompleted -= OnLightmapBakeCompleted;

        string cmdId = SessionState.GetString(SS_PendingLightmapId, "");
        if (string.IsNullOrEmpty(cmdId)) return;

        string startStr = SessionState.GetString(SS_PendingLightmapStart, "");
        SessionState.EraseString(SS_PendingLightmapId);
        SessionState.EraseString(SS_PendingLightmapStart);

        float elapsed = -1f;
        if (DateTime.TryParse(startStr, null, DateTimeStyles.RoundtripKind, out DateTime startTime))
            elapsed = (float)(DateTime.UtcNow - startTime).TotalSeconds;

        int atlasCount = LightmapSettings.lightmaps != null ? LightmapSettings.lightmaps.Length : 0;

        WriteResult(DonePath, cmdId, new UCAFResult {
            success   = true,
            message   = $"Lightmap bake completed: {atlasCount} atlas(es) in {elapsed:F1}s.",
            data_json = JsonUtility.ToJson(new UCAFLightmapResult {
                success    = true,
                duration_s = elapsed,
                atlas_count = atlasCount,
                errors     = new List<string>()
            })
        });
    }

    // FR-166: lightmap_clear
    static UCAFResult CmdLightmapClear(UCAFCommand cmd)
    {
        Lightmapping.Clear();
        return new UCAFResult { success = true, message = "Lightmap data cleared for current scene." };
    }
}
