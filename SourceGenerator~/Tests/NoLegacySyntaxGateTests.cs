using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Ruitk.SourceGenerator.Tests;

/// <summary>
/// The 0.16.0 no-legacy-left repo gate (removal plan §7): no shipped <c>.uitkx</c> under
/// <c>Samples/</c> may carry a wrapper-keyword declaration head — a reintroduced legacy
/// sample (revert, cherry-pick, doc-example copy) would hand every package consumer a hard
/// <c>UITKX2320</c> error. Line-anchored on the grammar's column-0 head rule, so the words
/// in comments, strings, or docs never false-match.
/// </summary>
public sealed class NoLegacySyntaxGateTests
{
    private static readonly Regex s_wrapperHead = new(
        @"^(?:export\s+)?(?:component|hook|module)\s+[A-Za-z_]",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static string WorkspaceRoot([CallerFilePath] string thisFile = "")
    {
        var dir = Path.GetDirectoryName(thisFile)!; // Tests/
        return Path.GetFullPath(Path.Combine(dir, "../.."));
    }

    [Fact]
    public void Samples_ContainNoWrapperKeywordHeads()
    {
        string samples = Path.Combine(WorkspaceRoot(), "Samples");
        Assert.True(Directory.Exists(samples), $"missing Samples/ at {samples}");

        var offenders = new List<string>();
        foreach (string file in Directory.EnumerateFiles(samples, "*.uitkx", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(file);
            var m = s_wrapperHead.Match(text);
            if (m.Success)
                offenders.Add($"{file}: '{m.Value.Trim()}'");
        }

        Assert.True(offenders.Count == 0,
            "wrapper-keyword declaration heads found in shipped samples (removed in 0.16.0 — "
            + "run the UitkxMigrateImports --es-modules codemod):\n"
            + string.Join("\n", offenders));
    }
}
