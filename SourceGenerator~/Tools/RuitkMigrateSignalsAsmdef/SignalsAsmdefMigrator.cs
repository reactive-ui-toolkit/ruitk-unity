using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Ruitk.SourceGenerator.Tools
{
    /// <summary>
    /// Adds a <c>Ruitk.Signals</c> reference to the assembly definitions that need one
    /// after the 0.21.0 split, and to no others.
    /// </summary>
    public static class SignalsAsmdefMigrator
    {
        public const string SignalsAssembly = "Ruitk.Signals";

        /// <summary>
        /// Assemblies that used to carry the signal types. An asmdef referencing none of
        /// them could not have named a signal type before the split either, so it cannot
        /// be broken by the split and is left alone.
        /// </summary>
        private static readonly string[] FormerCarriers =
        {
            "Ruitk.Shared",
            "Ruitk.Runtime",
            "Ruitk.Ugui",
            "Ruitk.Editor",
        };

        /// <summary>
        /// Naming any of these needs the reference, in any file type.
        /// </summary>
        private static readonly Regex NamesSignalType = new Regex(
            @"(\bSignal<|\bSignalFactory\b|\bSignalsRuntime\b|\bSignalBase\b|\bSignalUpdate\b|\bSignalRegistry\b|\bSignalSubscription\b|\busing\s+Ruitk\.Signals\b|@Ruitk\.Signals)",
            RegexOptions.Compiled);

        /// <summary>
        /// A <c>.uitkx</c> file that calls the lowercase <c>useSignal</c> wrapper needs the
        /// reference even when it never spells a signal type: the generator emits that
        /// wrapper with a <c>Ruitk.Signals.Signal&lt;T&gt;</c> parameter, so
        /// <c>useSignal("key", 0)</c> pulls the dependency in through generated code the
        /// author never sees.
        ///
        /// The same is NOT true of C#. <c>Hooks.UseSignal("key", 0)</c> resolves to an
        /// overload whose signature mentions nothing from the new assembly, and Roslyn does
        /// not demand the reference for overload resolution -- verified by compiling
        /// Ruitk.Ugui.Tests, which does exactly that and needs no reference. Matching it
        /// here would add references that are not needed, and an unnecessary reference is
        /// precisely the coupling this split exists to remove.
        /// </summary>
        private static readonly Regex CallsLowercaseUseSignal = new Regex(
            @"\buseSignal\s*(<|\()",
            RegexOptions.Compiled);

        public sealed class Result
        {
            public readonly List<string> Changed = new List<string>();
            public readonly List<string> AlreadyCorrect = new List<string>();
            public readonly List<string> NotAffected = new List<string>();
            public readonly List<string> GuidReferences = new List<string>();
            public readonly List<string> Failed = new List<string>();
        }

        public static Result Run(string projectRoot, bool dryRun)
        {
            var result = new Result();

            foreach (string asmdef in EnumerateAsmdefs(projectRoot))
            {
                try
                {
                    string text = File.ReadAllText(asmdef);

                    if (!ReferencesAnyFormerCarrier(text))
                    {
                        // A project may reference by GUID instead of by name, which cannot be
                        // matched textually. Say so rather than silently reporting "fine".
                        if (text.IndexOf("\"GUID:", StringComparison.Ordinal) >= 0)
                        {
                            result.GuidReferences.Add(asmdef);
                        }
                        else
                        {
                            result.NotAffected.Add(asmdef);
                        }
                        continue;
                    }
                    if (AlreadyReferences(text, SignalsAssembly))
                    {
                        result.AlreadyCorrect.Add(asmdef);
                        continue;
                    }
                    if (!AnySourceNeedsSignals(projectRoot, Path.GetDirectoryName(asmdef)!))
                    {
                        result.NotAffected.Add(asmdef);
                        continue;
                    }

                    string updated = InsertReference(text, SignalsAssembly);
                    if (updated == text)
                    {
                        result.Failed.Add(asmdef + " (could not locate the references array)");
                        continue;
                    }
                    if (!dryRun)
                    {
                        File.WriteAllText(asmdef, updated, EncodingOf(asmdef));
                    }
                    result.Changed.Add(asmdef);
                }
                catch (Exception ex)
                {
                    result.Failed.Add(asmdef + " (" + ex.Message + ")");
                }
            }

            return result;
        }

        private static IEnumerable<string> EnumerateAsmdefs(string root)
        {
            foreach (string file in Directory.EnumerateFiles(root, "*.asmdef", SearchOption.AllDirectories))
            {
                if (IsSkippedPath(root, file))
                {
                    continue;
                }
                yield return file;
            }
        }

        /// <summary>
        /// Never edits the package's own assembly definitions, nor anything Unity or a build
        /// system owns. A user with the package embedded under Packages/ would otherwise have
        /// the codemod "fix" the very assemblies that define the split.
        /// </summary>
        private static bool IsSkippedPath(string root, string path)
        {
            // Judged RELATIVE to the scanned root. Judging the absolute path would skip any
            // project that merely happens to live under a folder called Temp or bin -- which
            // is exactly what happened the first time this ran from a scratch directory.
            string relative;
            try { relative = Path.GetRelativePath(root, path); }
            catch { relative = path; }
            foreach (string segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                if (segment.Equals("com.reactiveuitoolkit", StringComparison.OrdinalIgnoreCase)
                    || segment.Equals("ReactiveUIToolkit", StringComparison.OrdinalIgnoreCase)
                    || segment.Equals("Library", StringComparison.OrdinalIgnoreCase)
                    || segment.Equals("Temp", StringComparison.OrdinalIgnoreCase)
                    || segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
                    || segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
                    || segment.Equals(".git", StringComparison.OrdinalIgnoreCase)
                    || segment.EndsWith("~", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool ReferencesAnyFormerCarrier(string asmdefText)
        {
            foreach (string carrier in FormerCarriers)
            {
                if (AlreadyReferences(asmdefText, carrier))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool AlreadyReferences(string asmdefText, string assemblyName) =>
            asmdefText.IndexOf("\"" + assemblyName + "\"", StringComparison.Ordinal) >= 0;

        internal static bool NeedsSignals(string text, bool isUitkx) =>
            NamesSignalType.IsMatch(text) || (isUitkx && CallsLowercaseUseSignal.IsMatch(text));

        private static bool AnySourceNeedsSignals(string root, string asmdefDir)
        {
            foreach (string file in Directory.EnumerateFiles(asmdefDir, "*.*", SearchOption.AllDirectories))
            {
                string extension = Path.GetExtension(file);
                bool isUitkx = extension.Equals(".uitkx", StringComparison.OrdinalIgnoreCase);
                if (!isUitkx && !extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (IsSkippedPath(root, file))
                {
                    continue;
                }
                // A nested assembly definition owns its own subtree; those sources belong to
                // that assembly, not this one, and it gets its own visit.
                if (OwnedByANestedAsmdef(file, asmdefDir))
                {
                    continue;
                }
                try
                {
                    if (NeedsSignals(File.ReadAllText(file), isUitkx))
                    {
                        return true;
                    }
                }
                catch
                {
                    // An unreadable source is the user's problem to see in Unity, not a reason
                    // to abandon the migration of every other assembly.
                }
            }
            return false;
        }

        private static bool OwnedByANestedAsmdef(string file, string asmdefDir)
        {
            string? dir = Path.GetDirectoryName(file);
            while (dir != null && !PathsEqual(dir, asmdefDir))
            {
                if (Directory.EnumerateFiles(dir, "*.asmdef", SearchOption.TopDirectoryOnly).Any())
                {
                    return true;
                }
                dir = Path.GetDirectoryName(dir);
            }
            return false;
        }

        private static bool PathsEqual(string a, string b) =>
            string.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Textual insertion, matching the file's own indentation. Round-tripping through a
        /// JSON serializer would reformat the whole file and bury a one-line change in a
        /// whole-file diff -- these are hand-written and hand-read files.
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

        private static Encoding EncodingOf(string path)
        {
            using var stream = File.OpenRead(path);
            var bom = new byte[3];
            int read = stream.Read(bom, 0, 3);
            bool hasBom = read == 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF;
            return new UTF8Encoding(hasBom);
        }
    }
}
