using UnityEngine;
using UnityEditor;
using UnityEngine.AI;
using System;
using System.Collections.Generic;

public static partial class UCAF_Listener
{
    // ── NavMesh bake & query (v4.2 Phase C, FR-138 to FR-141) ────────────
    // navmesh_bake uses the legacy editor bake API. The non-deprecated replacement
    // (NavMeshSurface) requires com.unity.ai.navigation which is not in this project.

    // FR-138: navmesh_bake
    static UCAFResult CmdNavMeshBake(UCAFCommand cmd)
    {
        try
        {
#pragma warning disable CS0618
            UnityEditor.AI.NavMeshBuilder.BuildNavMesh();
#pragma warning restore CS0618
            return new UCAFResult { success = true, message = "NavMesh baked successfully (legacy bake)." };
        }
        catch (Exception ex)
        {
            return new UCAFResult { success = false, message = $"NavMesh bake failed: {ex.Message}" };
        }
    }

    // FR-139: navmesh_query_path
    static UCAFResult CmdNavMeshQueryPath(UCAFCommand cmd)
    {
        string fromStr = cmd.GetParam("from", "");
        string toStr   = cmd.GetParam("to", "");
        if (string.IsNullOrEmpty(fromStr) || string.IsNullOrEmpty(toStr))
            return new UCAFResult { success = false, message = "from and to are required (format: x,y,z)." };

        if (!TryParseVector3(fromStr, out Vector3 from))
            return new UCAFResult { success = false, message = $"Cannot parse from='{fromStr}'. Expected: x,y,z (e.g. 0,0,0)." };
        if (!TryParseVector3(toStr, out Vector3 to))
            return new UCAFResult { success = false, message = $"Cannot parse to='{toStr}'. Expected: x,y,z." };

        int areaMask = int.TryParse(cmd.GetParam("area_mask", "-1"), out int am) ? am : NavMesh.AllAreas;

        var path = new NavMeshPath();
        bool found = NavMesh.CalculatePath(from, to, areaMask, path);

        float length = 0f;
        var corners = new List<string>();
        if (path.corners != null)
        {
            corners.Add(Vec3Str(path.corners[0]));
            for (int i = 1; i < path.corners.Length; i++)
            {
                length += Vector3.Distance(path.corners[i - 1], path.corners[i]);
                corners.Add(Vec3Str(path.corners[i]));
            }
        }

        var result = new UCAFNavMeshPath {
            reachable    = path.status == NavMeshPathStatus.PathComplete,
            status       = path.status.ToString(),
            length       = length,
            corner_count = corners.Count,
            corners      = corners
        };

        return new UCAFResult {
            success   = true,
            message   = $"Path {path.status}: {corners.Count} corners, length={length:F2}",
            data_json = JsonUtility.ToJson(result)
        };
    }

    // FR-140: navmesh_sample_position
    static UCAFResult CmdNavMeshSamplePosition(UCAFCommand cmd)
    {
        string posStr   = cmd.GetParam("position", "");
        if (string.IsNullOrEmpty(posStr))
            return new UCAFResult { success = false, message = "position is required (format: x,y,z)." };

        if (!TryParseVector3(posStr, out Vector3 pos))
            return new UCAFResult { success = false, message = $"Cannot parse position='{posStr}'." };

        float maxDist = float.TryParse(cmd.GetParam("max_distance", "5"), out float md) ? md : 5f;
        int areaMask  = int.TryParse(cmd.GetParam("area_mask", "-1"), out int am) ? am : NavMesh.AllAreas;

        bool hit = NavMesh.SamplePosition(pos, out NavMeshHit navHit, maxDist, areaMask);

        var result = new UCAFNavMeshSample {
            hit      = hit,
            position = hit ? Vec3Str(navHit.position) : "",
            distance = hit ? navHit.distance : -1f
        };

        return new UCAFResult {
            success   = true,
            message   = hit ? $"Walkable position found: {result.position} (dist={navHit.distance:F3})" : "No walkable position found within max_distance.",
            data_json = JsonUtility.ToJson(result)
        };
    }

    // FR-141: add_offmesh_link
    // OffMeshLink is deprecated in Unity 6 but NavMeshLink requires com.unity.ai.navigation
    // which is not in this project. Suppressing CS0618 until the package is added.
    static UCAFResult CmdAddOffMeshLink(UCAFCommand cmd)
    {
        string fromPath = cmd.GetParam("from_obj_path", "");
        string toPath   = cmd.GetParam("to_obj_path", "");
        if (string.IsNullOrEmpty(fromPath) || string.IsNullOrEmpty(toPath))
            return new UCAFResult { success = false, message = "from_obj_path and to_obj_path are required." };

        var fromObj = GameObject.Find(fromPath);
        if (fromObj == null) return new UCAFResult { success = false, message = $"GameObject not found: {fromPath}" };

        var toObj = GameObject.Find(toPath);
        if (toObj == null) return new UCAFResult { success = false, message = $"GameObject not found: {toPath}" };

#pragma warning disable CS0618
        var link = fromObj.GetComponent<OffMeshLink>();
        bool created = false;
        if (link == null)
        {
            link = Undo.AddComponent<OffMeshLink>(fromObj);
            created = true;
        }

        link.startTransform = fromObj.transform;
        link.endTransform   = toObj.transform;
        link.biDirectional  = cmd.GetParam("bidirectional", "true") != "false";
        link.activated      = true;
#pragma warning restore CS0618

        EditorUtility.SetDirty(fromObj);
        return new UCAFResult {
            success = true,
            message = $"OffMeshLink {(created ? "added to" : "updated on")} '{fromPath}' → '{toPath}'."
        };
    }

    // ── Shared Vector3 helpers ─────────────────────────────────────────────

    internal static bool TryParseVector3(string s, out Vector3 result)
    {
        result = Vector3.zero;
        if (string.IsNullOrEmpty(s)) return false;
        var parts = s.Split(',');
        if (parts.Length < 3) return false;
        if (float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float x) &&
            float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float y) &&
            float.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float z))
        {
            result = new Vector3(x, y, z);
            return true;
        }
        return false;
    }

    static string Vec3Str(Vector3 v) =>
        v.x.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) + "," +
        v.y.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) + "," +
        v.z.ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
}
