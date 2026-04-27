using UnityEngine;
using UnityEditor;
using System;
using System.Collections.Generic;
using System.IO;

public static partial class UCAF_Listener
{
    // ── JSON Schema validace (v4.2 Phase E, FR-175 to FR-178) ───────────────
    //
    // Schema files: ucaf_workspace/schemas/{command_type}.schema.json
    // Format: UCAFCommandSchema (JsonUtility-serializable subset of JSON Schema)
    // Pre-dispatch validation fires only if a schema file exists for the command type.

    static string SchemasDir => Path.Combine(WorkspacePath, "schemas");

    static UCAFResult CmdGetCommandSchema(UCAFCommand cmd)
    {
        string type = cmd.GetParam("type", "");
        if (string.IsNullOrEmpty(type))
            return new UCAFResult { success = false, message = "type required (e.g. add_package)." };

        Directory.CreateDirectory(SchemasDir);
        string path = Path.Combine(SchemasDir, $"{type}.schema.json");

        if (!File.Exists(path))
            return new UCAFResult {
                success = false,
                message = $"No schema for '{type}'. Create ucaf_workspace/schemas/{type}.schema.json to define one."
            };

        try
        {
            return new UCAFResult {
                success   = true,
                message   = $"Schema for '{type}'",
                data_json = File.ReadAllText(path)
            };
        }
        catch (Exception ex)
        {
            return new UCAFResult { success = false, message = $"Failed to read schema: {ex.Message}" };
        }
    }

    static UCAFResult CmdValidateCommand(UCAFCommand cmd)
    {
        string payload = cmd.GetParam("payload", "");
        if (string.IsNullOrEmpty(payload))
            return new UCAFResult { success = false, message = "payload required (full UCAF command JSON)." };

        UCAFCommand target;
        try { target = JsonUtility.FromJson<UCAFCommand>(payload); }
        catch (Exception ex)
        {
            return new UCAFResult { success = false, message = $"payload is not valid UCAF command JSON: {ex.Message}" };
        }

        var (valid, issues, ctx) = ValidateAgainstSchema(target);
        var result = new UCAFSchemaValidationResult {
            valid         = valid,
            command_type  = target.type ?? "",
            issues_count  = issues.Count,
            issues        = issues,
            error_context = ctx ?? new UCAFErrorContext()
        };

        return new UCAFResult {
            success   = valid,
            message   = valid ? $"'{target.type}' is valid." : $"{issues.Count} issue(s): {string.Join("; ", issues)}",
            data_json = JsonUtility.ToJson(result)
        };
    }

    // Called from Poll (pre-dispatch) and validate_command.
    // Returns (valid=true, empty list, null) when no schema exists → no-op.
    internal static (bool valid, List<string> issues, UCAFErrorContext ctx) ValidateAgainstSchema(UCAFCommand cmd)
    {
        var issues = new List<string>();

        string schemaPath = Path.Combine(SchemasDir, $"{cmd.type}.schema.json");
        if (!File.Exists(schemaPath))
            return (true, issues, null);

        UCAFCommandSchema schema;
        try
        {
            schema = JsonUtility.FromJson<UCAFCommandSchema>(File.ReadAllText(schemaPath));
        }
        catch
        {
            return (true, issues, null); // malformed schema — skip silently
        }

        // Required param check
        if (schema.required_params != null)
        {
            foreach (var req in schema.required_params)
            {
                if (!cmd.HasParam(req) || string.IsNullOrEmpty(cmd.GetParam(req)))
                    issues.Add($"Missing required param: '{req}'");
            }
        }

        // Enum value check
        if (schema.optional_params != null)
        {
            foreach (var def in schema.optional_params)
            {
                if (def.enum_values == null || def.enum_values.Count == 0) continue;
                if (!cmd.HasParam(def.key)) continue;
                string val = cmd.GetParam(def.key);
                if (!def.enum_values.Contains(val))
                    issues.Add($"Param '{def.key}' must be one of [{string.Join(", ", def.enum_values)}], got '{val}'");
            }
        }

        if (issues.Count == 0) return (true, issues, null);

        var ctx = new UCAFErrorContext {
            error_code = "schema_violation",
            hint       = $"See ucaf_workspace/schemas/{cmd.type}.schema.json"
        };
        return (false, issues, ctx);
    }
}
