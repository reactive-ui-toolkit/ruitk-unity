using System;
using System.Globalization;
using System.Text;
using Ruitk.Core.Diagnostics;
using UnityEngine;

namespace Ruitk.Core.Config
{
    /// <summary>
    /// Environment selector for <see cref="RuitkSettings.environment"/>. <c>Auto</c> resolves at
    /// read time: editor or development player builds report <c>development</c>, release player
    /// builds report <c>production</c>.
    /// </summary>
    public enum RuitkEnvironment
    {
        Auto,
        Development,
        Production,
    }

    /// <summary>
    /// Tri-state for the settings that default to context-dependent behavior
    /// (<c>hook_validation</c>, <c>strict_diagnostics</c>): <c>Auto</c> maps to ON in the editor
    /// and in development player builds, OFF in release players
    /// (<see cref="Ruitk.Core.BuildDefinesConfig.MapTriState"/>).
    /// </summary>
    public enum RuitkTriState
    {
        Auto,
        On,
        Off,
    }

    /// <summary>
    /// The unified project-wide settings store for Reactive UI Toolkit: a plain JSON file at
    /// <c>Assets/Resources/ReactiveUIToolkit/config.json</c> in the CONSUMER project (never inside
    /// this package), edited via the settings window (<i>Reactive UI Toolkit ▸ Settings</i>) and
    /// <b>created on demand only</b> by that window's create button — merely reading settings never
    /// writes anything into a user's project.
    ///
    /// <para>Living under <c>Resources/</c> the file ships into every player build automatically
    /// (no build hooks, no preloaded-assets injection), and
    /// <c>Resources.Load&lt;TextAsset&gt;("ReactiveUIToolkit/config")</c> is synchronous on all
    /// platforms, WebGL included. The load happens once and is cached;
    /// <see cref="Invalidate"/> clears the cache (the editor window calls it after writes).</para>
    ///
    /// <para><see cref="Ruitk.Core.BuildDefinesConfig"/> consumes this store as the first hop of
    /// the resolution chain: JSON store → legacy <c>Assets/ReactiveUIToolkit/config.json</c>
    /// <c>envVariables</c> (<see cref="RuitkConfig"/>) → compiled defaults. Missing key = the
    /// field's initializer (the §3 default); unknown keys are ignored (forward compat); unknown
    /// enum values fall back to the default with one editor-only warning.</para>
    ///
    /// <para>Runtime-safe by construction: no <c>UnityEditor</c> references may appear here (this
    /// type compiles into <c>Ruitk.Shared</c>, a player assembly). Main thread only
    /// (<c>Resources.Load</c> requires it).</para>
    /// </summary>
    public sealed class RuitkSettings
    {
        /// <summary>The <c>Resources.Load</c> path of the store (no extension).</summary>
        public const string ResourcesLoadPath = "ReactiveUIToolkit/config";

        /// <summary>Where the settings window creates the store, relative to the project root.</summary>
        public const string ProjectFilePath = "Assets/Resources/ReactiveUIToolkit/config.json";

        // ── Typed settings (defaults = the canonical §3 defaults) ─────────────

        public RuitkEnvironment environment = RuitkEnvironment.Auto;
        public bool timeSlicing = true;
        public float timeSliceMs = 2.0f;
        public float frameBudgetMs = 4.0f;
        public bool hostNodePool = true;
        public RuitkTriState hookValidation = RuitkTriState.Auto;
        public RuitkTriState strictDiagnostics = RuitkTriState.Auto;
        public bool strictMode = false;
        public DiagnosticsConfig.TraceLevel traceLevel = DiagnosticsConfig.TraceLevel.None;
        public bool diffTracing = false;
        public string diagnosticsOutputFolder = "";

        // Unity 6.5 PanelRenderer workaround opt-outs (plan §5.9 registry; all
        // symptom-gated so they are inert when the underlying Unity bug is
        // absent - see the Unity 6.5 known-issues docs page).
        public bool mountWatchdog = true;
        public bool nestedPrevention = true;
        public bool nestedRepair = true;

        // ── The active store ──────────────────────────────────────────────────

        private static RuitkSettings s_override;
        private static RuitkSettings s_loaded;
        private static bool s_loadAttempted;

        /// <summary>Test seam: forces <see cref="ActiveOrNull"/> to behave as if no store file exists.</summary>
        internal static bool SuppressResourceLoadForTests;

        /// <summary>
        /// The active settings store, or <c>null</c> when no store exists (⇒ callers fall through
        /// to the legacy <c>config.json</c> and then compiled defaults). Backed by a cached
        /// <c>Resources.Load</c> + parse; an explicit <see cref="SetActive"/> override wins.
        /// </summary>
        public static RuitkSettings ActiveOrNull
        {
            get
            {
                if (s_override != null)
                {
                    return s_override;
                }
                if (!s_loadAttempted)
                {
                    s_loadAttempted = true;
                    s_loaded = LoadFromResources();
                }
                return s_loaded;
            }
        }

        /// <summary>
        /// Installs an explicit settings instance (pass <c>null</c> to clear and fall back to the
        /// Resources-loaded store). An override/test seam — production code never calls this; the
        /// store loads itself.
        /// </summary>
        public static void SetActive(RuitkSettings settings)
        {
            s_override = settings;
        }

        /// <summary>
        /// Drops the cached parse so the next <see cref="ActiveOrNull"/> re-loads. The settings
        /// window calls this after every write (with a re-import, so <c>Resources.Load</c> sees
        /// the new bytes).
        /// </summary>
        public static void Invalidate()
        {
            s_loadAttempted = false;
            s_loaded = null;
        }

        private static RuitkSettings LoadFromResources()
        {
            if (SuppressResourceLoadForTests)
            {
                return null;
            }
            var asset = Resources.Load<TextAsset>(ResourcesLoadPath);
            if (asset == null)
            {
                return null;
            }
            return Parse(asset.text);
        }

        // ── Parsing (string-in core: unit-testable without asset plumbing) ────

        /// <summary>
        /// JsonUtility DTO. Field names ARE the canonical snake_case keys; initializers ARE the
        /// defaults — JsonUtility leaves absent fields at their initializers, which is exactly
        /// missing-key = default, and ignores unknown keys (forward compat).
        /// </summary>
        [Serializable]
        private sealed class Dto
        {
            public string environment = "auto";
            public bool time_slicing = true;
            public float time_slice_ms = 2.0f;
            public float frame_budget_ms = 4.0f;
            public bool host_node_pool = true;
            public string hook_validation = "auto";
            public string strict_diagnostics = "auto";
            public bool strict_mode = false;
            public string trace_level = "none";
            public bool diff_tracing = false;
            public string diagnostics_output_folder = "";
            public bool mount_watchdog = true;
            public bool nested_prevention = true;
            public bool nested_repair = true;
        }

        /// <summary>
        /// Parses store JSON into a settings instance. Never throws: a null/empty/unparseable body
        /// yields the defaults (with one editor-only warning when the body was actually malformed).
        /// </summary>
        public static RuitkSettings Parse(string json)
        {
            var result = new RuitkSettings();
            if (string.IsNullOrWhiteSpace(json))
            {
                return result;
            }

            Dto dto;
            try
            {
                dto = JsonUtility.FromJson<Dto>(json);
            }
            catch (Exception e)
            {
                WarnEditorOnly(
                    "Reactive UI Toolkit: could not parse the settings store ("
                        + ProjectFilePath
                        + "); using defaults. "
                        + e.Message
                );
                return result;
            }
            if (dto == null)
            {
                return result;
            }

            result.environment = ParseEnvironment(dto.environment);
            result.timeSlicing = dto.time_slicing;
            result.timeSliceMs = dto.time_slice_ms;
            result.frameBudgetMs = dto.frame_budget_ms;
            result.hostNodePool = dto.host_node_pool;
            result.hookValidation = ParseTriState(dto.hook_validation, "hook_validation");
            result.strictDiagnostics = ParseTriState(dto.strict_diagnostics, "strict_diagnostics");
            result.strictMode = dto.strict_mode;
            result.traceLevel = ParseTraceLevel(dto.trace_level);
            result.diffTracing = dto.diff_tracing;
            result.diagnosticsOutputFolder = dto.diagnostics_output_folder ?? "";
            result.mountWatchdog = dto.mount_watchdog;
            result.nestedPrevention = dto.nested_prevention;
            result.nestedRepair = dto.nested_repair;
            return result;
        }

        private static RuitkEnvironment ParseEnvironment(string value)
        {
            switch (Lower(value))
            {
                case "":
                case "auto":
                    return RuitkEnvironment.Auto;
                case "development":
                    return RuitkEnvironment.Development;
                case "production":
                    return RuitkEnvironment.Production;
                default:
                    WarnUnknownValue("environment", value, "auto");
                    return RuitkEnvironment.Auto;
            }
        }

        private static RuitkTriState ParseTriState(string value, string key)
        {
            switch (Lower(value))
            {
                case "":
                case "auto":
                    return RuitkTriState.Auto;
                case "on":
                    return RuitkTriState.On;
                case "off":
                    return RuitkTriState.Off;
                default:
                    WarnUnknownValue(key, value, "auto");
                    return RuitkTriState.Auto;
            }
        }

        private static DiagnosticsConfig.TraceLevel ParseTraceLevel(string value)
        {
            switch (Lower(value))
            {
                case "":
                case "none":
                    return DiagnosticsConfig.TraceLevel.None;
                case "basic":
                    return DiagnosticsConfig.TraceLevel.Basic;
                case "verbose":
                    return DiagnosticsConfig.TraceLevel.Verbose;
                default:
                    WarnUnknownValue("trace_level", value, "none");
                    return DiagnosticsConfig.TraceLevel.None;
            }
        }

        private static string Lower(string value)
        {
            return value == null ? "" : value.Trim().ToLowerInvariant();
        }

        private static void WarnUnknownValue(string key, string value, string fallback)
        {
            WarnEditorOnly(
                "Reactive UI Toolkit: unknown value \""
                    + value
                    + "\" for setting \""
                    + key
                    + "\"; using \""
                    + fallback
                    + "\"."
            );
        }

        private static void WarnEditorOnly(string message)
        {
            if (Application.isEditor)
            {
                Debug.LogWarning(message);
            }
        }

        // ── Canonical serialization (the settings window's writer body) ───────

        /// <summary>
        /// The full canonical store body: ALL keys explicit in canonical order, lowercase
        /// enum/tri-state strings, 2-space indent, LF, trailing newline. The settings window always
        /// writes this exact shape.
        /// </summary>
        public string ToCanonicalJson()
        {
            var sb = new StringBuilder(512);
            sb.Append("{\n");
            sb.Append("  \"environment\": \"").Append(EnvironmentToString(environment)).Append("\",\n");
            sb.Append("  \"time_slicing\": ").Append(Bool(timeSlicing)).Append(",\n");
            sb.Append("  \"time_slice_ms\": ").Append(Float(timeSliceMs)).Append(",\n");
            sb.Append("  \"frame_budget_ms\": ").Append(Float(frameBudgetMs)).Append(",\n");
            sb.Append("  \"host_node_pool\": ").Append(Bool(hostNodePool)).Append(",\n");
            sb.Append("  \"hook_validation\": \"").Append(TriStateToString(hookValidation)).Append("\",\n");
            sb.Append("  \"strict_diagnostics\": \"").Append(TriStateToString(strictDiagnostics)).Append("\",\n");
            sb.Append("  \"strict_mode\": ").Append(Bool(strictMode)).Append(",\n");
            sb.Append("  \"trace_level\": \"").Append(TraceLevelToString(traceLevel)).Append("\",\n");
            sb.Append("  \"diff_tracing\": ").Append(Bool(diffTracing)).Append(",\n");
            sb.Append("  \"diagnostics_output_folder\": \"")
                .Append(EscapeJsonString(diagnosticsOutputFolder ?? ""))
                .Append("\",\n");
            sb.Append("  \"mount_watchdog\": ").Append(Bool(mountWatchdog)).Append(",\n");
            sb.Append("  \"nested_prevention\": ").Append(Bool(nestedPrevention)).Append(",\n");
            sb.Append("  \"nested_repair\": ").Append(Bool(nestedRepair)).Append("\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        internal static string EnvironmentToString(RuitkEnvironment value)
        {
            switch (value)
            {
                case RuitkEnvironment.Development:
                    return "development";
                case RuitkEnvironment.Production:
                    return "production";
                default:
                    return "auto";
            }
        }

        internal static string TriStateToString(RuitkTriState value)
        {
            switch (value)
            {
                case RuitkTriState.On:
                    return "on";
                case RuitkTriState.Off:
                    return "off";
                default:
                    return "auto";
            }
        }

        internal static string TraceLevelToString(DiagnosticsConfig.TraceLevel value)
        {
            switch (value)
            {
                case DiagnosticsConfig.TraceLevel.Basic:
                    return "basic";
                case DiagnosticsConfig.TraceLevel.Verbose:
                    return "verbose";
                default:
                    return "none";
            }
        }

        private static string Bool(bool value)
        {
            return value ? "true" : "false";
        }

        private static string Float(float value)
        {
            // Whole values print with one decimal ("2.0", the §3 shape); everything else
            // round-trips ("2.5", "1.25").
            if (value == Math.Floor(value) && !float.IsInfinity(value))
            {
                return value.ToString("0.0", CultureInfo.InvariantCulture);
            }
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static string EscapeJsonString(string value)
        {
            var sb = new StringBuilder(value.Length + 8);
            foreach (char c in value)
            {
                switch (c)
                {
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            return sb.ToString();
        }

        // ── Derived values ────────────────────────────────────────────────────

        /// <summary>
        /// The effective environment string fed to <c>HostContext.Environment["env"]</c>:
        /// <c>"development"</c> or <c>"production"</c>, with <see cref="RuitkEnvironment.Auto"/>
        /// resolved from the running context.
        /// </summary>
        public string ResolveEnvironmentLabel()
        {
            switch (environment)
            {
                case RuitkEnvironment.Development:
                    return "development";
                case RuitkEnvironment.Production:
                    return "production";
                default:
                    return Application.isEditor || Debug.isDebugBuild
                        ? "development"
                        : "production";
            }
        }
    }
}
