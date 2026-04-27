using UnityEngine;
using System;
using System.Collections.Generic;

public static partial class UCAF_Listener
{
    // ── batch ───────────────────────────────────────────────────────────
    //
    // Param `payload` is a JSON string of UCAFBatchPayload:
    //   { "stop_on_error": true,
    //     "commands": [ { "type": "...", "params_list": [...] }, ... ] }
    //
    // Async sub-commands (compile_and_wait, take_screenshot) return null in
    // the dispatcher to defer their result. Inside batch this is not
    // supported — they're recorded as a sub-failure and skipped, otherwise
    // the batch result file would never be written.

    static UCAFResult CmdBatch(UCAFCommand cmd)
    {
        string payloadJson = cmd.GetParam("payload", "");
        if (string.IsNullOrEmpty(payloadJson))
            return new UCAFResult { success = false, message = "payload (JSON) required" };

        UCAFBatchPayload payload;
        try
        {
            payload = JsonUtility.FromJson<UCAFBatchPayload>(payloadJson);
        }
        catch (Exception ex)
        {
            return new UCAFResult { success = false, message = $"Bad payload JSON: {ex.Message}" };
        }
        if (payload == null || payload.commands == null || payload.commands.Count == 0)
            return new UCAFResult { success = false, message = "payload.commands is empty" };

        var batchOut = new UCAFBatchResult { total = payload.commands.Count };
        for (int i = 0; i < payload.commands.Count; i++)
        {
            var sub = payload.commands[i];
            if (sub == null)
            {
                batchOut.results.Add(new UCAFBatchSubResult {
                    index = i, type = "<null>", success = false, message = "null sub-command"
                });
                batchOut.failed++;
                if (payload.stop_on_error) { batchOut.stopped_early = true; break; }
                continue;
            }
            if (string.IsNullOrEmpty(sub.id)) sub.id = $"{cmd.id}.{i}";

            UCAFResult r;
            try
            {
                if (sub.type == "batch")
                {
                    r = new UCAFResult { success = false, message = "Nested batch not supported" };
                }
                else
                {
                    r = ExecuteCommand(sub);
                    if (r == null)
                    {
                        r = new UCAFResult {
                            success = false,
                            message = $"Async command '{sub.type}' not supported inside batch"
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                r = new UCAFResult { success = false, message = $"Exception: {ex.Message}" };
            }

            batchOut.results.Add(new UCAFBatchSubResult {
                index = i,
                type = sub.type,
                success = r.success,
                message = r.message,
                data_json = r.data_json,
                screenshot_path = r.screenshot_path
            });
            if (r.success) batchOut.succeeded++; else batchOut.failed++;

            if (!r.success && payload.stop_on_error) { batchOut.stopped_early = true; break; }
        }

        bool allOk = batchOut.failed == 0 && !batchOut.stopped_early;
        return new UCAFResult {
            success = allOk,
            message = $"Batch: {batchOut.succeeded}/{batchOut.total} OK" +
                      (batchOut.stopped_early ? " (stopped early)" : ""),
            data_json = JsonUtility.ToJson(batchOut)
        };
    }
}
