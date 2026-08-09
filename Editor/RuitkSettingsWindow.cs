using System.IO;
using Ruitk.Core;
using Ruitk.Core.Config;
using Ruitk.Core.Diagnostics;
using Ruitk.EditorSupport.HMR;
using UnityEditor;
using UnityEngine;

namespace Ruitk.EditorSupport
{
    /// <summary>
    /// The one Reactive UI Toolkit settings window (<i>Reactive UI Toolkit ▸ Settings</i>):
    /// every user-facing knob on a single scrollable screen, in clearly labeled sections —
    /// <b>Configuration</b> (the project-scoped JSON settings store,
    /// <c>Assets/Resources/ReactiveUIToolkit/config.json</c> — a typed editor over that file,
    /// and its ONLY writer), <b>Hot Reload (HMR)</b> (per-developer EditorPrefs, including the
    /// two keybind recorders), and <b>Console navigation</b>. It replaces the former Project
    /// Settings / Preferences pages; windows that used to embed settings (the HMR window) link
    /// here instead.
    ///
    /// <para>Without a settings file the Configuration section is read-only: it shows the
    /// values currently in effect (compiled defaults, or a legacy <c>config.json</c> if the
    /// project still honours one) plus a "Create settings file" button. Nothing is ever
    /// written to the project until the user clicks that button — the Input System pattern:
    /// merely opening the window must not dirty version control or force a file on projects
    /// that are happy with the defaults.</para>
    ///
    /// <para>Every change rewrites the FULL canonical schema
    /// (<see cref="RuitkSettings.ToCanonicalJson"/>), re-imports the asset so
    /// <c>Resources.Load</c> sees the new bytes, and calls <see cref="RuitkSettings.Invalidate"/>
    /// so the next resolver read re-parses.</para>
    /// </summary>
    internal sealed class RuitkSettingsWindow : EditorWindow
    {
        /// <summary>Popup options for the tri-state knobs — indices match <see cref="RuitkTriState"/>.</summary>
        private static readonly GUIContent[] TriStateOptions =
        {
            new GUIContent("Auto"),
            new GUIContent("On"),
            new GUIContent("Off"),
        };

        private Vector2 _scroll;
        private RuitkSettings _model;
        private System.DateTime _modelStamp;
        private UitkxHmrController _prefsController;
        private int _recording; // 0=none, 1=toggle HMR, 2=open window

        [MenuItem("Reactive UI Toolkit/Settings", priority = 300)]
        public static void ShowWindow()
        {
            var wnd = GetWindow<RuitkSettingsWindow>("Reactive UI Toolkit Settings");
            wnd.minSize = new Vector2(380, 440);
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.Space(8);
            DrawConfigurationSection();

            EditorGUILayout.Space(12);
            DrawSeparator();
            EditorGUILayout.Space(8);
            DrawHmrSection();

            EditorGUILayout.Space(12);
            DrawSeparator();
            EditorGUILayout.Space(8);
            DrawConsoleNavigationSection();

            EditorGUILayout.Space(12);
            EditorGUILayout.EndScrollView();
        }

        // ── Configuration (per-project: the JSON settings store) ─────────────

        private void DrawConfigurationSection()
        {
            EditorGUILayout.LabelField("Configuration", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Per project — stored in " + RuitkSettings.ProjectFilePath + ".",
                EditorStyles.miniLabel
            );
            EditorGUILayout.Space(4);

            if (!File.Exists(RuitkSettings.ProjectFilePath))
            {
                DrawNoStore();
                return;
            }

            DrawStore();
        }

        private void DrawNoStore()
        {
            _model = null;

            EditorGUILayout.HelpBox(
                "No Reactive UI Toolkit settings file exists in this project, so the values "
                    + "below are in effect (compiled defaults, or a legacy "
                    + "Assets/ReactiveUIToolkit/config.json if the project still carries one). "
                    + "Create the file to edit them; under Resources it also ships into player "
                    + "builds automatically.",
                MessageType.Info
            );

            EditorGUILayout.Space(4);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.LabelField("Effective values", EditorStyles.boldLabel);
                EditorGUILayout.TextField("Environment", BuildDefinesConfig.ResolveEnvironment());
                EditorGUILayout.Toggle("Time Slicing", BuildDefinesConfig.ResolveTimeSlicing());
                EditorGUILayout.FloatField(
                    "Time Slice (ms)",
                    BuildDefinesConfig.ResolveTimeSliceMs()
                );
                EditorGUILayout.FloatField(
                    "Frame Budget (ms)",
                    BuildDefinesConfig.ResolveFrameBudgetMs()
                );
                EditorGUILayout.Toggle(
                    "Host Node Pool",
                    BuildDefinesConfig.ResolveHostNodePool()
                );
                EditorGUILayout.Toggle(
                    "Hook Validation",
                    BuildDefinesConfig.ResolveHookValidation()
                );
                EditorGUILayout.Toggle(
                    "Strict Diagnostics",
                    BuildDefinesConfig.ResolveStrictDiagnostics()
                );
                EditorGUILayout.Toggle("Strict Mode", BuildDefinesConfig.ResolveStrictMode());
                EditorGUILayout.EnumPopup("Trace Level", BuildDefinesConfig.ResolveTraceLevel());
                EditorGUILayout.Toggle("Diff Tracing", BuildDefinesConfig.ResolveEnableDiffTracing());
                EditorGUILayout.Toggle(
                    "6.5 Mount Watchdog",
                    BuildDefinesConfig.ResolveMountWatchdog()
                );
                EditorGUILayout.Toggle(
                    "6.5 Nested Prevention",
                    BuildDefinesConfig.ResolveNestedPrevention()
                );
                EditorGUILayout.Toggle(
                    "6.5 Nested Repair",
                    BuildDefinesConfig.ResolveNestedRepair()
                );
                EditorGUILayout.TextField(
                    "Diagnostics Output",
                    RuitkDiagnosticsPaths.GetOutputRoot()
                );
            }

            EditorGUILayout.Space(8);
            if (GUILayout.Button("Create settings file", GUILayout.Width(180)))
            {
                WriteStore(new RuitkSettings());
                PingStore();
            }
        }

        private void DrawStore()
        {
            // Re-parse only when the file changed on disk (external edit, VCS update).
            var stamp = File.GetLastWriteTimeUtc(RuitkSettings.ProjectFilePath);
            if (_model == null || stamp != _modelStamp)
            {
                _model = RuitkSettings.Parse(File.ReadAllText(RuitkSettings.ProjectFilePath));
                _modelStamp = stamp;
            }

            EditorGUI.BeginChangeCheck();

            var environment = (RuitkEnvironment)
                EditorGUILayout.EnumPopup(
                    new GUIContent(
                        "Environment",
                        "environment — exposed as HostContext.Environment[\"env\"]. auto resolves "
                            + "to development in the editor and development builds, production "
                            + "otherwise."
                    ),
                    _model.environment
                );
            bool timeSlicing = EditorGUILayout.Toggle(
                new GUIContent(
                    "Time Slicing",
                    "time_slicing — when a scheduler is installed, renders are split into "
                        + "time slices across frames. Off forces every render through the "
                        + "synchronous work loop (explicit scheduler bypass). Default: on."
                ),
                _model.timeSlicing
            );
            float timeSliceMs = EditorGUILayout.FloatField(
                new GUIContent(
                    "Time Slice (ms)",
                    "time_slice_ms — max milliseconds the render phase runs per slice before "
                        + "yielding back to the scheduler. Applies wherever a scheduler "
                        + "slices, the editor included. Default: 2.0."
                ),
                _model.timeSliceMs
            );
            float frameBudgetMs = EditorGUILayout.FloatField(
                new GUIContent(
                    "Frame Budget (ms)",
                    "frame_budget_ms — per-frame budget for the play-mode/player "
                        + "RenderScheduler queues. Unity note: editor mounts use the editor "
                        + "scheduler, which is unbudgeted by design (it drains fully every "
                        + "editor update), so this value does not apply there. Default: 4.0."
                ),
                _model.frameBudgetMs
            );
            bool hostNodePool = EditorGUILayout.Toggle(
                new GUIContent(
                    "Host Node Pool",
                    "host_node_pool — pools unmounted uGUI host elements whose adapters "
                        + "provably reset them (TryResetForPool); off destroys them instead. "
                        + "The UI Toolkit path is never pooled. Default: on."
                ),
                _model.hostNodePool
            );
            var hookValidation = (RuitkTriState)
                EditorGUILayout.Popup(
                    new GUIContent(
                        "Hook Validation",
                        "hook_validation — validates hook order and count on every render "
                            + "(catches conditional hook calls). auto = on in the editor and "
                            + "development builds, off in release players. Default: auto."
                    ),
                    (int)_model.hookValidation,
                    TriStateOptions
                );
            var strictDiagnostics = (RuitkTriState)
                EditorGUILayout.Popup(
                    new GUIContent(
                        "Strict Diagnostics",
                        "strict_diagnostics — development warnings for suspect hook usage "
                            + "(state updates during render, hooks invoked without a "
                            + "dependency array), deduplicated per component. auto = on in "
                            + "the editor and development builds, off in release players. "
                            + "Default: auto."
                    ),
                    (int)_model.strictDiagnostics,
                    TriStateOptions
                );
            bool strictMode = EditorGUILayout.Toggle(
                new GUIContent(
                    "Strict Mode",
                    "strict_mode — double-invokes renders in dev so impure render bodies "
                        + "surface (first result discarded, effects still run once); forced "
                        + "off in release builds. Default: off."
                ),
                _model.strictMode
            );
            var traceLevel = (DiagnosticsConfig.TraceLevel)
                EditorGUILayout.EnumPopup(
                    new GUIContent(
                        "Trace Level",
                        "trace_level — none (silent), basic (structural reconciler events), or "
                            + "verbose (structural + per-element/per-hook detail)."
                    ),
                    _model.traceLevel
                );
            bool diffTracing = EditorGUILayout.Toggle(
                new GUIContent(
                    "Diff Tracing",
                    "diff_tracing — detailed Fiber diff/reconciliation tracing, independent of "
                        + "the trace level."
                ),
                _model.diffTracing
            );
            bool mountWatchdog = EditorGUILayout.Toggle(
                new GUIContent(
                    "6.5 Mount Watchdog",
                    "mount_watchdog — Unity 6.5 PanelRenderer workaround (case IN-150082 + "
                        + "UUM-147875): when an enabled, configured PanelRenderer never delivers "
                        + "its UI reload callback, force the attach path via a panelSettings "
                        + "round-trip. Symptom-gated: inert on fixed editors. Default: on."
                ),
                _model.mountWatchdog
            );
            bool nestedPrevention = EditorGUILayout.Toggle(
                new GUIContent(
                    "6.5 Nested Prevention",
                    "nested_prevention — Unity 6.5 workaround (UUM-148452): disable nested "
                        + "child PanelRenderers around rebuilds the library itself triggers so "
                        + "the release cascade cannot poison them. Default: on."
                ),
                _model.nestedPrevention
            );
            bool nestedRepair = EditorGUILayout.Toggle(
                new GUIContent(
                    "6.5 Nested Repair",
                    "nested_repair — Unity 6.5 workaround (UUM-148452): when a parent rebuild "
                        + "released a nested PanelRenderer's tree and no follow-up callback "
                        + "arrives, destroy + re-add only that nested renderer with all settings "
                        + "copied. Symptom-gated. Default: on."
                ),
                _model.nestedRepair
            );

            string outputFolder = _model.diagnosticsOutputFolder ?? "";
            using (new EditorGUILayout.HorizontalScope())
            {
                outputFolder = EditorGUILayout.TextField(
                    new GUIContent(
                        "Diagnostics Output Folder (Unity-only)",
                        "diagnostics_output_folder — where benchmark results and log captures "
                            + "are written. Empty = <project>/Logs/ReactiveUIToolkit in the "
                            + "editor, <persistentDataPath>/ReactiveUIToolkit in players. "
                            + "Absolute paths are used as-is; relative paths resolve against the "
                            + "project root (editor) or persistentDataPath (player)."
                    ),
                    outputFolder
                );
                if (GUILayout.Button("Browse…", GUILayout.Width(70)))
                {
                    string projectRoot = System.IO.Path.GetDirectoryName(Application.dataPath);
                    string start = string.IsNullOrEmpty(outputFolder) ? projectRoot : outputFolder;
                    string picked = EditorUtility.OpenFolderPanel(
                        "Diagnostics Output Folder",
                        start,
                        ""
                    );
                    if (!string.IsNullOrEmpty(picked))
                    {
                        // Folders inside the project are stored project-relative so the file
                        // stays valid when the project moves or is shared between machines.
                        string normalizedRoot = projectRoot.Replace('\\', '/').TrimEnd('/') + "/";
                        string normalizedPick = picked.Replace('\\', '/');
                        outputFolder = normalizedPick.StartsWith(
                            normalizedRoot,
                            System.StringComparison.OrdinalIgnoreCase
                        )
                            ? normalizedPick.Substring(normalizedRoot.Length)
                            : picked;
                        GUI.FocusControl(null);
                    }
                }
            }

            if (EditorGUI.EndChangeCheck())
            {
                _model.environment = environment;
                _model.timeSlicing = timeSlicing;
                _model.timeSliceMs = timeSliceMs;
                _model.frameBudgetMs = frameBudgetMs;
                _model.hostNodePool = hostNodePool;
                _model.hookValidation = hookValidation;
                _model.strictDiagnostics = strictDiagnostics;
                _model.strictMode = strictMode;
                _model.traceLevel = traceLevel;
                _model.diffTracing = diffTracing;
                _model.mountWatchdog = mountWatchdog;
                _model.nestedPrevention = nestedPrevention;
                _model.nestedRepair = nestedRepair;
                _model.diagnosticsOutputFolder = outputFolder;
                WriteStore(_model);
                _modelStamp = File.GetLastWriteTimeUtc(RuitkSettings.ProjectFilePath);
            }

            EditorGUILayout.Space(8);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("File", RuitkSettings.ProjectFilePath);
                if (GUILayout.Button("Select", GUILayout.Width(70)))
                {
                    PingStore();
                }
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                "Effective environment: " + BuildDefinesConfig.ResolveEnvironment()
                    + "\nDiagnostics output root: " + RuitkDiagnosticsPaths.GetOutputRoot()
                    + "\nThe file lives under Resources/, so it ships into player builds and "
                    + "these values apply in players too.",
                MessageType.None
            );
        }

        /// <summary>
        /// The one writer of the settings store (U-01 contract): full canonical schema, then
        /// re-import + <see cref="RuitkSettings.Invalidate"/> so both the asset database and the
        /// cached parse see the new bytes.
        /// </summary>
        private static void WriteStore(RuitkSettings model)
        {
            string directory = System.IO.Path.GetDirectoryName(RuitkSettings.ProjectFilePath);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllText(RuitkSettings.ProjectFilePath, model.ToCanonicalJson());
            AssetDatabase.ImportAsset(RuitkSettings.ProjectFilePath);
            RuitkSettings.Invalidate();
        }

        private static void PingStore()
        {
            var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(RuitkSettings.ProjectFilePath);
            if (asset != null)
            {
                Selection.activeObject = asset;
                EditorGUIUtility.PingObject(asset);
            }
        }

        // ── Hot Reload (HMR) (per-developer: EditorPrefs) ────────────────────

        /// <summary>
        /// The live controller when HMR is running, else a dormant one — constructing a
        /// controller is side-effect free until <c>Start()</c>, and its pref-backed properties
        /// are how the EditorPrefs keys stay defined in exactly one place each.
        /// </summary>
        private UitkxHmrController Controller
        {
            get
            {
                var live = UitkxHmrController.Instance;
                if (live != null)
                {
                    return live;
                }
                return _prefsController ??= new UitkxHmrController();
            }
        }

        private void DrawHmrSection()
        {
            EditorGUILayout.LabelField("Hot Reload (HMR)", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Per developer — stored in EditorPrefs on this machine, not in the project.",
                EditorStyles.miniLabel
            );
            EditorGUILayout.Space(4);

            var controller = Controller;

            bool autoStop = EditorGUILayout.Toggle(
                new GUIContent(
                    "Auto-stop on Play Mode",
                    "Stop HMR automatically when entering Play Mode."
                ),
                controller.AutoStopOnPlayMode
            );
            if (autoStop != controller.AutoStopOnPlayMode)
            {
                controller.AutoStopOnPlayMode = autoStop;
            }

            bool showNotify = EditorGUILayout.Toggle(
                new GUIContent(
                    "Show notifications",
                    "Show an in-window notification after each successful hot swap."
                ),
                controller.ShowNotifications
            );
            if (showNotify != controller.ShowNotifications)
            {
                controller.ShowNotifications = showNotify;
            }

            bool autoReload = EditorGUILayout.Toggle(
                new GUIContent(
                    "Auto-reload on rude edit",
                    "When a CLR rude edit is detected (newly added static readonly field), "
                        + "request a domain reload automatically."
                ),
                controller.AutoReloadOnRudeEdit
            );
            if (autoReload != controller.AutoReloadOnRudeEdit)
            {
                controller.AutoReloadOnRudeEdit = autoReload;
            }

            bool verboseWatcher = EditorGUILayout.Toggle(
                new GUIContent(
                    "Verbose watcher trace",
                    "Log every raw file-watcher event to the Console ([HMR][trace]). High noise; "
                        + "use when a save appears to do nothing."
                ),
                controller.VerboseWatcherTrace
            );
            if (verboseWatcher != controller.VerboseWatcherTrace)
            {
                controller.VerboseWatcherTrace = verboseWatcher;
            }

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Keyboard shortcuts", EditorStyles.miniBoldLabel);
            DrawKeybindRow("Toggle HMR", 1, UitkxHmrKeybinds.ToggleHmrKey);
            DrawKeybindRow("Open HMR Window", 2, UitkxHmrKeybinds.ToggleWindowKey);
        }

        /// <summary>
        /// The keybind recorder (moved here from the HMR window — it needs an EditorWindow's
        /// key-capture loop): click the combo button, press a modifier+key, Escape cancels,
        /// × clears.
        /// </summary>
        private void DrawKeybindRow(string label, int id, KeyCombo current)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(label, GUILayout.Width(EditorGUIUtility.labelWidth));

                if (_recording == id)
                {
                    // Recording mode — capture next key combo
                    GUI.backgroundColor = new Color(1f, 0.85f, 0.3f);
                    GUILayout.Button("Press keys...", EditorStyles.miniButton, GUILayout.Width(90));
                    GUI.backgroundColor = Color.white;

                    var e = Event.current;
                    if (e != null && e.type == EventType.KeyDown)
                    {
                        if (e.keyCode == KeyCode.Escape)
                        {
                            _recording = 0;
                            e.Use();
                        }
                        else if (
                            e.keyCode != KeyCode.None
                            && (e.control || e.alt || e.shift)
                            && e.keyCode != KeyCode.LeftControl
                            && e.keyCode != KeyCode.RightControl
                            && e.keyCode != KeyCode.LeftAlt
                            && e.keyCode != KeyCode.RightAlt
                            && e.keyCode != KeyCode.LeftShift
                            && e.keyCode != KeyCode.RightShift
                        )
                        {
                            var combo = new KeyCombo(e.control, e.alt, e.shift, e.keyCode);
                            if (id == 1)
                                UitkxHmrKeybinds.ToggleHmrKey = combo;
                            else
                                UitkxHmrKeybinds.ToggleWindowKey = combo;
                            _recording = 0;
                            e.Use();
                        }
                    }
                    Repaint();
                }
                else
                {
                    if (
                        GUILayout.Button(
                            current.ToDisplay(),
                            EditorStyles.miniButton,
                            GUILayout.Width(90)
                        )
                    )
                        _recording = id;

                    EditorGUI.BeginDisabledGroup(!current.IsValid);
                    if (GUILayout.Button("×", EditorStyles.miniButton, GUILayout.Width(20)))
                    {
                        if (id == 1)
                            UitkxHmrKeybinds.ToggleHmrKey = default;
                        else
                            UitkxHmrKeybinds.ToggleWindowKey = default;
                    }
                    EditorGUI.EndDisabledGroup();
                }
            }
        }

        // ── Console navigation (per-developer: EditorPrefs) ──────────────────

        private void DrawConsoleNavigationSection()
        {
            EditorGUILayout.LabelField("Console navigation", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Per developer — stored in EditorPrefs on this machine, not in the project.",
                EditorStyles.miniLabel
            );
            EditorGUILayout.Space(4);

            bool navVerbose = EditorGUILayout.Toggle(
                new GUIContent(
                    "Verbose .uitkx navigation logs",
                    "Log every step of .uitkx console-link resolution ([UITKX Nav]). "
                        + "Takes effect immediately — no domain reload needed."
                ),
                Ruitk.Editor.UitkxConsoleNavigation.VerboseLogging
            );
            if (navVerbose != Ruitk.Editor.UitkxConsoleNavigation.VerboseLogging)
            {
                Ruitk.Editor.UitkxConsoleNavigation.VerboseLogging = navVerbose;
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
