#if UNITY_EDITOR
using System;
using System.Diagnostics;
using System.Text;
using Ruitk;
using Ruitk.EditorSupport;
using Ruitk.Uitkx.Spikes.CanvasSpike;
using Ruitk.Uitkx.Spikes.CodeFieldSpike;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Ruitk.Builder.Spikes
{
    /// <summary>
    /// VE-00 spike hosts. The two .uitkx spikes ARE the dogfooding experiment:
    /// pan/zoom and per-keystroke recolor run through the real reconciler, and
    /// their on-screen HUD numbers decide whether canvas/CodeField stay
    /// dogfooded (plan VE-00: a frame-budget miss demotes that one control).
    /// </summary>
    internal sealed class CanvasSpikeWindow : EditorWindow
    {
        [MenuItem("Reactive UI Toolkit/Diagnostics/Builder Spikes/Canvas (pan-zoom)", priority = 400)]
        private static void Open()
        {
            var w = GetWindow<CanvasSpikeWindow>();
            w.titleContent = new GUIContent("Spike: Canvas");
            w.minSize = new Vector2(800f, 500f);
        }

        private void CreateGUI()
        {
            rootVisualElement.style.flexGrow = 1f;
            EditorRootRendererUtility.Render(rootVisualElement, V.Func(CanvasSpike.Render));
        }

        private void OnDisable() => EditorRootRendererUtility.Unmount(rootVisualElement);
    }

    internal sealed class CodeFieldSpikeWindow : EditorWindow
    {
        [MenuItem("Reactive UI Toolkit/Diagnostics/Builder Spikes/CodeField (overlay)", priority = 401)]
        private static void Open()
        {
            var w = GetWindow<CodeFieldSpikeWindow>();
            w.titleContent = new GUIContent("Spike: CodeField");
            w.minSize = new Vector2(700f, 450f);
        }

        private void CreateGUI()
        {
            rootVisualElement.style.flexGrow = 1f;
            EditorRootRendererUtility.Render(rootVisualElement, V.Func(CodeFieldSpike.Render));
        }

        private void OnDisable() => EditorRootRendererUtility.Unmount(rootVisualElement);
    }

    /// <summary>
    /// Immutable + LSP spikes: plain C# on purpose — they probe the domain and
    /// the process layer, not the rendering stack.
    /// </summary>
    internal sealed class RuntimeSpikeWindow : EditorWindow
    {
        [MenuItem("Reactive UI Toolkit/Diagnostics/Builder Spikes/LSP + Immutable", priority = 402)]
        private static void Open()
        {
            var w = GetWindow<RuntimeSpikeWindow>();
            w.titleContent = new GUIContent("Spike: LSP");
            w.minSize = new Vector2(640f, 420f);
        }

        private BuilderLspClient _client;
        private string _log = "Press a button.";
        private Vector2 _scroll;

        private void OnGUI()
        {
            if (GUILayout.Button("1. Immutable domain check"))
                RunImmutableCheck();
            if (GUILayout.Button("2. Start LSP + initialize + ruitk/schema round-trip"))
                RunLspCheck();
            if (GUILayout.Button("3. Stop LSP (graceful)"))
                StopLsp();

            _scroll = GUILayout.BeginScrollView(_scroll);
            GUILayout.TextArea(_log, GUILayout.ExpandHeight(true));
            GUILayout.EndScrollView();
        }

        private void RunImmutableCheck()
        {
            var sb = new StringBuilder();
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var name = asm.GetName();
                    if (name.Name == "System.Collections.Immutable"
                        || name.Name == "Ruitk.Language.Editor"
                        || name.Name == "System.Memory")
                        sb.AppendLine($"loaded: {name.Name} {name.Version} @ {SafeLocation(asm)}");
                }

                var sw = Stopwatch.StartNew();
                var parsed = BuilderLanguage.Parse(
                    "export VirtualNode SpikeProbe() {\n  return (\n    <Label text=\"ok\" />\n  );\n}\n",
                    "SpikeProbe.uitkx");
                sw.Stop();
                sb.AppendLine($"parse via Ruitk.Language.Editor: OK, {parsed.RootNodes.Length} root node(s), "
                    + $"{parsed.Diagnostics.Length} diagnostic(s), {sw.Elapsed.TotalMilliseconds:F1} ms");
                var immutable = typeof(System.Collections.Immutable.ImmutableArray).Assembly.GetName();
                sb.AppendLine($"ImmutableArray bound to: {immutable.Version}");
                sb.AppendLine("VERDICT: pass if the bound version is 6.0+ and parse succeeded.");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"FAILED: {ex.GetType().Name}: {ex.Message}");
            }
            _log = sb.ToString();
        }

        private static string SafeLocation(System.Reflection.Assembly asm)
        {
            try { return string.IsNullOrEmpty(asm.Location) ? "(dynamic)" : asm.Location; }
            catch { return "(unavailable)"; }
        }

        private async void RunLspCheck()
        {
            var sb = new StringBuilder();
            try
            {
                var total = Stopwatch.StartNew();
                _client?.Dispose();
                _client = BuilderLspClient.StartOrThrow();
                BuilderAssetEvents.ActiveClient = _client;
                sb.AppendLine($"spawned in {total.Elapsed.TotalMilliseconds:F0} ms");

                string projectRoot = System.IO.Path.GetDirectoryName(Application.dataPath);
                var init = Stopwatch.StartNew();
                await _client.InitializeAsync(projectRoot);
                sb.AppendLine($"initialize handshake {init.Elapsed.TotalMilliseconds:F0} ms (root: {projectRoot})");

                var schemaSw = Stopwatch.StartNew();
                var schema = await _client.RequestSchema();
                string json = schema?.Value<string>("json") ?? schema?.Value<string>("Json") ?? "";
                sb.AppendLine($"ruitk/schema {schemaSw.Elapsed.TotalMilliseconds:F0} ms, {json.Length} chars");

                var hooksSw = Stopwatch.StartNew();
                var hooks = await _client.RequestHooks();
                var hookArr = (hooks?["hooks"] ?? hooks?["Hooks"]) as Newtonsoft.Json.Linq.JArray;
                int hookCount = hookArr?.Count ?? 0;
                sb.AppendLine($"ruitk/hooks {hooksSw.Elapsed.TotalMilliseconds:F0} ms, {hookCount} entries");

                sb.AppendLine($"TOTAL cold start {total.Elapsed.TotalMilliseconds:F0} ms");
                sb.AppendLine("VERDICT: pass if schema chars > 100000 and hooks >= 40.");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"FAILED: {ex.GetType().Name}: {ex.Message}");
            }
            _log = sb.ToString();
            Repaint();
        }

        private void StopLsp()
        {
            if (_client == null)
            {
                _log = "no client running";
                return;
            }
            _client.Dispose();
            BuilderAssetEvents.ActiveClient = null;
            _client = null;
            _log = "stopped (graceful shutdown attempted; check Task Manager for orphan dotnet processes "
                + "after 10 domain reloads - there must be none)";
        }

        private void OnDisable() => StopLsp();
    }
}
#endif
