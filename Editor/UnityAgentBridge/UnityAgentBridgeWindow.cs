using UnityEditor;
using UnityEngine;

namespace UnityAgentBridge
{
    public class UnityAgentBridgeWindow : EditorWindow
    {
        private Vector2 _scrollPosition;

        private string _testEndpoint = "/health";
        private string _lastTestResult = "";

        [MenuItem("Window/Agent Bridge")]
        public static void ShowWindow()
        {
            var window = GetWindow<UnityAgentBridgeWindow>("Agent Bridge");
            window.minSize = new Vector2(300, 200);
        }

        private void OnEnable()
        {
            UnityAgentBridgeServer.OnConnectionStateChanged += OnConnectionStateChanged;
        }

        private void OnDisable()
        {
            UnityAgentBridgeServer.OnConnectionStateChanged -= OnConnectionStateChanged;
        }

        private void OnConnectionStateChanged(bool connected)
        {
            Repaint();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(10);

            // Header
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                GUILayout.Label("Agent Bridge", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
            }

            EditorGUILayout.Space(5);

            // Status indicator
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();

                var statusColor = UnityAgentBridgeServer.IsRunning ? Color.green : Color.red;
                var statusText = UnityAgentBridgeServer.IsRunning ? "Connected" : "Disconnected";

                var originalColor = GUI.color;
                GUI.color = statusColor;
                GUILayout.Label("●", GUILayout.Width(20));
                GUI.color = originalColor;

                GUILayout.Label(statusText, EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
            }

            EditorGUILayout.Space(5);

            // Port info
            if (UnityAgentBridgeServer.IsRunning)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    EditorGUILayout.SelectableLabel(
                        $"http://127.0.0.1:{UnityAgentBridgeServer.CurrentPort}",
                        EditorStyles.miniLabel,
                        GUILayout.Height(EditorGUIUtility.singleLineHeight)
                    );
                    GUILayout.FlexibleSpace();
                }
            }

            EditorGUILayout.Space(10);

            // Control buttons
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();

                if (UnityAgentBridgeServer.IsRunning)
                {
                    if (GUILayout.Button("Stop Server", GUILayout.Width(100)))
                    {
                        UnityAgentBridgeServer.StopServer();
                    }
                }
                else
                {
                    if (GUILayout.Button("Start Server", GUILayout.Width(100)))
                    {
                        UnityAgentBridgeServer.StartServer();
                    }
                }

                GUILayout.FlexibleSpace();
            }

            EditorGUILayout.Space(15);

            // Test section
            EditorGUILayout.LabelField("Test Endpoints", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                _testEndpoint = EditorGUILayout.TextField("Endpoint:", _testEndpoint);

                if (GUILayout.Button("Test", GUILayout.Width(50)))
                {
                    TestEndpoint();
                }
            }

            EditorGUILayout.Space(5);

            // Quick test buttons
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("/health"))
                {
                    _testEndpoint = "/health";
                    TestEndpoint();
                }
                if (GUILayout.Button("/hierarchy"))
                {
                    _testEndpoint = "/hierarchy";
                    TestEndpoint();
                }
                if (GUILayout.Button("/scene"))
                {
                    _testEndpoint = "/scene";
                    TestEndpoint();
                }
                if (GUILayout.Button("/prefabs"))
                {
                    _testEndpoint = "/prefabs";
                    TestEndpoint();
                }
            }

            EditorGUILayout.Space(10);

            // Test result
            if (!string.IsNullOrEmpty(_lastTestResult))
            {
                EditorGUILayout.LabelField("Result:", EditorStyles.boldLabel);
                _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition, GUILayout.Height(200));
                EditorGUILayout.TextArea(_lastTestResult, GUILayout.ExpandHeight(true));
                EditorGUILayout.EndScrollView();
            }

            EditorGUILayout.Space(10);

            // Instructions
            EditorGUILayout.HelpBox(
                "Agent Bridge allows AI agents to control the Unity Editor via MCP.\n\n" +
                "1. Ensure the server is running\n" +
                "2. Configure your MCP client with the UnityMCP server\n" +
                "3. Ask your AI agent to interact with your scene",
                MessageType.Info
            );

            // Footer with curl command
            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("Test with curl:", EditorStyles.miniLabel);
            var curlCommand = $"curl http://127.0.0.1:{UnityAgentBridgeServer.CurrentPort}/health";
            EditorGUILayout.SelectableLabel(curlCommand, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
        }

        private async void TestEndpoint()
        {
            if (!UnityAgentBridgeServer.IsRunning)
            {
                _lastTestResult = "Error: Server not running";
                Repaint();
                return;
            }

            try
            {
                using (var client = new System.Net.Http.HttpClient())
                {
                    client.Timeout = System.TimeSpan.FromSeconds(5);
                    var url = $"http://127.0.0.1:{UnityAgentBridgeServer.CurrentPort}{_testEndpoint}";
                    var response = await client.GetAsync(url);
                    var content = await response.Content.ReadAsStringAsync();

                    // Pretty print JSON
                    try
                    {
                        var parsed = JsonUtility.FromJson<object>(content);
                        _lastTestResult = $"Status: {response.StatusCode}\n\n{content}";
                    }
                    catch
                    {
                        _lastTestResult = $"Status: {response.StatusCode}\n\n{content}";
                    }
                }
            }
            catch (System.Exception ex)
            {
                _lastTestResult = $"Error: {ex.Message}";
            }

            Repaint();
        }
    }
}
