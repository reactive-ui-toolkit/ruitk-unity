#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Ruitk.EditorSupport
{
    /// <summary>
    /// Finds project assembly definitions that need a <c>Ruitk.Signals</c> reference after
    /// the 0.21.0 split, and offers to add it.
    ///
    /// <para>Why this exists rather than only the codemod: the person who hits this has a
    /// <c>CS0246</c> in a file they did not write, has never heard of
    /// <c>RuitkMigrateSignalsAsmdef</c>, and has no reason to connect the two. This runs
    /// where they already are.</para>
    ///
    /// <para>It can run at all because the failure is in THEIR assembly, not ours:
    /// <c>Ruitk.Editor</c> still compiles, so it can read their asmdef JSON off disk and
    /// say something useful while their own code is broken.</para>
    ///
    /// <para>Reports once per domain reload, never edits without being asked, and skips
    /// the package's own assemblies. Assets &gt; Reactive UI Toolkit &gt; Fix Signals
    /// Assembly References runs it on demand.</para>
    /// </summary>
    [InitializeOnLoad]
    internal static class SignalsAsmdefCheck
    {
        private const string SignalsAssembly = "Ruitk.Signals";
        private const string MenuPath = "Assets/Reactive UI Toolkit/Fix Signals Assembly References";
        private const string SuppressKey = "Ruitk.SignalsAsmdefCheck.Suppressed";

        private static readonly Regex NamesSignalType = new Regex(
            @"(\bSignal<|\bSignalFactory\b|\bSignalsRuntime\b|\bSignalBase\b|\bSignalUpdate\b|\bSignalRegistry\b|\bSignalSubscription\b|\busing\s+Ruitk\.Signals\b|@Ruitk\.Signals)",
            RegexOptions.Compiled);

        // A .uitkx that calls the lowercase wrapper needs the reference even without naming a
        // signal type: the generator emits that wrapper with a Signal<T> parameter. C# does
        // not -- Hooks.UseSignal("key", 0) binds to an overload that names nothing from the
        // new assembly.
        private static readonly Regex CallsLowercaseUseSignal = new Regex(
            @"\buseSignal\s*(<|\()",
            RegexOptions.Compiled);

        private static readonly string[] FormerCarriers =
        {
            "Ruitk.Shared",
            "Ruitk.Runtime",
            "Ruitk.Ugui",
            "Ruitk.Editor",
        };

        static SignalsAsmdefCheck()
        {
            EditorApplication.delayCall += ReportOnce;
        }

        private static void ReportOnce()
        {
            if (SessionState.GetBool(SuppressKey, false))
            {
                return;
            }
            SessionState.SetBool(SuppressKey, true);

            List<string> needing = FindAsmdefsNeedingSignals();
            if (needing.Count == 0)
            {
                return;
            }

            var sb = new StringBuilder();
            sb.Append("[Ruitk] ")
                .Append(needing.Count)
                .Append(needing.Count == 1 ? " assembly definition uses" : " assembly definitions use")
                .Append(" signals but does not reference '")
                .Append(SignalsAssembly)
                .Append("'.\n\n");
            sb.Append("Signal<T>, SignalFactory and SignalsRuntime moved out of Ruitk.Shared into ")
                .Append("their own assembly in 0.21.0, so that code can depend on signals without ")
                .Append("pulling in the renderer. Unity assembly references are not transitive, so ")
                .Append("an assembly that names a signal type - or a .uitkx that calls useSignal - ")
                .Append("needs the reference added.\n\n");
            foreach (string path in needing)
            {
                sb.Append("  ").Append(path).Append('\n');
            }
            sb.Append("\nFIX: ").Append(MenuPath).Append(" - or run the codemod across the whole ")
                .Append("project:\n  dotnet run --project SourceGenerator~/Tools/RuitkMigrateSignalsAsmdef -- <projectPath>");

            Debug.LogWarning(sb.ToString());
        }

        [MenuItem(MenuPath)]
        private static void FixAll()
        {
            List<string> needing = FindAsmdefsNeedingSignals();
            if (needing.Count == 0)
            {
                Debug.Log("[Ruitk] Every assembly definition that uses signals already references "
                    + SignalsAssembly + ".");
                return;
            }

            int fixedCount = 0;
            foreach (string assetPath in needing)
            {
                string fullPath = Path.Combine(ProjectRoot(), assetPath);
                try
                {
                    string text = File.ReadAllText(fullPath);
                    string updated = InsertReference(text, SignalsAssembly);
                    if (updated == text)
                    {
                        Debug.LogWarning("[Ruitk] Could not find a references array in " + assetPath
                            + " - add \"" + SignalsAssembly + "\" by hand.");
                        continue;
                    }
                    File.WriteAllText(fullPath, updated);
                    fixedCount++;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[Ruitk] Could not update " + assetPath + ": " + ex.Message);
                }
            }

            if (fixedCount > 0)
            {
                AssetDatabase.Refresh();
                Debug.Log("[Ruitk] Added the " + SignalsAssembly + " reference to " + fixedCount
                    + (fixedCount == 1 ? " assembly definition." : " assembly definitions."));
            }
        }

        private static List<string> FindAsmdefsNeedingSignals()
        {
            var needing = new List<string>();
            string root = ProjectRoot();

            foreach (string guid in AssetDatabase.FindAssets("t:AssemblyDefinitionAsset"))
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(assetPath) || !assetPath.StartsWith("Assets/", StringComparison.Ordinal))
                {
                    // Only the user's own Assets/ tree. Package assemblies are not theirs to fix,
                    // and ours are already correct.
                    continue;
                }
                try
                {
                    string fullPath = Path.Combine(root, assetPath);
                    string text = File.ReadAllText(fullPath);
                    if (Mentions(text, SignalsAssembly) || !MentionsAnyFormerCarrier(text))
                    {
                        continue;
                    }
                    if (AnySourceNeedsSignals(Path.GetDirectoryName(fullPath)))
                    {
                        needing.Add(assetPath);
                    }
                }
                catch
                {
                    // Unreadable asmdef: Unity will complain about it on its own.
                }
            }

            needing.Sort(StringComparer.Ordinal);
            return needing;
        }

        private static bool Mentions(string asmdefText, string assemblyName) =>
            asmdefText.IndexOf("\"" + assemblyName + "\"", StringComparison.Ordinal) >= 0;

        private static bool MentionsAnyFormerCarrier(string asmdefText)
        {
            foreach (string carrier in FormerCarriers)
            {
                if (Mentions(asmdefText, carrier))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool AnySourceNeedsSignals(string asmdefDir)
        {
            if (string.IsNullOrEmpty(asmdefDir) || !Directory.Exists(asmdefDir))
            {
                return false;
            }
            foreach (string file in Directory.EnumerateFiles(asmdefDir, "*.*", SearchOption.AllDirectories))
            {
                bool isUitkx = file.EndsWith(".uitkx", StringComparison.OrdinalIgnoreCase);
                if (!isUitkx && !file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (OwnedByANestedAsmdef(file, asmdefDir))
                {
                    continue;
                }
                try
                {
                    string text = File.ReadAllText(file);
                    if (NamesSignalType.IsMatch(text) || (isUitkx && CallsLowercaseUseSignal.IsMatch(text)))
                    {
                        return true;
                    }
                }
                catch
                {
                    // Skip unreadable sources rather than abandoning the scan.
                }
            }
            return false;
        }

        private static bool OwnedByANestedAsmdef(string file, string asmdefDir)
        {
            string dir = Path.GetDirectoryName(file);
            string stop = Path.GetFullPath(asmdefDir).TrimEnd(Path.DirectorySeparatorChar);
            while (!string.IsNullOrEmpty(dir))
            {
                string current = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
                if (string.Equals(current, stop, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                if (Directory.GetFiles(dir, "*.asmdef", SearchOption.TopDirectoryOnly).Length > 0)
                {
                    return true;
                }
                dir = Path.GetDirectoryName(dir);
            }
            return false;
        }

        /// <summary>
        /// Same textual insertion the codemod performs, for the same reason: an assembly
        /// definition is hand-written and hand-read, and reformatting it would bury a
        /// one-line change in a whole-file diff.
        /// </summary>
        internal static string InsertReference(string asmdefText, string assemblyName)
        {
            string newline = asmdefText.Contains("\r\n") ? "\r\n" : "\n";

            var empty = Regex.Match(asmdefText, "\"references\"\\s*:\\s*\\[\\s*\\]");
            if (empty.Success)
            {
                string indent = DetectIndent(asmdefText);
                string replacement =
                    "\"references\": [" + newline + indent + indent + "\"" + assemblyName + "\"" + newline + indent + "]";
                return asmdefText.Substring(0, empty.Index)
                    + replacement
                    + asmdefText.Substring(empty.Index + empty.Length);
            }

            var firstEntry = Regex.Match(asmdefText, "(\"references\"\\s*:\\s*\\[\\s*\\r?\\n)([ \\t]*)\"");
            if (firstEntry.Success)
            {
                string entryIndent = firstEntry.Groups[2].Value;
                int insertAt = firstEntry.Index + firstEntry.Groups[1].Length;
                return asmdefText.Substring(0, insertAt)
                    + entryIndent + "\"" + assemblyName + "\"," + newline
                    + asmdefText.Substring(insertAt);
            }

            var inline = Regex.Match(asmdefText, "\"references\"\\s*:\\s*\\[\\s*\"");
            if (inline.Success)
            {
                int insertAt = inline.Index + inline.Length - 1;
                return asmdefText.Substring(0, insertAt)
                    + "\"" + assemblyName + "\", "
                    + asmdefText.Substring(insertAt);
            }

            return asmdefText;
        }

        private static string DetectIndent(string text)
        {
            var match = Regex.Match(text, "\\r?\\n([ \\t]+)\"");
            return match.Success ? match.Groups[1].Value : "  ";
        }

        private static string ProjectRoot() => Directory.GetParent(Application.dataPath).FullName;
    }
}
#endif
