using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace UnityAgentBridge
{
    public static partial class UnityCommands
    {
        private static TestRunnerResultStore _testResultStore = new TestRunnerResultStore();

        [BridgeRoute("POST", "/tests/run", Category = "tests", Description = "Start EditMode or PlayMode tests (non-blocking, poll /tests/results)",
            TimeoutDefault = 10000, TimeoutMin = 1000, TimeoutMax = 30000)]
        public static string RunTests(string jsonData)
        {
            try
            {
                var parsed = MiniJSON.Json.Deserialize(jsonData) as Dictionary<string, object>;
                if (parsed == null)
                    return JsonError("Invalid JSON body");

                var testModeStr = parsed.ContainsKey("testMode") ? parsed["testMode"] as string : "EditMode";
                var testMode = string.Equals(testModeStr, "PlayMode", StringComparison.OrdinalIgnoreCase)
                    ? TestMode.PlayMode
                    : TestMode.EditMode;

                var filter = new Filter
                {
                    testMode = testMode
                };

                if (parsed.ContainsKey("assemblyName") && parsed["assemblyName"] is string asm && !string.IsNullOrWhiteSpace(asm))
                    filter.assemblyNames = new[] { asm };
                if (parsed.ContainsKey("className") && parsed["className"] is string cls && !string.IsNullOrWhiteSpace(cls))
                    filter.groupNames = new[] { cls };
                if (parsed.ContainsKey("methodName") && parsed["methodName"] is string mtd && !string.IsNullOrWhiteSpace(mtd))
                    filter.testNames = new[] { mtd };
                if (parsed.ContainsKey("categoryName") && parsed["categoryName"] is string cat && !string.IsNullOrWhiteSpace(cat))
                    filter.categoryNames = new[] { cat };

                _testResultStore.Reset();

                // Auto-save dirty scenes to prevent "Scene(s) Have Been Modified" modal dialog
                EditorSceneManager.SaveOpenScenes();

                var api = ScriptableObject.CreateInstance<TestRunnerApi>();
                api.RegisterCallbacks(_testResultStore);
                api.Execute(new ExecutionSettings(filter));

                // Non-blocking: tests run asynchronously via callbacks.
                // Client should poll GET /tests/results to check completion.
                return JsonResult(new Dictionary<string, object>
                {
                    { "success", true },
                    { "message", "Test run started. Poll GET /tests/results to check progress." },
                    { "testMode", testModeStr }
                });
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        [BridgeRoute("GET", "/tests/results", Category = "tests", Description = "Get last test run results",
            ReadOnly = true)]
        public static string GetTestResults()
        {
            try
            {
                if (!_testResultStore.HasResults)
                {
                    return JsonResult(new Dictionary<string, object>
                    {
                        { "success", true },
                        { "hasResults", false },
                        { "message", "No test results available. Run tests first with POST /tests/run." }
                    });
                }

                return JsonResult(_testResultStore.BuildResultDict());
            }
            catch (Exception ex)
            {
                return JsonError(ex.Message);
            }
        }

        private class TestRunnerResultStore : ICallbacks
        {
            private readonly List<Dictionary<string, object>> _results = new List<Dictionary<string, object>>();
            private int _passed;
            private int _failed;
            private int _skipped;
            private int _total;
            private double _totalDuration;
            private bool _isComplete;
            private bool _hasResults;

            public bool IsComplete => _isComplete;
            public bool HasResults => _hasResults;

            public void Reset()
            {
                _results.Clear();
                _passed = 0;
                _failed = 0;
                _skipped = 0;
                _total = 0;
                _totalDuration = 0;
                _isComplete = false;
                _hasResults = false;
            }

            public Dictionary<string, object> BuildResultDict()
            {
                return new Dictionary<string, object>
                {
                    { "success", _failed == 0 && _isComplete },
                    { "passed", _passed },
                    { "failed", _failed },
                    { "skipped", _skipped },
                    { "total", _total },
                    { "duration", Math.Round(_totalDuration, 3) },
                    { "isComplete", _isComplete },
                    { "tests", _results }
                };
            }

            public void RunStarted(ITestAdaptor testsToRun)
            {
                // No-op, we reset before execute
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                _totalDuration = result.Duration;
                _isComplete = true;
                _hasResults = true;
            }

            public void TestStarted(ITestAdaptor test)
            {
                // No-op
            }

            public void TestFinished(ITestResultAdaptor result)
            {
                if (result.Test.IsSuite) return;

                _total++;

                var status = result.TestStatus.ToString();
                if (result.TestStatus == TestStatus.Passed) _passed++;
                else if (result.TestStatus == TestStatus.Failed) _failed++;
                else _skipped++;

                var entry = new Dictionary<string, object>
                {
                    { "name", result.Test.Name },
                    { "fullName", result.FullName },
                    { "status", status },
                    { "duration", Math.Round(result.Duration, 4) }
                };

                if (result.TestStatus == TestStatus.Failed && !string.IsNullOrEmpty(result.Message))
                {
                    entry["message"] = result.Message.Length > 500
                        ? result.Message.Substring(0, 500) + "..."
                        : result.Message;
                }

                _results.Add(entry);
            }
        }
    }
}
