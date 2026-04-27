using UnityEngine;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

public static partial class UCAF_Listener
{
    // ── Unity Test Runner (v4.2 Phase B, FR-111 to FR-114) ────────────────

    internal const string SS_PendingTestId = "UCAF_PendingTestId";

    static readonly List<UCAFTestResultEntry> _testResults = new List<UCAFTestResultEntry>();
    static TestRunnerApi _testRunnerApi;
    static UcafTestCallbacks _testCallbacks;
    static float _testRunStart;

    // list_tests deferred state (callback may fire async)
    static string _testListCmdId;
    static bool   _testListCallbackFired;
    static UCAFTestList _testListPending;

    // ── run_tests ──────────────────────────────────────────────────────────

    static UCAFResult CmdRunTests(UCAFCommand cmd)
    {
        if (!string.IsNullOrEmpty(SessionState.GetString(SS_PendingTestId, "")))
            return new UCAFResult { success = false, message = "Another run_tests is already in progress." };

        string modeStr  = cmd.GetParam("mode", "editmode").ToLowerInvariant();
        string filter   = cmd.GetParam("filter", "");
        int timeout     = int.TryParse(cmd.GetParam("timeout_seconds", "120"), out int t) ? t : 120;

        var testMode = modeStr switch {
            "playmode" => TestMode.PlayMode,
            "all"      => TestMode.EditMode | TestMode.PlayMode,
            _          => TestMode.EditMode
        };

        _testResults.Clear();
        _testRunStart = (float)EditorApplication.timeSinceStartup;

        SessionState.SetString(SS_PendingTestId, cmd.id);

        if (_testRunnerApi == null)  _testRunnerApi  = ScriptableObject.CreateInstance<TestRunnerApi>();
        if (_testCallbacks == null)  _testCallbacks  = new UcafTestCallbacks();

        _testRunnerApi.RegisterCallbacks(_testCallbacks);

        var execSettings = new ExecutionSettings(
            new Filter {
                testMode  = testMode,
                testNames = string.IsNullOrEmpty(filter) ? null : new[] { filter }
            }
        );

        _testRunnerApi.Execute(execSettings);
        return null; // deferred — UcafTestCallbacks.RunFinished will write the result
    }

    // ── list_tests ─────────────────────────────────────────────────────────

    static UCAFResult CmdListTests(UCAFCommand cmd)
    {
        string modeStr = cmd.GetParam("mode", "all").ToLowerInvariant();
        var testMode = modeStr switch {
            "editmode" => TestMode.EditMode,
            "playmode" => TestMode.PlayMode,
            _          => TestMode.EditMode | TestMode.PlayMode
        };

        if (_testRunnerApi == null) _testRunnerApi = ScriptableObject.CreateInstance<TestRunnerApi>();

        _testListCallbackFired = false;
        _testListPending = new UCAFTestList();

        _testRunnerApi.RetrieveTestList(testMode, root => {
            CollectTestInfos(root, _testListPending.tests, testMode);
            _testListPending.total = _testListPending.tests.Count;
            _testListCallbackFired = true;
        });

        // Unity fires this callback synchronously in most cases (Edit Mode, cached list).
        // If it fires synchronously we can return immediately; otherwise defer.
        if (_testListCallbackFired)
        {
            return new UCAFResult {
                success   = true,
                message   = $"{_testListPending.total} tests",
                data_json = JsonUtility.ToJson(_testListPending)
            };
        }

        _testListCmdId = cmd.id;
        return null; // deferred — CheckPendingTestList will write the result
    }

    internal static void CheckPendingTestList()
    {
        if (string.IsNullOrEmpty(_testListCmdId)) return;
        if (!_testListCallbackFired) return;

        string id = _testListCmdId;
        _testListCmdId = null;

        WriteResult(DonePath, id, new UCAFResult {
            success   = true,
            message   = $"{_testListPending?.total ?? 0} tests",
            data_json = _testListPending != null ? JsonUtility.ToJson(_testListPending) : "{}"
        });
    }

    static void CollectTestInfos(ITestAdaptor node, List<UCAFTestInfo> list, TestMode mode)
    {
        if (node == null) return;
        if (!node.IsSuite)
        {
            list.Add(new UCAFTestInfo {
                test_name = node.Name,
                full_name = node.FullName,
                mode      = mode.ToString().ToLowerInvariant(),
                assembly  = node.TypeInfo?.Assembly?.GetName().Name ?? ""
            });
        }
        if (node.Children != null)
            foreach (var child in node.Children)
                CollectTestInfos(child, list, mode);
    }

    // ── create_test ────────────────────────────────────────────────────────

    static UCAFResult CmdCreateTest(UCAFCommand cmd)
    {
        string className  = cmd.GetParam("class_name", "NewTests");
        string modeStr    = cmd.GetParam("mode", "editmode").ToLowerInvariant();
        string assembly   = cmd.GetParam("assembly_name", "Tests");
        string methodsRaw = cmd.GetParam("methods", "SampleTest");

        bool isPlayMode = modeStr == "playmode";
        string folder   = Path.Combine(Application.dataPath, "Tests");
        Directory.CreateDirectory(folder);

        string[] methods  = methodsRaw.Split(',');
        var sb = new StringBuilder();
        sb.AppendLine("using NUnit.Framework;");
        if (isPlayMode) sb.AppendLine("using System.Collections;");
        if (isPlayMode) sb.AppendLine("using UnityEngine.TestTools;");
        sb.AppendLine();
        sb.AppendLine($"public class {className}");
        sb.AppendLine("{");
        foreach (string m in methods)
        {
            string methodName = m.Trim();
            if (string.IsNullOrEmpty(methodName)) continue;
            if (isPlayMode)
            {
                sb.AppendLine($"    [UnityTest]");
                sb.AppendLine($"    public IEnumerator {methodName}()");
                sb.AppendLine("    {");
                sb.AppendLine("        yield return null;");
                sb.AppendLine("        Assert.Pass();");
                sb.AppendLine("    }");
            }
            else
            {
                sb.AppendLine($"    [Test]");
                sb.AppendLine($"    public void {methodName}()");
                sb.AppendLine("    {");
                sb.AppendLine("        Assert.Pass();");
                sb.AppendLine("    }");
            }
            sb.AppendLine();
        }
        sb.AppendLine("}");

        string filePath = Path.Combine(folder, $"{className}.cs");
        File.WriteAllText(filePath, sb.ToString());
        AssetDatabase.Refresh();

        return new UCAFResult {
            success = true,
            message = $"Test file created: Assets/Tests/{className}.cs"
        };
    }

    // ── register_test_assembly ─────────────────────────────────────────────

    static UCAFResult CmdRegisterTestAssembly(UCAFCommand cmd)
    {
        string folder  = Path.Combine(Application.dataPath, "Tests");
        Directory.CreateDirectory(folder);
        string asmPath = Path.Combine(folder, "Tests.asmdef");

        if (File.Exists(asmPath))
            return new UCAFResult { success = true, message = "Tests.asmdef already exists", data_json = "{\"skipped\":true}" };

        string asmDef = "{\n" +
                        "    \"name\": \"Tests\",\n" +
                        "    \"references\": [],\n" +
                        "    \"includePlatforms\": [],\n" +
                        "    \"excludePlatforms\": [],\n" +
                        "    \"allowUnsafeCode\": false,\n" +
                        "    \"overrideReferences\": true,\n" +
                        "    \"precompiledReferences\": [\n" +
                        "        \"nunit.framework.dll\"\n" +
                        "    ],\n" +
                        "    \"autoReferenced\": false,\n" +
                        "    \"defineConstraints\": [],\n" +
                        "    \"versionDefines\": [],\n" +
                        "    \"noEngineReferences\": false\n" +
                        "}\n";

        File.WriteAllText(asmPath, asmDef);
        AssetDatabase.Refresh();
        return new UCAFResult { success = true, message = "Tests.asmdef created at Assets/Tests/" };
    }

    // ── Async checker (registered in constructor) ───────────────────────────

    internal static void CheckPendingTestRun()
    {
        // Actual completion is handled by UcafTestCallbacks.RunFinished
        // This checker handles timeout only
        string id = SessionState.GetString(SS_PendingTestId, "");
        if (string.IsNullOrEmpty(id)) return;

        // Default: 120s timeout
        float elapsed = (float)EditorApplication.timeSinceStartup - _testRunStart;
        if (elapsed > 180f)
        {
            FinishTestRun(id, timedOut: true, elapsed);
        }
    }

    internal static void FinishTestRun(string id, bool timedOut, float elapsed)
    {
        if (_testCallbacks != null && _testRunnerApi != null)
            _testRunnerApi.UnregisterCallbacks(_testCallbacks);

        SessionState.EraseString(SS_PendingTestId);

        int passed = 0, failed = 0, skipped = 0;
        foreach (var r in _testResults)
        {
            if (r.result_type == "Passed")       passed++;
            else if (r.result_type == "Skipped") skipped++;
            else                                  failed++;
        }

        var payload = new UCAFTestRunResult {
            total      = _testResults.Count,
            passed     = passed,
            failed     = failed,
            skipped    = skipped,
            duration_s = elapsed,
            tests      = new List<UCAFTestResultEntry>(_testResults)
        };

        string msg = timedOut
            ? $"Test run timed out after {elapsed:F0}s. Completed: {_testResults.Count}"
            : $"Tests: {passed} passed, {failed} failed, {skipped} skipped ({elapsed:F1}s)";

        WriteResult(DonePath, id, new UCAFResult {
            success   = !timedOut && failed == 0,
            message   = msg,
            data_json = JsonUtility.ToJson(payload)
        });

        _testResults.Clear();
    }

    // ── Callbacks inner class ──────────────────────────────────────────────

    class UcafTestCallbacks : ICallbacks
    {
        public void RunStarted(ITestAdaptor testsToRun) { }

        public void RunFinished(ITestResultAdaptor result)
        {
            string id = SessionState.GetString(SS_PendingTestId, "");
            if (string.IsNullOrEmpty(id)) return;
            float elapsed = (float)EditorApplication.timeSinceStartup - _testRunStart;
            FinishTestRun(id, timedOut: false, elapsed);
        }

        public void TestStarted(ITestAdaptor test) { }

        public void TestFinished(ITestResultAdaptor result)
        {
            if (result.Test.IsSuite) return;
            _testResults.Add(new UCAFTestResultEntry {
                test_name  = result.Test.Name,
                full_name  = result.Test.FullName,
                result_type = result.TestStatus.ToString(),
                duration_s = (float)result.Duration,
                message    = result.Message ?? "",
                stack_trace = result.StackTrace ?? ""
            });
        }
    }
}
