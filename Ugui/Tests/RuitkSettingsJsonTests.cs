using NUnit.Framework;
using Ruitk.Core;
using Ruitk.Core.Config;
using Ruitk.Core.Diagnostics;

namespace Ruitk.Ugui.Tests
{
    /// <summary>
    /// EditMode tests for the JSON settings store (<see cref="RuitkSettings"/>) and the
    /// three-hop resolution chain (<see cref="BuildDefinesConfig"/>): parse-string cases
    /// (missing key = default, unknown key ignored, bad enum value = default, case
    /// insensitivity), the canonical-schema round-trip, the tri-state mapping, and the
    /// resolution-order proof seeding each hop — the settings-campaign functional smoke,
    /// pinned as a real test.
    /// </summary>
    public class RuitkSettingsJsonTests
    {
        /// <summary>The §3 canonical schema body — the writer must emit exactly this at defaults.</summary>
        private const string CanonicalDefaultJson =
            "{\n"
            + "  \"environment\": \"auto\",\n"
            + "  \"time_slicing\": true,\n"
            + "  \"time_slice_ms\": 2.0,\n"
            + "  \"frame_budget_ms\": 4.0,\n"
            + "  \"host_node_pool\": true,\n"
            + "  \"hook_validation\": \"auto\",\n"
            + "  \"strict_diagnostics\": \"auto\",\n"
            + "  \"strict_mode\": false,\n"
            + "  \"trace_level\": \"none\",\n"
            + "  \"diff_tracing\": false,\n"
            + "  \"diagnostics_output_folder\": \"\",\n"
            + "  \"mount_watchdog\": true,\n"
            + "  \"nested_prevention\": true,\n"
            + "  \"nested_repair\": true\n"
            + "}\n";

        [SetUp]
        public void SetUp()
        {
            ResetStores();
        }

        [TearDown]
        public void TearDown()
        {
            ResetStores();
        }

        private static void ResetStores()
        {
            RuitkSettings.SetActive(null);
            RuitkSettings.SuppressResourceLoadForTests = false;
            RuitkSettings.Invalidate();
            RuitkConfig.SetCurrentForTests(null);
        }

        private static void AssertAllDefaults(RuitkSettings s)
        {
            Assert.AreEqual(RuitkEnvironment.Auto, s.environment);
            Assert.IsTrue(s.timeSlicing);
            Assert.AreEqual(2.0f, s.timeSliceMs);
            Assert.AreEqual(4.0f, s.frameBudgetMs);
            Assert.IsTrue(s.hostNodePool);
            Assert.AreEqual(RuitkTriState.Auto, s.hookValidation);
            Assert.AreEqual(RuitkTriState.Auto, s.strictDiagnostics);
            Assert.IsFalse(s.strictMode);
            Assert.AreEqual(DiagnosticsConfig.TraceLevel.None, s.traceLevel);
            Assert.IsFalse(s.diffTracing);
            Assert.AreEqual("", s.diagnosticsOutputFolder);
            Assert.IsTrue(s.mountWatchdog);
            Assert.IsTrue(s.nestedPrevention);
            Assert.IsTrue(s.nestedRepair);
        }

        // ── Parse cases ───────────────────────────────────────────────────────

        [Test]
        public void Parse_EmptyObject_YieldsAllDefaults()
        {
            AssertAllDefaults(RuitkSettings.Parse("{}"));
        }

        [Test]
        public void Parse_NullOrWhitespace_YieldsAllDefaults()
        {
            AssertAllDefaults(RuitkSettings.Parse(null));
            AssertAllDefaults(RuitkSettings.Parse(""));
            AssertAllDefaults(RuitkSettings.Parse("   \n"));
        }

        [Test]
        public void Parse_CanonicalDefaultBody_YieldsAllDefaults()
        {
            AssertAllDefaults(RuitkSettings.Parse(CanonicalDefaultJson));
        }

        [Test]
        public void CanonicalJson_OfDefaults_IsTheCanonicalSchemaByteForByte()
        {
            Assert.AreEqual(CanonicalDefaultJson, new RuitkSettings().ToCanonicalJson());
        }

        [Test]
        public void CanonicalJson_RoundTrips_NonDefaultValues()
        {
            var settings = new RuitkSettings
            {
                environment = RuitkEnvironment.Production,
                timeSlicing = false,
                timeSliceMs = 1.5f,
                frameBudgetMs = 8.0f,
                hostNodePool = false,
                hookValidation = RuitkTriState.On,
                strictDiagnostics = RuitkTriState.Off,
                strictMode = true,
                traceLevel = DiagnosticsConfig.TraceLevel.Verbose,
                diffTracing = true,
                diagnosticsOutputFolder = "Logs/Custom",
                mountWatchdog = false,
                nestedPrevention = false,
                nestedRepair = false,
            };

            var parsed = RuitkSettings.Parse(settings.ToCanonicalJson());

            Assert.AreEqual(RuitkEnvironment.Production, parsed.environment);
            Assert.IsFalse(parsed.timeSlicing);
            Assert.AreEqual(1.5f, parsed.timeSliceMs);
            Assert.AreEqual(8.0f, parsed.frameBudgetMs);
            Assert.IsFalse(parsed.hostNodePool);
            Assert.AreEqual(RuitkTriState.On, parsed.hookValidation);
            Assert.AreEqual(RuitkTriState.Off, parsed.strictDiagnostics);
            Assert.IsTrue(parsed.strictMode);
            Assert.AreEqual(DiagnosticsConfig.TraceLevel.Verbose, parsed.traceLevel);
            Assert.IsTrue(parsed.diffTracing);
            Assert.AreEqual("Logs/Custom", parsed.diagnosticsOutputFolder);
            Assert.IsFalse(parsed.mountWatchdog);
            Assert.IsFalse(parsed.nestedPrevention);
            Assert.IsFalse(parsed.nestedRepair);
        }

        [Test]
        public void Parse_UnknownKey_IsIgnored()
        {
            var parsed = RuitkSettings.Parse(
                "{ \"environment\": \"production\", \"not_a_known_key\": 123 }"
            );
            Assert.AreEqual(RuitkEnvironment.Production, parsed.environment);
            Assert.AreEqual(DiagnosticsConfig.TraceLevel.None, parsed.traceLevel);
        }

        [Test]
        public void Parse_MissingKeys_KeepDefaults()
        {
            var parsed = RuitkSettings.Parse("{ \"trace_level\": \"basic\" }");
            Assert.AreEqual(DiagnosticsConfig.TraceLevel.Basic, parsed.traceLevel);
            // Everything else untouched by the partial document:
            Assert.AreEqual(RuitkEnvironment.Auto, parsed.environment);
            Assert.IsTrue(parsed.timeSlicing);
            Assert.AreEqual(2.0f, parsed.timeSliceMs);
            Assert.IsTrue(parsed.hostNodePool);
        }

        [Test]
        public void Parse_BadEnumValues_FallBackToDefaults()
        {
            // Editor-only warnings fire for each unknown value; LogAssert tolerates them.
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            try
            {
                var parsed = RuitkSettings.Parse(
                    "{ \"environment\": \"staging\", \"trace_level\": \"chatty\", "
                        + "\"hook_validation\": \"maybe\", \"strict_diagnostics\": \"sometimes\" }"
                );
                Assert.AreEqual(RuitkEnvironment.Auto, parsed.environment);
                Assert.AreEqual(DiagnosticsConfig.TraceLevel.None, parsed.traceLevel);
                Assert.AreEqual(RuitkTriState.Auto, parsed.hookValidation);
                Assert.AreEqual(RuitkTriState.Auto, parsed.strictDiagnostics);
            }
            finally
            {
                UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false;
            }
        }

        [Test]
        public void Parse_IsCaseInsensitive()
        {
            var parsed = RuitkSettings.Parse(
                "{ \"environment\": \"PRODUCTION\", \"trace_level\": \"Basic\", "
                    + "\"hook_validation\": \"On\", \"strict_diagnostics\": \"OFF\" }"
            );
            Assert.AreEqual(RuitkEnvironment.Production, parsed.environment);
            Assert.AreEqual(DiagnosticsConfig.TraceLevel.Basic, parsed.traceLevel);
            Assert.AreEqual(RuitkTriState.On, parsed.hookValidation);
            Assert.AreEqual(RuitkTriState.Off, parsed.strictDiagnostics);
        }

        // ── Tri-state mapping (auto in editor context = on) ───────────────────

        [Test]
        public void MapTriState_Table()
        {
            Assert.IsTrue(BuildDefinesConfig.MapTriState(RuitkTriState.On));
            Assert.IsFalse(BuildDefinesConfig.MapTriState(RuitkTriState.Off));
            // These tests run inside the editor, so Auto maps to ON here.
            Assert.IsTrue(BuildDefinesConfig.MapTriState(RuitkTriState.Auto));
        }

        // ── Resolution order: JSON store → legacy config.json → defaults ──────

        [Test]
        public void ResolutionOrder_JsonStore_WinsOverEverything()
        {
            RuitkSettings.SuppressResourceLoadForTests = true;
            RuitkSettings.Invalidate();
            RuitkConfig.SetCurrentForTests(
                RuitkConfig.Parse(
                    "{ \"envVariables\": { \"env\": \"development\", \"traceLevel\": \"Basic\", "
                        + "\"diffTracing\": true } }"
                )
            );
            RuitkSettings.SetActive(
                RuitkSettings.Parse(
                    "{ \"environment\": \"production\", \"trace_level\": \"verbose\", "
                        + "\"diff_tracing\": false }"
                )
            );

            Assert.AreEqual("production", BuildDefinesConfig.ResolveEnvironment());
            Assert.AreEqual(
                DiagnosticsConfig.TraceLevel.Verbose,
                BuildDefinesConfig.ResolveTraceLevel()
            );
            Assert.IsFalse(BuildDefinesConfig.ResolveEnableDiffTracing());
        }

        [Test]
        public void ResolutionOrder_LegacyConfig_WhenNoJsonStore()
        {
            RuitkSettings.SetActive(null);
            RuitkSettings.SuppressResourceLoadForTests = true;
            RuitkSettings.Invalidate();
            RuitkConfig.SetCurrentForTests(
                RuitkConfig.Parse(
                    "{ \"envVariables\": { \"env\": \"development\", \"traceLevel\": \"Basic\", "
                        + "\"diffTracing\": true } }"
                )
            );

            Assert.AreEqual("development", BuildDefinesConfig.ResolveEnvironment());
            Assert.AreEqual(
                DiagnosticsConfig.TraceLevel.Basic,
                BuildDefinesConfig.ResolveTraceLevel()
            );
            Assert.IsTrue(BuildDefinesConfig.ResolveEnableDiffTracing());
        }

        [Test]
        public void ResolutionOrder_CompiledDefaults_WhenNeitherStoreExists()
        {
            RuitkSettings.SetActive(null);
            RuitkSettings.SuppressResourceLoadForTests = true;
            RuitkSettings.Invalidate();
            // An empty legacy document = the compiled defaults RuitkConfig carries.
            RuitkConfig.SetCurrentForTests(RuitkConfig.Parse("{}"));

            Assert.AreEqual("production", BuildDefinesConfig.ResolveEnvironment());
            Assert.AreEqual(
                DiagnosticsConfig.TraceLevel.None,
                BuildDefinesConfig.ResolveTraceLevel()
            );
            Assert.IsFalse(BuildDefinesConfig.ResolveEnableDiffTracing());
        }

        [Test]
        public void ResolutionOrder_LegacyParse_IsNeverThrowing()
        {
            var cfg = RuitkConfig.Parse("this is not json");
            Assert.AreEqual("production", cfg.EnvironmentLabel);
            Assert.AreEqual(DiagnosticsConfig.TraceLevel.None, cfg.TraceLevel);
            Assert.IsFalse(cfg.EnableDiffTracing);
        }

        // ── Reconciler knobs (M3): resolution order, defaults, key spellings ──

        [Test]
        public void Parse_ReconcilerKnobKeys_PartialDocument()
        {
            var parsed = RuitkSettings.Parse(
                "{ \"time_slicing\": false, \"time_slice_ms\": 0.5, "
                    + "\"frame_budget_ms\": 12.5, \"host_node_pool\": false }"
            );
            Assert.IsFalse(parsed.timeSlicing);
            Assert.AreEqual(0.5f, parsed.timeSliceMs);
            Assert.AreEqual(12.5f, parsed.frameBudgetMs);
            Assert.IsFalse(parsed.hostNodePool);
            // Untouched keys keep their defaults:
            Assert.AreEqual(RuitkEnvironment.Auto, parsed.environment);
            Assert.AreEqual(DiagnosticsConfig.TraceLevel.None, parsed.traceLevel);
        }

        [Test]
        public void ResolutionOrder_ReconcilerKnobs_JsonStoreWins()
        {
            RuitkSettings.SuppressResourceLoadForTests = true;
            RuitkSettings.Invalidate();
            RuitkSettings.SetActive(
                RuitkSettings.Parse(
                    "{ \"time_slicing\": false, \"time_slice_ms\": 1.25, "
                        + "\"frame_budget_ms\": 8.0, \"host_node_pool\": false }"
                )
            );

            Assert.IsFalse(BuildDefinesConfig.ResolveTimeSlicing());
            Assert.AreEqual(1.25f, BuildDefinesConfig.ResolveTimeSliceMs());
            Assert.AreEqual(8.0f, BuildDefinesConfig.ResolveFrameBudgetMs());
            Assert.IsFalse(BuildDefinesConfig.ResolveHostNodePool());
        }

        [Test]
        public void ResolutionOrder_ReconcilerKnobs_HaveNoLegacyHop()
        {
            // The legacy config.json never carried these keys: even with a legacy document
            // present, the chain is JSON store → compiled default. The compiled defaults
            // reproduce the pre-knob constants (2.0 / 4.0 / pooling on / slicing on).
            RuitkSettings.SetActive(null);
            RuitkSettings.SuppressResourceLoadForTests = true;
            RuitkSettings.Invalidate();
            RuitkConfig.SetCurrentForTests(
                RuitkConfig.Parse(
                    "{ \"envVariables\": { \"env\": \"development\", \"traceLevel\": \"Verbose\", "
                        + "\"diffTracing\": true } }"
                )
            );

            Assert.IsTrue(BuildDefinesConfig.ResolveTimeSlicing());
            Assert.AreEqual(2.0f, BuildDefinesConfig.ResolveTimeSliceMs());
            Assert.AreEqual(4.0f, BuildDefinesConfig.ResolveFrameBudgetMs());
            Assert.IsTrue(BuildDefinesConfig.ResolveHostNodePool());
        }

        // ── Strict knobs (M4): resolution order, defaults, the release force-off ──

        [Test]
        public void Parse_StrictKnobKeys_PartialDocument()
        {
            var parsed = RuitkSettings.Parse(
                "{ \"hook_validation\": \"on\", \"strict_diagnostics\": \"off\", "
                    + "\"strict_mode\": true }"
            );
            Assert.AreEqual(RuitkTriState.On, parsed.hookValidation);
            Assert.AreEqual(RuitkTriState.Off, parsed.strictDiagnostics);
            Assert.IsTrue(parsed.strictMode);
            // Untouched keys keep their defaults:
            Assert.AreEqual(RuitkEnvironment.Auto, parsed.environment);
            Assert.IsTrue(parsed.timeSlicing);
        }

        [Test]
        public void ResolutionOrder_StrictKnobs_JsonStoreWins()
        {
            RuitkSettings.SuppressResourceLoadForTests = true;
            RuitkSettings.Invalidate();
            RuitkSettings.SetActive(
                RuitkSettings.Parse(
                    "{ \"hook_validation\": \"off\", \"strict_diagnostics\": \"on\", "
                        + "\"strict_mode\": true }"
                )
            );

            Assert.IsFalse(BuildDefinesConfig.ResolveHookValidation());
            Assert.IsTrue(BuildDefinesConfig.ResolveStrictDiagnostics());
            // These tests run in the editor = development context, so the stored opt-in wins.
            Assert.IsTrue(BuildDefinesConfig.ResolveStrictMode());
        }

        [Test]
        public void ResolutionOrder_StrictKnobs_HaveNoLegacyHop()
        {
            // The legacy config.json never carried these keys: even with a legacy document
            // present, the chain is JSON store → compiled default. The tri-states default to
            // auto (= ON in this editor context — the hook-validation release FLIP lives in
            // the auto mapping); strict_mode defaults to off.
            RuitkSettings.SetActive(null);
            RuitkSettings.SuppressResourceLoadForTests = true;
            RuitkSettings.Invalidate();
            RuitkConfig.SetCurrentForTests(
                RuitkConfig.Parse(
                    "{ \"envVariables\": { \"env\": \"development\", \"traceLevel\": \"Verbose\", "
                        + "\"diffTracing\": true } }"
                )
            );

            Assert.IsTrue(BuildDefinesConfig.ResolveHookValidation());
            Assert.IsTrue(BuildDefinesConfig.ResolveStrictDiagnostics());
            Assert.IsFalse(BuildDefinesConfig.ResolveStrictMode());
        }

        // ── Unity 6.5 workaround knobs: keys, defaults, resolution ────────────

        [Test]
        public void Parse_WorkaroundKnobKeys_PartialDocument()
        {
            var parsed = RuitkSettings.Parse(
                "{ \"mount_watchdog\": false, \"nested_prevention\": false, "
                    + "\"nested_repair\": false }"
            );
            Assert.IsFalse(parsed.mountWatchdog);
            Assert.IsFalse(parsed.nestedPrevention);
            Assert.IsFalse(parsed.nestedRepair);
            // Untouched keys keep their defaults:
            Assert.AreEqual(RuitkEnvironment.Auto, parsed.environment);
            Assert.IsTrue(parsed.timeSlicing);
        }

        [Test]
        public void ResolutionOrder_WorkaroundKnobs_JsonStoreWins_AndDefaultOn()
        {
            RuitkSettings.SuppressResourceLoadForTests = true;
            RuitkSettings.Invalidate();

            // No store: all three default on.
            RuitkSettings.SetActive(null);
            Assert.IsTrue(BuildDefinesConfig.ResolveMountWatchdog());
            Assert.IsTrue(BuildDefinesConfig.ResolveNestedPrevention());
            Assert.IsTrue(BuildDefinesConfig.ResolveNestedRepair());

            RuitkSettings.SetActive(
                RuitkSettings.Parse(
                    "{ \"mount_watchdog\": false, \"nested_prevention\": false, "
                        + "\"nested_repair\": false }"
                )
            );
            Assert.IsFalse(BuildDefinesConfig.ResolveMountWatchdog());
            Assert.IsFalse(BuildDefinesConfig.ResolveNestedPrevention());
            Assert.IsFalse(BuildDefinesConfig.ResolveNestedRepair());
        }

        [Test]
        public void ResolveStrictMode_IsForceOffOutsideDevelopmentContext()
        {
            // The release force-off is resolver-level (D-9): with strict_mode stored TRUE,
            // a release-player context (not editor, not debug build) still resolves FALSE —
            // release players cannot opt in. The same stored value activates in a
            // development context.
            RuitkSettings.SuppressResourceLoadForTests = true;
            RuitkSettings.Invalidate();
            RuitkSettings.SetActive(RuitkSettings.Parse("{ \"strict_mode\": true }"));

            Assert.IsFalse(BuildDefinesConfig.ResolveStrictMode(developmentContext: false));
            Assert.IsTrue(BuildDefinesConfig.ResolveStrictMode(developmentContext: true));

            // Without a store the default is off in EVERY context.
            RuitkSettings.SetActive(null);
            Assert.IsFalse(BuildDefinesConfig.ResolveStrictMode(developmentContext: false));
            Assert.IsFalse(BuildDefinesConfig.ResolveStrictMode(developmentContext: true));
        }
    }
}
