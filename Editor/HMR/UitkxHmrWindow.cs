using UnityEditor;
using UnityEngine;

namespace Ruitk.EditorSupport.HMR
{
    /// <summary>
    /// Editor window for UITKX Hot Module Replacement.
    /// Provides Start/Stop toggle, status readout, and stats. Settings (the HMR toggles and
    /// keybind recorders included) live in the unified settings window
    /// (<see cref="RuitkSettingsWindow"/>, <i>Reactive UI Toolkit ▸ Settings</i>) — this
    /// window only links to it.
    /// </summary>
    internal sealed class UitkxHmrWindow : EditorWindow
    {
        private UitkxHmrController _controller;
        private Vector2 _errorScroll;

        // Track last-known state to avoid unnecessary Repaint() calls
        private int _lastSwapCount;
        private int _lastErrorCount;
        private bool _lastActive;

        // Memory tracking
        private long _windowBaselineMemory;
        private double _nextMemoryRefreshTime;
        private const double MemoryRefreshInterval = 2.0; // seconds

        [MenuItem("Reactive UI Toolkit/HMR Mode", priority = 200)]
        public static void ShowWindow()
        {
            var wnd = GetWindow<UitkxHmrWindow>("UITKX Hot Reload");
            wnd.minSize = new Vector2(300, 260);
        }

        public static void RepaintIfOpen()
        {
            if (HasOpenInstances<UitkxHmrWindow>())
                GetWindow<UitkxHmrWindow>().Repaint();
        }

        private void OnEnable()
        {
            _controller = UitkxHmrController.Instance;
            _windowBaselineMemory = UitkxHmrController.CurrentMemoryBytes;
            EditorApplication.update += PeriodicMemoryRefresh;
        }

        private void OnDisable()
        {
            EditorApplication.update -= PeriodicMemoryRefresh;
        }

        private void PeriodicMemoryRefresh()
        {
            if (EditorApplication.timeSinceStartup < _nextMemoryRefreshTime)
                return;
            _nextMemoryRefreshTime = EditorApplication.timeSinceStartup + MemoryRefreshInterval;
            Repaint();
        }

        private void OnDestroy()
        {
            // Auto-stop when the last window is destroyed and editor quits
            if (_controller != null && _controller.Active && !HasOpenInstances<UitkxHmrWindow>())
            {
                _controller.Stop();
            }
        }

        private void OnGUI()
        {
            bool isActive = _controller != null && _controller.Active;

            EditorGUILayout.Space(8);

            // ── Start / Stop button ──────────────────────────────────────────
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();

                if (isActive)
                {
                    GUI.backgroundColor = new Color(0.9f, 0.3f, 0.3f);
                    if (
                        GUILayout.Button(
                            "  ■  Stop HMR  ",
                            GUILayout.Height(32),
                            GUILayout.MinWidth(160)
                        )
                    )
                    {
                        _controller.Stop();
                    }
                    GUI.backgroundColor = Color.white;
                }
                else
                {
                    GUI.backgroundColor = new Color(0.3f, 0.8f, 0.3f);
                    if (
                        GUILayout.Button(
                            "  ●  Start HMR  ",
                            GUILayout.Height(32),
                            GUILayout.MinWidth(160)
                        )
                    )
                    {
                        // Reuse existing controller to keep the compiler alive
                        // across start/stop cycles (avoids 150-200MB Roslyn re-init)
                        if (_controller == null)
                            _controller = new UitkxHmrController();
                        if (!_controller.Start(out string error))
                        {
                            EditorUtility.DisplayDialog(
                                "HMR Error",
                                "Failed to start HMR:\n\n" + error,
                                "OK"
                            );
                            _controller.Dispose();
                            _controller = null;
                        }
                    }
                    GUI.backgroundColor = Color.white;
                }

                GUILayout.FlexibleSpace();
            }

            EditorGUILayout.Space(4);

            // ── Status ───────────────────────────────────────────────────────
            var statusStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 12,
            };

            if (isActive)
            {
                statusStyle.normal.textColor = new Color(0.2f, 0.8f, 0.2f);
                EditorGUILayout.LabelField("● ACTIVE", statusStyle);
            }
            else
            {
                statusStyle.normal.textColor = Color.gray;
                EditorGUILayout.LabelField("Idle", statusStyle);
            }

            EditorGUILayout.Space(8);
            DrawSeparator();
            EditorGUILayout.Space(4);

            // ── Stats ────────────────────────────────────────────────────────
            if (isActive || (_controller != null && _controller.SwapCount > 0))
            {
                EditorGUILayout.LabelField("Watched", "Assets + embedded/local packages (**/*.uitkx)");
                EditorGUILayout.LabelField("Swaps", _controller?.SwapCount.ToString() ?? "0");
                EditorGUILayout.LabelField("Errors", _controller?.ErrorCount.ToString() ?? "0");

                if (!string.IsNullOrEmpty(_controller?.LastComponentName))
                {
                    EditorGUILayout.Space(2);
                    EditorGUILayout.LabelField(
                        "Last",
                        $"{_controller.LastComponentName} ({_controller.LastSwapMs:F0}ms)"
                    );

                    // Show timing breakdown on hover/always in a smaller font
                    if (!string.IsNullOrEmpty(_controller.LastTimingBreakdown))
                    {
                        var miniStyle = new GUIStyle(EditorStyles.miniLabel)
                        {
                            wordWrap = true,
                            normal = { textColor = new Color(0.6f, 0.6f, 0.6f) },
                        };
                        EditorGUILayout.LabelField(_controller.LastTimingBreakdown, miniStyle);
                    }
                }

                EditorGUILayout.Space(4);
                DrawSeparator();
                EditorGUILayout.Space(4);
            }

            // ── Memory ───────────────────────────────────────────────────────
            {
                long currentMem = UitkxHmrController.CurrentMemoryBytes;
                float currentMB = currentMem / (1024f * 1024f);
                float windowDeltaMB = (currentMem - _windowBaselineMemory) / (1024f * 1024f);

                EditorGUILayout.LabelField(
                    "RAM",
                    $"{currentMB:F0} MB  ({(windowDeltaMB >= 0 ? "+" : "")}{windowDeltaMB:F1} since open)"
                );
                if (_controller != null && _controller.Active)
                {
                    float sessionDelta = _controller.SessionMemoryDeltaMB;
                    EditorGUILayout.LabelField(
                        "Session Δ",
                        $"{(sessionDelta >= 0 ? "+" : "")}{sessionDelta:F1} MB"
                    );
                }

                EditorGUILayout.Space(4);
                DrawSeparator();
                EditorGUILayout.Space(4);
            }

            // ── Settings link ────────────────────────────────────────────────
            // The HMR toggles and keybind recorders live in the unified settings
            // window; this row is the "for comfort" link to it.
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Settings…", EditorStyles.miniButton, GUILayout.Width(90)))
                    RuitkSettingsWindow.ShowWindow();
                GUILayout.FlexibleSpace();
            }

            // ── Warning ──────────────────────────────────────────────────────
            if (isActive)
            {
                EditorGUILayout.Space(8);
                EditorGUILayout.HelpBox(
                    "Assembly reload is locked.\n" + "Stop HMR to compile normally.",
                    MessageType.Warning
                );
            }

            // ── Recent errors ────────────────────────────────────────────────
            if (_controller != null && _controller.RecentErrors.Count > 0)
            {
                EditorGUILayout.Space(4);
                DrawSeparator();
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Recent Errors", EditorStyles.boldLabel);

                _errorScroll = EditorGUILayout.BeginScrollView(
                    _errorScroll,
                    GUILayout.MaxHeight(120)
                );
                foreach (var err in _controller.RecentErrors)
                {
                    EditorGUILayout.TextArea(err, EditorStyles.wordWrappedLabel);
                    EditorGUILayout.Space(2);
                }
                EditorGUILayout.EndScrollView();
            }

            // Repaint only when HMR state changes (not every frame)
            if (isActive)
            {
                int swaps = _controller?.SwapCount ?? 0;
                int errors = _controller?.ErrorCount ?? 0;
                if (swaps != _lastSwapCount || errors != _lastErrorCount || isActive != _lastActive)
                {
                    _lastSwapCount = swaps;
                    _lastErrorCount = errors;
                    _lastActive = isActive;
                    Repaint();
                }
            }
            else if (isActive != _lastActive)
            {
                _lastActive = isActive;
                Repaint();
            }
        }

        private static void DrawSeparator()
        {
            var rect = EditorGUILayout.GetControlRect(false, 1);
            rect.height = 1;
            EditorGUI.DrawRect(rect, new Color(0.5f, 0.5f, 0.5f, 0.3f));
        }
    }
}
