using UnityEngine;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;

public static partial class UCAF_Listener
{
    // ── Package Manager API (v4.2 Phase A, FR-147 to FR-150; FR-192 search) ──
    //
    // PM requests are async. We hold the active request + command ID in statics
    // and poll them in CheckPendingPackageManager (registered in constructor).
    // Only one PM operation can run at a time (UPM is single-threaded).

    static ListRequest   _pmListReq;
    static SearchRequest _pmSearchReq;
    static AddRequest    _pmAddReq;
    static RemoveRequest _pmRemoveReq;
    static string        _pmCmdId;

    // SessionState backup — survives domain reload triggered by package install
    internal const string SS_PmPendingCmdId = "UCAF_PM_PendingCmdId";
    internal const string SS_PmPendingPkgId = "UCAF_PM_PendingPkgId";

    static void ClearPmState()
    {
        _pmListReq   = null;
        _pmSearchReq = null;
        _pmAddReq    = null;
        _pmRemoveReq = null;
        _pmCmdId     = null;
        SessionState.EraseString(SS_PmPendingCmdId);
        SessionState.EraseString(SS_PmPendingPkgId);
    }

    // Called from UCAF_Listener constructor — resolves any add/update that triggered a domain reload
    internal static void CheckPendingPackageManagerOnStartup()
    {
        string id = SessionState.GetString(SS_PmPendingCmdId, "");
        if (string.IsNullOrEmpty(id)) return;

        string pkgId = SessionState.GetString(SS_PmPendingPkgId, "");
        SessionState.EraseString(SS_PmPendingCmdId);
        SessionState.EraseString(SS_PmPendingPkgId);

        // Domain reload means Unity recompiled after the package installed — treat as success
        WriteResult(DonePath, id, new UCAFResult {
            success = true,
            message = $"Package operation completed (domain reload occurred): {pkgId}"
        });
    }

    static bool PmBusy() => !string.IsNullOrEmpty(_pmCmdId);

    // ── Commands ────────────────────────────────────────────────────────────

    static UCAFResult CmdListPackages(UCAFCommand cmd)
    {
        if (PmBusy()) return new UCAFResult { success = false, message = "Another Package Manager operation is already in progress." };
        _pmCmdId   = cmd.id;
        _pmListReq = Client.List(offlineMode: false);
        return null; // deferred
    }

    static UCAFResult CmdSearchPackages(UCAFCommand cmd)
    {
        string query = cmd.GetParam("query", "");
        if (string.IsNullOrEmpty(query))
            return new UCAFResult { success = false, message = "query required (e.g. \"cinemachine\" or \"com.unity.cinemachine\")" };
        if (PmBusy()) return new UCAFResult { success = false, message = "Another Package Manager operation is already in progress." };

        _pmCmdId     = cmd.id;
        _pmSearchReq = Client.Search(query);
        return null; // deferred
    }

    static UCAFResult CmdAddPackage(UCAFCommand cmd)
    {
        string name = cmd.GetParam("name", "");
        if (string.IsNullOrEmpty(name))
            return new UCAFResult { success = false, message = "name required (e.g. com.unity.inputsystem or com.unity.inputsystem@1.7.0)" };
        if (PmBusy()) return new UCAFResult { success = false, message = "Another Package Manager operation is already in progress." };

        string version    = cmd.GetParam("version", "");
        string identifier = string.IsNullOrEmpty(version) ? name : $"{name}@{version}";
        SessionState.SetString(SS_PmPendingCmdId, cmd.id);
        SessionState.SetString(SS_PmPendingPkgId, identifier);
        _pmCmdId  = cmd.id;
        _pmAddReq = Client.Add(identifier);
        return null;
    }

    static UCAFResult CmdRemovePackage(UCAFCommand cmd)
    {
        string name = cmd.GetParam("name", "");
        if (string.IsNullOrEmpty(name))
            return new UCAFResult { success = false, message = "name required (e.g. com.unity.inputsystem)" };
        if (PmBusy()) return new UCAFResult { success = false, message = "Another Package Manager operation is already in progress." };

        _pmCmdId     = cmd.id;
        _pmRemoveReq = Client.Remove(name);
        return null;
    }

    static UCAFResult CmdUpdatePackage(UCAFCommand cmd)
    {
        string name = cmd.GetParam("name", "");
        if (string.IsNullOrEmpty(name))
            return new UCAFResult { success = false, message = "name required" };
        if (PmBusy()) return new UCAFResult { success = false, message = "Another Package Manager operation is already in progress." };

        // "update" is the same as "add with a newer version"
        string version    = cmd.GetParam("version", "latest");
        string identifier = version == "latest" ? name : $"{name}@{version}";
        SessionState.SetString(SS_PmPendingCmdId, cmd.id);
        SessionState.SetString(SS_PmPendingPkgId, identifier);
        _pmCmdId  = cmd.id;
        _pmAddReq = Client.Add(identifier);
        return null;
    }

    // ── Async checker (registered in constructor) ───────────────────────────

    internal static void CheckPendingPackageManager()
    {
        if (!PmBusy()) return;

        if (_pmListReq != null && _pmListReq.IsCompleted)
        {
            var req = _pmListReq; string id = _pmCmdId; ClearPmState();
            if (req.Status == StatusCode.Success)
            {
                var list = new UCAFPackageList();
                foreach (var pkg in req.Result)
                    list.packages.Add(new UCAFPackageInfo {
                        name        = pkg.name,
                        version     = pkg.version,
                        source      = pkg.source.ToString(),
                        description = pkg.description
                    });
                list.total = list.packages.Count;
                WriteResult(DonePath, id, new UCAFResult {
                    success   = true,
                    message   = $"{list.total} packages",
                    data_json = JsonUtility.ToJson(list)
                });
            }
            else
            {
                WriteResult(DonePath, id, new UCAFResult {
                    success = false,
                    message = $"Package list error: {req.Error?.message ?? "unknown"}"
                });
            }
            return;
        }

        if (_pmSearchReq != null && _pmSearchReq.IsCompleted)
        {
            var req = _pmSearchReq; string id = _pmCmdId; ClearPmState();
            if (req.Status == StatusCode.Success)
            {
                var list = new UCAFPackageList();
                foreach (var pkg in req.Result)
                    list.packages.Add(new UCAFPackageInfo {
                        name        = pkg.name,
                        displayName = pkg.displayName,
                        version     = pkg.version,
                        source      = pkg.source.ToString(),
                        description = pkg.description,
                        category    = pkg.category
                    });
                list.total = list.packages.Count;
                WriteResult(DonePath, id, new UCAFResult {
                    success   = true,
                    message   = $"{list.total} results",
                    data_json = JsonUtility.ToJson(list)
                });
            }
            else
            {
                WriteResult(DonePath, id, new UCAFResult {
                    success = false,
                    message = $"Package search error: {req.Error?.message ?? "unknown"}"
                });
            }
            return;
        }

        if (_pmAddReq != null && _pmAddReq.IsCompleted)
        {
            var req = _pmAddReq; string id = _pmCmdId; ClearPmState();
            if (req.Status == StatusCode.Success)
            {
                var pkg  = req.Result;
                var info = new UCAFPackageInfo { name = pkg.name, version = pkg.version, source = pkg.source.ToString() };
                WriteResult(DonePath, id, new UCAFResult {
                    success   = true,
                    message   = $"Package installed: {pkg.name}@{pkg.version}",
                    data_json = JsonUtility.ToJson(info)
                });
            }
            else
            {
                WriteResult(DonePath, id, new UCAFResult {
                    success = false,
                    message = $"Package add/update error: {req.Error?.message ?? "unknown"}"
                });
            }
            return;
        }

        if (_pmRemoveReq != null && _pmRemoveReq.IsCompleted)
        {
            var req = _pmRemoveReq; string id = _pmCmdId; ClearPmState();
            if (req.Status == StatusCode.Success)
                WriteResult(DonePath, id, new UCAFResult { success = true, message = $"Package removed: {req.PackageIdOrName}" });
            else
                WriteResult(DonePath, id, new UCAFResult { success = false, message = $"Package remove error: {req.Error?.message ?? "unknown"}" });
        }
    }
}
