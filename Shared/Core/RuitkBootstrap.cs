using Ruitk.Core.Diagnostics;
using Ruitk.Core.Fiber;
using Ruitk.Elements;
using Ruitk.Signals;

namespace Ruitk.Core
{
    /// <summary>
    /// The one bootstrap every mount site shares. Before this existed the same
    /// block lived as three near-identical copies in <c>RootRenderer</c>,
    /// <c>UguiRootRenderer</c> and <c>EditorRootRendererUtility</c>, and each
    /// new host meant a fourth. A host supplies only what genuinely differs
    /// per world: its registry, its backend config (null = the UI Toolkit
    /// default), its scheduler, and whether it is an editor mount.
    /// </summary>
    public static class RuitkBootstrap
    {
        /// <summary>
        /// Resolves every global knob from <see cref="BuildDefinesConfig"/> -
        /// trace level, diff tracing, time slicing, hook validation, strict
        /// mode, internal logs. Idempotent; safe to call at every mount.
        /// </summary>
        public static void ApplyGlobalConfig()
        {
            DiagnosticsConfig.CurrentTraceLevel = BuildDefinesConfig.ResolveTraceLevel();
            DiagnosticsConfig.EnableDiffTracing = BuildDefinesConfig.ResolveEnableDiffTracing();

            FiberConfig.TimeSlicingEnabled = BuildDefinesConfig.ResolveTimeSlicing();
            FiberConfig.TimeSliceMs = BuildDefinesConfig.ResolveTimeSliceMs();

            Hooks.EnableHookValidation = BuildDefinesConfig.ResolveHookValidation();
            Hooks.EnableStrictDiagnostics = BuildDefinesConfig.ResolveStrictDiagnostics();
            FiberConfig.StrictModeEnabled = BuildDefinesConfig.ResolveStrictMode();

            InternalLogOptions.EnableInternalLogs =
                DiagnosticsConfig.CurrentTraceLevel == DiagnosticsConfig.TraceLevel.Verbose;
        }

        /// <summary>
        /// Creates a mount's <see cref="HostContext"/> with the built-in
        /// environment slots seeded and the global config applied. The signals
        /// runtime is initialized as a side effect.
        /// </summary>
        public static HostContext CreateHostContext(
            ElementRegistry registry,
            FiberHostConfig hostConfig,
            IScheduler scheduler,
            bool isEditor
        )
        {
            SignalsRuntime.EnsureInitialized();

            var context =
                hostConfig != null
                    ? new HostContext(registry, hostConfig)
                    : new HostContext(registry);
            context.Environment["scheduler"] = scheduler;
            context.Environment["isEditor"] = isEditor;
            context.Environment["env"] = BuildDefinesConfig.ResolveEnvironment();

            ApplyGlobalConfig();
            return context;
        }
    }
}
