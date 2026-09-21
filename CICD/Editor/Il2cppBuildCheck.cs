using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Ruitk.CICD
{
    /// <summary>
    /// Headless IL2CPP build of a shell project that references this package, so the
    /// IL2CPP-only class of defect stops being invisible.
    ///
    /// <para>#253: <c>MultiColumnLayoutTracker&lt;TView, TState&gt;</c> constrained
    /// <c>TState</c> to an interface with no <c>class</c>, then assigned through it.
    /// IL2CPP's generic sharing cannot emit that for a <c>TState</c> that might be a
    /// value type, so the build stops. Mono and the editor compile it happily, which is
    /// why it survived: every suite in this repository runs on Mono or on plain
    /// Roslyn.</para>
    ///
    /// <para>Windows IL2CPP rather than WebGL on purpose. The defect is in IL2CPP's
    /// generic sharing, which is backend-wide, and a Windows player builds in a small
    /// fraction of WebGL's time. WebGL is where it was reported, not where it lives.</para>
    ///
    /// <para>The probe script the driver writes into the shell project's Assets calls
    /// <c>ElementRegistryProvider.GetDefaultRegistry()</c>, which constructs every element
    /// adapter by hand. That is what roots the tracker's generic instantiation: without a
    /// live reference, managed stripping removes the adapters and IL2CPP never has to emit
    /// the code under test, and the build passes for the wrong reason.</para>
    ///
    /// <code>
    /// Unity -batchmode -nographics -quit -projectPath &lt;shell&gt;
    ///       -executeMethod Ruitk.CICD.Il2cppBuildCheck.Run
    ///       [-buildOut &lt;absolute folder&gt;]
    /// </code>
    /// </summary>
    internal static class Il2cppBuildCheck
    {
        private const string ProbeTypeName = "RuitkIl2cppProbe, Assembly-CSharp";
        private const string ScenePath = "Assets/RuitkIl2cppProbe.unity";

        public static void Run()
        {
            try
            {
                string outputDir = ReadArg("-buildOut")
                    ?? Path.Combine(Path.GetTempPath(), "ruitk-il2cpp-build");
                Directory.CreateDirectory(outputDir);

                CreateProbeScene();

                var group = BuildTargetGroup.Standalone;
                var target = BuildTarget.StandaloneWindows64;

                PlayerSettings.SetScriptingBackend(group, ScriptingImplementation.IL2CPP);
                // Minimal keeps user assemblies intact. Anything more aggressive can strip
                // the adapters back out and hide the very instantiation under test.
                PlayerSettings.SetManagedStrippingLevel(group, ManagedStrippingLevel.Minimal);
                PlayerSettings.SetIl2CppCompilerConfiguration(
                    group,
                    Il2CppCompilerConfiguration.Debug
                );

                Debug.Log(
                    "[IL2CPP-CHECK] backend="
                        + PlayerSettings.GetScriptingBackend(group)
                        + " stripping="
                        + PlayerSettings.GetManagedStrippingLevel(group)
                        + " out="
                        + outputDir
                );

                var options = new BuildPlayerOptions
                {
                    scenes = new[] { ScenePath },
                    locationPathName = Path.Combine(outputDir, "RuitkIl2cppProbe.exe"),
                    target = target,
                    targetGroup = group,
                    options = BuildOptions.Development,
                };

                BuildReport report = BuildPipeline.BuildPlayer(options);
                BuildSummary summary = report.summary;

                foreach (BuildStep step in report.steps)
                {
                    foreach (BuildStepMessage message in step.messages)
                    {
                        if (message.type == LogType.Error || message.type == LogType.Exception)
                        {
                            Debug.Log("[IL2CPP-CHECK] " + step.name + ": " + message.content);
                        }
                    }
                }

                if (summary.result == BuildResult.Succeeded)
                {
                    Debug.Log(
                        "[IL2CPP-CHECK] RESULT=SUCCEEDED size="
                            + summary.totalSize
                            + " time="
                            + summary.totalTime
                    );
                    EditorApplication.Exit(0);
                    return;
                }

                Debug.Log(
                    "[IL2CPP-CHECK] RESULT=FAILED errors=" + summary.totalErrors
                );
                EditorApplication.Exit(1);
            }
            catch (Exception ex)
            {
                Debug.Log("[IL2CPP-CHECK] RESULT=THREW " + ex);
                EditorApplication.Exit(2);
            }
        }

        private static void CreateProbeScene()
        {
            var probeType = Type.GetType(ProbeTypeName);
            if (probeType == null)
            {
                throw new InvalidOperationException(
                    "The probe script was not compiled into Assembly-CSharp. The driver writes "
                        + "Assets/RuitkIl2cppProbe.cs before Unity opens the project; without it "
                        + "managed stripping removes the element adapters and the build proves "
                        + "nothing."
                );
            }

            var scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene,
                NewSceneMode.Single
            );
            var go = new GameObject("RuitkIl2cppProbe");
            go.AddComponent(probeType);
            EditorSceneManager.SaveScene(scene, ScenePath);
        }

        private static string ReadArg(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
    }
}
