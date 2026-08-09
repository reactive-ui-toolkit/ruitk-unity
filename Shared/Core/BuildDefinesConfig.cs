using Ruitk.Core.Config;
using Ruitk.Core.Diagnostics;
using UnityEngine;

namespace Ruitk.Core
{
    /// <summary>
    /// The bootstrap-facing resolvers the root renderers call once at mount. Resolution order for
    /// every value: the JSON settings store (<see cref="RuitkSettings"/> —
    /// <c>Assets/Resources/ReactiveUIToolkit/config.json</c>, edited via the settings window,
    /// <i>Reactive UI Toolkit ▸ Settings</i>; ships into player builds via <c>Resources</c>) → the
    /// legacy <c>config.json</c> (<see cref="RuitkConfig"/>, kept for store customers with an
    /// edited file) → compiled defaults (which <see cref="RuitkConfig"/> returns when no file
    /// exists).
    /// </summary>
    public static class BuildDefinesConfig
    {
        /// <summary>
        /// The canonical tri-state mapping: <c>On</c>/<c>Off</c> are literal; <c>Auto</c> means ON
        /// in the editor and in development player builds, OFF in release players.
        /// </summary>
        public static bool MapTriState(RuitkTriState value)
        {
            switch (value)
            {
                case RuitkTriState.On:
                    return true;
                case RuitkTriState.Off:
                    return false;
                default:
                    return Application.isEditor || Debug.isDebugBuild;
            }
        }

        public static string ResolveEnvironment()
        {
            var settings = RuitkSettings.ActiveOrNull;
            if (settings != null)
            {
                return settings.ResolveEnvironmentLabel();
            }
            return RuitkConfig.Current.EnvironmentLabel ?? "production";
        }

        public static DiagnosticsConfig.TraceLevel ResolveTraceLevel()
        {
            var settings = RuitkSettings.ActiveOrNull;
            if (settings != null)
            {
                return settings.traceLevel;
            }
            return RuitkConfig.Current.TraceLevel;
        }

        public static bool ResolveEnableDiffTracing()
        {
            var settings = RuitkSettings.ActiveOrNull;
            if (settings != null)
            {
                return settings.diffTracing;
            }
            return RuitkConfig.Current.EnableDiffTracing;
        }

        // ── Reconciler knobs (0.13 additions) ─────────────────────────────────
        // The legacy config.json only ever carried env/traceLevel/diffTracing, so these
        // resolvers have no legacy hop: JSON store → compiled default (the §3 defaults,
        // which reproduce the pre-knob constants byte-for-byte).

        /// <summary>
        /// <c>time_slicing</c> — whether renders may be time-sliced through an installed
        /// scheduler; <c>false</c> forces the synchronous work loop (explicit scheduler
        /// bypass). Default <c>true</c>.
        /// </summary>
        public static bool ResolveTimeSlicing()
        {
            var settings = RuitkSettings.ActiveOrNull;
            return settings != null ? settings.timeSlicing : true;
        }

        /// <summary>
        /// <c>time_slice_ms</c> — the render-phase slice budget in milliseconds. Default
        /// <c>2.0</c> (the former <c>FiberReconciler</c> constant).
        /// </summary>
        public static float ResolveTimeSliceMs()
        {
            var settings = RuitkSettings.ActiveOrNull;
            return settings != null ? settings.timeSliceMs : 2.0f;
        }

        /// <summary>
        /// <c>frame_budget_ms</c> — the play-mode/player <c>RenderScheduler</c> per-frame
        /// queue budget in milliseconds. Default <c>4.0</c>. The editor scheduler is
        /// unbudgeted by design and does not read this.
        /// </summary>
        public static float ResolveFrameBudgetMs()
        {
            var settings = RuitkSettings.ActiveOrNull;
            return settings != null ? settings.frameBudgetMs : 4.0f;
        }

        /// <summary>
        /// <c>host_node_pool</c> — gates the uGUI host-element pool (adapter-gated
        /// <c>TryResetForPool</c> reuse); <c>false</c> destroys unmounted elements instead.
        /// Default <c>true</c>. The UI Toolkit path is never pooled.
        /// </summary>
        public static bool ResolveHostNodePool()
        {
            var settings = RuitkSettings.ActiveOrNull;
            return settings != null ? settings.hostNodePool : true;
        }

        /// <summary>
        /// <c>mount_watchdog</c> — the Unity 6.5 PanelRenderer mount watchdog (WA1/WA2:
        /// case IN-150082 + UUM-147875). Symptom-gated, so it is inert on fixed editors.
        /// Default <c>true</c>.
        /// </summary>
        public static bool ResolveMountWatchdog()
        {
            var settings = RuitkSettings.ActiveOrNull;
            return settings != null ? settings.mountWatchdog : true;
        }

        /// <summary>
        /// <c>nested_prevention</c> — disable nested child PanelRenderers around rebuilds
        /// the library itself triggers (WA3: UUM-148452). Default <c>true</c>.
        /// </summary>
        public static bool ResolveNestedPrevention()
        {
            var settings = RuitkSettings.ActiveOrNull;
            return settings != null ? settings.nestedPrevention : true;
        }

        /// <summary>
        /// <c>nested_repair</c> — destroy + re-add a nested PanelRenderer whose tree Unity
        /// released without a follow-up callback (WA4: UUM-148452). Symptom-gated.
        /// Default <c>true</c>.
        /// </summary>
        public static bool ResolveNestedRepair()
        {
            var settings = RuitkSettings.ActiveOrNull;
            return settings != null ? settings.nestedRepair : true;
        }

        // ── Strict knobs (0.13 additions; same no-legacy-hop chain) ───────────

        /// <summary>
        /// <c>hook_validation</c> — hook order/count validation on every render
        /// (<see cref="Hooks.EnableHookValidation"/>). Tri-state: <c>auto</c> (the default)
        /// maps to ON in the editor and development builds, OFF in release players.
        /// </summary>
        public static bool ResolveHookValidation()
        {
            var settings = RuitkSettings.ActiveOrNull;
            return MapTriState(settings != null ? settings.hookValidation : RuitkTriState.Auto);
        }

        /// <summary>
        /// <c>strict_diagnostics</c> — development-time warnings for suspect hook usage
        /// (<see cref="Hooks.EnableStrictDiagnostics"/>): state updates during render, hooks
        /// invoked without a dependency array. Same tri-state mapping as
        /// <c>hook_validation</c>.
        /// </summary>
        public static bool ResolveStrictDiagnostics()
        {
            var settings = RuitkSettings.ActiveOrNull;
            return MapTriState(settings != null ? settings.strictDiagnostics : RuitkTriState.Auto);
        }

        /// <summary>
        /// <c>strict_mode</c> — double-invoke function-component renders
        /// (<see cref="Ruitk.Core.Fiber.FiberConfig.StrictModeEnabled"/>). Default
        /// <c>false</c>, opt-in, and FORCE-OFF in release players: the stored value cannot
        /// activate it outside the editor / development builds. The denial is resolver-level,
        /// not <c>#if</c> — the code ships into builds, the activation is denied.
        /// </summary>
        public static bool ResolveStrictMode()
        {
            return ResolveStrictMode(Application.isEditor || Debug.isDebugBuild);
        }

        /// <summary>
        /// Testable core of <see cref="ResolveStrictMode()"/>.
        /// <paramref name="developmentContext"/> is
        /// <c>Application.isEditor || Debug.isDebugBuild</c> in production callers; a release
        /// player (<c>false</c>) resolves to <c>false</c> regardless of the stored value.
        /// </summary>
        internal static bool ResolveStrictMode(bool developmentContext)
        {
            if (!developmentContext)
            {
                return false;
            }
            var settings = RuitkSettings.ActiveOrNull;
            return settings != null && settings.strictMode;
        }
    }
}
