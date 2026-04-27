using UnityEngine;
using UnityEditor;
using System;
using System.Collections.Generic;
using System.Globalization;

public static partial class UCAF_Listener
{
    // ── Input Simulation (v4.2 Phase B, FR-115 to FR-118) ─────────────────
    //
    // Requires com.unity.inputsystem package (add via add_package command).
    // All input commands require active Play Mode.
    // Compiled conditionally: ENABLE_INPUT_SYSTEM is defined by Unity when
    // the Input System package is installed and active.

    static UCAFResult RequirePlayMode()
    {
        if (!EditorApplication.isPlaying)
            return new UCAFResult { success = false, message = "Input simulation requires Play Mode. Call playmode_enter first." };
        return null;
    }

#if ENABLE_INPUT_SYSTEM

    static UCAFResult CmdInputPressKey(UCAFCommand cmd)
    {
        var err = RequirePlayMode(); if (err != null) return err;

        string keyStr = cmd.GetParam("key", "");
        if (string.IsNullOrEmpty(keyStr))
            return new UCAFResult { success = false, message = "key required (e.g. Space, W, LeftShift)" };

        int durationMs = int.TryParse(cmd.GetParam("duration_ms", "100"), out int d) ? d : 100;

        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (kb == null)
            return new UCAFResult { success = false, message = "No Keyboard device found in Input System." };

        if (!Enum.TryParse<UnityEngine.InputSystem.Key>(keyStr, true, out var key))
            return new UCAFResult { success = false, message = $"Unknown key '{keyStr}'. Valid: {string.Join(", ", Enum.GetNames(typeof(UnityEngine.InputSystem.Key)))}" };

        // Press
        UnityEngine.InputSystem.InputSystem.QueueStateEvent(kb,
            new UnityEngine.InputSystem.LowLevel.KeyboardState(key));

        // Release after duration
        double releaseAt = EditorApplication.timeSinceStartup + durationMs / 1000.0;
        EditorApplication.CallbackFunction release = null;
        release = () => {
            if (EditorApplication.timeSinceStartup >= releaseAt)
            {
                UnityEngine.InputSystem.InputSystem.QueueStateEvent(kb,
                    new UnityEngine.InputSystem.LowLevel.KeyboardState());
                EditorApplication.update -= release;
            }
        };
        EditorApplication.update += release;

        return new UCAFResult { success = true, message = $"Key press queued: {keyStr} for {durationMs}ms" };
    }

    static UCAFResult CmdInputMoveMouse(UCAFCommand cmd)
    {
        var err = RequirePlayMode(); if (err != null) return err;

        var mouse = UnityEngine.InputSystem.Mouse.current;
        if (mouse == null)
            return new UCAFResult { success = false, message = "No Mouse device found in Input System." };

        string toStr    = cmd.GetParam("to", "");
        string deltaStr = cmd.GetParam("delta", "");
        if (string.IsNullOrEmpty(toStr) && string.IsNullOrEmpty(deltaStr))
            return new UCAFResult { success = false, message = "Either to=x,y (absolute) or delta=dx,dy required" };

        Vector2 current = mouse.position.ReadValue();
        Vector2 target;

        if (!string.IsNullOrEmpty(toStr))
        {
            var p = toStr.Split(',');
            if (p.Length < 2) return new UCAFResult { success = false, message = "to must be x,y" };
            target = new Vector2(
                float.Parse(p[0].Trim(), CultureInfo.InvariantCulture),
                float.Parse(p[1].Trim(), CultureInfo.InvariantCulture));
        }
        else
        {
            var p = deltaStr.Split(',');
            if (p.Length < 2) return new UCAFResult { success = false, message = "delta must be dx,dy" };
            target = current + new Vector2(
                float.Parse(p[0].Trim(), CultureInfo.InvariantCulture),
                float.Parse(p[1].Trim(), CultureInfo.InvariantCulture));
        }

        int durationMs = int.TryParse(cmd.GetParam("duration_ms", "200"), out int dur) ? dur : 200;
        int steps      = Math.Max(1, durationMs / 16);
        int step       = 0;

        EditorApplication.CallbackFunction move = null;
        move = () => {
            if (!EditorApplication.isPlaying || step >= steps) { EditorApplication.update -= move; return; }
            float t   = (float)(step + 1) / steps;
            Vector2 p = Vector2.Lerp(current, target, t);
            UnityEngine.InputSystem.InputSystem.QueueStateEvent(mouse,
                new UnityEngine.InputSystem.LowLevel.MouseState { position = p });
            step++;
        };
        EditorApplication.update += move;

        return new UCAFResult { success = true, message = $"Mouse move queued to ({target.x},{target.y}) over {durationMs}ms" };
    }

    static UCAFResult CmdInputGamepadStick(UCAFCommand cmd)
    {
        var err = RequirePlayMode(); if (err != null) return err;

        var gamepad = UnityEngine.InputSystem.Gamepad.current;
        if (gamepad == null)
            return new UCAFResult { success = false, message = "No Gamepad device found. Add one via InputSystem.AddDevice<Gamepad>() in test setup or connect a physical gamepad." };

        string stick     = cmd.GetParam("stick", "left").ToLowerInvariant();
        string valueStr  = cmd.GetParam("value", "0,0");
        int    durationMs = int.TryParse(cmd.GetParam("duration_ms", "200"), out int d) ? d : 200;

        var parts = valueStr.Split(',');
        if (parts.Length < 2) return new UCAFResult { success = false, message = "value must be x,y in range -1..1" };
        var v = new Vector2(
            float.Parse(parts[0].Trim(), CultureInfo.InvariantCulture),
            float.Parse(parts[1].Trim(), CultureInfo.InvariantCulture));

        var state = new UnityEngine.InputSystem.LowLevel.GamepadState();
        if (stick == "right") state.rightStick = v;
        else                  state.leftStick  = v;
        UnityEngine.InputSystem.InputSystem.QueueStateEvent(gamepad, state);

        double resetAt = EditorApplication.timeSinceStartup + durationMs / 1000.0;
        EditorApplication.CallbackFunction reset = null;
        reset = () => {
            if (EditorApplication.timeSinceStartup >= resetAt)
            {
                var zero = new UnityEngine.InputSystem.LowLevel.GamepadState();
                UnityEngine.InputSystem.InputSystem.QueueStateEvent(gamepad, zero);
                EditorApplication.update -= reset;
            }
        };
        EditorApplication.update += reset;

        return new UCAFResult { success = true, message = $"Gamepad {stick} stick set to ({v.x},{v.y}) for {durationMs}ms" };
    }

    static UCAFResult CmdInputSequence(UCAFCommand cmd)
    {
        var err = RequirePlayMode(); if (err != null) return err;

        string seqJson = cmd.GetParam("sequence", "");
        if (string.IsNullOrEmpty(seqJson))
            return new UCAFResult { success = false, message = "sequence required: JSON array [{\"type\":\"press_key\",\"key\":\"Space\",\"delay_ms\":0,\"duration_ms\":100},...]" };

        var events = ParseInputSequence(seqJson);
        if (events == null || events.Count == 0)
            return new UCAFResult { success = false, message = "Failed to parse sequence JSON or empty sequence" };

        double baseTime = EditorApplication.timeSinceStartup;
        int idx = 0;
        foreach (var evt in events)
        {
            double fireAt   = baseTime + evt.delay_ms / 1000.0;
            var    captured = evt;
            int    ci       = idx++;

            EditorApplication.CallbackFunction cb = null;
            cb = () => {
                if (EditorApplication.timeSinceStartup < fireAt) return;
                EditorApplication.update -= cb;

                var subCmd = new UCAFCommand { id = $"{cmd.id}_s{ci}", type = $"input_{captured.type}" };
                if (!string.IsNullOrEmpty(captured.key))
                    subCmd.params_list.Add(new UCAFParam { key = "key", value = captured.key });
                if (!string.IsNullOrEmpty(captured.value))
                    subCmd.params_list.Add(new UCAFParam { key = "value", value = captured.value });
                if (!string.IsNullOrEmpty(captured.stick))
                    subCmd.params_list.Add(new UCAFParam { key = "stick", value = captured.stick });
                if (!string.IsNullOrEmpty(captured.to))
                    subCmd.params_list.Add(new UCAFParam { key = "to", value = captured.to });
                subCmd.params_list.Add(new UCAFParam { key = "duration_ms", value = captured.duration_ms.ToString() });
                ExecuteCommand(subCmd); // fire-and-forget
            };
            EditorApplication.update += cb;
        }

        return new UCAFResult { success = true, message = $"Input sequence queued: {events.Count} event(s)" };
    }

    class InputSeqEvent
    {
        public string type;
        public string key;
        public string value;
        public string stick;
        public string to;
        public int    delay_ms;
        public int    duration_ms;
    }

    static List<InputSeqEvent> ParseInputSequence(string json)
    {
        var list = new List<InputSeqEvent>();
        json = json.Trim();
        if (!json.StartsWith("[")) return list;
        int depth = 0, start = -1;
        for (int i = 0; i < json.Length; i++)
        {
            if (json[i] == '{') { if (depth == 0) start = i; depth++; }
            else if (json[i] == '}' && --depth == 0 && start >= 0)
            {
                string obj = json.Substring(start, i - start + 1);
                var e = new InputSeqEvent {
                    type        = ExtractStr(obj, "type"),
                    key         = ExtractStr(obj, "key"),
                    value       = ExtractStr(obj, "value"),
                    stick       = ExtractStr(obj, "stick"),
                    to          = ExtractStr(obj, "to"),
                    delay_ms    = ExtractInt(obj, "delay_ms"),
                    duration_ms = ExtractInt(obj, "duration_ms", 100)
                };
                if (!string.IsNullOrEmpty(e.type)) list.Add(e);
                start = -1;
            }
        }
        return list;
    }

    static string ExtractStr(string json, string key)
    {
        string k = $"\"{key}\":"; int idx = json.IndexOf(k);
        if (idx < 0) { k = $"\"{key}\": "; idx = json.IndexOf(k); }
        if (idx < 0) return "";
        int s = json.IndexOf('"', idx + k.Length); if (s < 0) return "";
        int e = json.IndexOf('"', s + 1); return e < 0 ? "" : json.Substring(s + 1, e - s - 1);
    }

    static int ExtractInt(string json, string key, int def = 0)
    {
        string k = $"\"{key}\":"; int idx = json.IndexOf(k);
        if (idx < 0) return def;
        int ns = idx + k.Length;
        while (ns < json.Length && (json[ns] == ' ' || json[ns] == '"')) ns++;
        int ne = ns;
        while (ne < json.Length && (char.IsDigit(json[ne]) || json[ne] == '-')) ne++;
        return int.TryParse(json.Substring(ns, ne - ns), out int v) ? v : def;
    }

#else

    static UCAFResult CmdInputPressKey(UCAFCommand cmd)    => InputSystemMissing();
    static UCAFResult CmdInputMoveMouse(UCAFCommand cmd)   => InputSystemMissing();
    static UCAFResult CmdInputGamepadStick(UCAFCommand cmd)=> InputSystemMissing();
    static UCAFResult CmdInputSequence(UCAFCommand cmd)    => InputSystemMissing();

    static UCAFResult InputSystemMissing() => new UCAFResult {
        success = false,
        message = "Input System package (com.unity.inputsystem) is not installed or not set as active input handler. " +
                  "Fix: add_package name=com.unity.inputsystem, then Project Settings > Player > Active Input Handling = 'Input System Package' or 'Both'."
    };

#endif
}
