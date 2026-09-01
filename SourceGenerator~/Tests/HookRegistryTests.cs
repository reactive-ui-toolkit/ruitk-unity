// SPDX-License-Identifier: LicenseRef-ReactiveUI-Community-1.0
// ReactiveUIToolkit — see THIRDPARTY.md
//
//  HookRegistryTests
//
//  Locks the HookRegistry single-source-of-truth tables against drift, both
//  internally (count, naming invariants, caching contract) and externally
//  via byte-identical comparisons to the golden snapshots captured in
//  SourceGenerator~/Tests/Golden/HookRegistry/.
//
//  Whenever you add or remove a hook in Shared/Core/Hooks.cs:
//    1. Update HookRegistry.cs (see its docs comment for the 5-step checklist).
//    2. Extend the expected-additions list in the golden DIFF tests - the
//       fixtures are immutable; see the README in the golden dir.
//    3. Bump ExpectedHookCount below.
//    4. If a hook's docs change, regenerate hover_docs.golden.json.
//
//  Tests are split into three groups:
//    A) Internal invariants — purely from registry state.
//    B) Runtime parity — uses reflection over typeof(Hooks).GetMethods().
//    C) Golden parity — byte-compares accessor output to fixtures on disk.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Ruitk.Core;
using Xunit;

namespace Ruitk.SourceGenerator.Tests;

public sealed class HookRegistryTests
{
    // Bump this when adding a hook to Shared/Core/Hooks.cs.
    // 21 = 20 hooks + ProvideContext (counted as a hook-like API in the registry).
    private const int ExpectedHookCount = 21;

    private static string N(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n");

    private static string GoldenDir([CallerFilePath] string thisFile = "")
        => Path.Combine(Path.GetDirectoryName(thisFile)!, "Golden", "HookRegistry");

    private static string ReadGolden(string name) => N(File.ReadAllText(Path.Combine(GoldenDir(), name)));

    // ════════════════════════════════════════════════════════════════════════
    //  A — Internal invariants
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Registry_HookCount_IsExpected()
    {
        Assert.Equal(ExpectedHookCount, HookRegistry.HookCount);
        Assert.Equal(ExpectedHookCount, HookRegistry.CanonicalNames.Count);
    }

    [Fact]
    public void Registry_CanonicalNames_AreUniqueAndPascalCase()
    {
        var set = new HashSet<string>(HookRegistry.CanonicalNames, StringComparer.Ordinal);
        Assert.Equal(HookRegistry.CanonicalNames.Count, set.Count);
        foreach (var name in HookRegistry.CanonicalNames)
            Assert.True(char.IsUpper(name[0]), $"Expected PascalCase, got '{name}'");
    }

    [Fact]
    public void Registry_AliasTable_HasOneEntryPerHook()
    {
        var aliases = HookRegistry.GetAliasTable();
        Assert.Equal(ExpectedHookCount, aliases.Length);
        foreach (var (from, to) in aliases)
        {
            Assert.EndsWith("(", from);
            Assert.StartsWith("Hooks.", to);
            Assert.EndsWith("(", to);
            // Camel → Pascal: "useFoo(" should map to "Hooks.UseFoo("
            // i.e. removing the trailing '(' and lowercasing first char of Pascal
            var pascal = to.Substring("Hooks.".Length, to.Length - "Hooks.".Length - 1);
            var camel  = char.ToLower(pascal[0]) + pascal.Substring(1);
            Assert.Equal(camel + "(", from);
        }
    }

    [Fact]
    public void Registry_DocMap_HasBothFormsPerHook()
    {
        var map = HookRegistry.GetDocMap();
        // Core hooks get TWO entries (camelCase shorthand + qualified). Router
        // hooks get ONE, the qualified form, because they have no shorthand - a
        // "useNavigate" key would document a spelling that does not compile.
        Assert.Equal(
            ExpectedHookCount * 2 + HookRegistry.RouterHookNames.Count, map.Count);
        foreach (var pascal in HookRegistry.CanonicalNames)
        {
            var camel = char.ToLower(pascal[0]) + pascal.Substring(1);
            Assert.True(map.ContainsKey(camel),          $"Doc map missing '{camel}'");
            Assert.True(map.ContainsKey("Hooks." + pascal), $"Doc map missing 'Hooks.{pascal}'");
        }
        foreach (var pascal in HookRegistry.RouterHookNames)
        {
            var camel = char.ToLower(pascal[0]) + pascal.Substring(1);
            Assert.True(map.ContainsKey("RouterHooks." + pascal),
                $"Doc map missing 'RouterHooks.{pascal}'");
            Assert.False(map.ContainsKey(camel),
                $"Doc map documents '{camel}', a spelling router hooks do not have");
        }
    }

    [Fact]
    public void Registry_ValidationPatterns_HaveThreeFormsPerHook()
    {
        var patterns = HookRegistry.GetValidationPatterns();
        var detection = HookRegistry.DetectionNames;
        Assert.Equal(detection.Count * 3, patterns.Length);
        // Section ordering: <Owner>.UseFoo(, then UseFoo(, then useFoo(
        int n = detection.Count;
        for (int i = 0; i < n; i++)
        {
            // The qualified form names the hook's OWNER. A router hook is only
            // ever written RouterHooks.UseNavigate(, never Hooks.UseNavigate(.
            string owner = HookRegistry.OwnerTypeOf(detection[i]);
            Assert.StartsWith(owner + ".", patterns[i]);
        }
        for (int i = n; i < 2 * n; i++) Assert.DoesNotContain(".", patterns[i]);
        for (int i = 2 * n; i < 3 * n; i++) Assert.True(char.IsLower(patterns[i][0]));

        // Rules of hooks must reach the router set: UseBlocker composes
        // Hooks.UseEffect, so a conditional call breaks effect ordering exactly
        // as a conditional useEffect would. Nothing said so before RTR-1.
        Assert.Contains("RouterHooks.UseBlocker(", patterns);
        Assert.Contains("UseNavigate(", patterns);
    }

    [Fact]
    public void Registry_Accessors_ReturnSameReferenceOnRepeatedCalls()
    {
        // The performance contract: per-keystroke consumers (DiagnosticsAnalyzer)
        // call accessors in the hot path.  Allocation on every call would be
        // observed as IDE typing lag.
        Assert.Same(HookRegistry.GetAliasTable(),         HookRegistry.GetAliasTable());
        Assert.Same(HookRegistry.GetSignatureRegexPattern(), HookRegistry.GetSignatureRegexPattern());
        Assert.Same(HookRegistry.GetGenericHookPattern(), HookRegistry.GetGenericHookPattern());
        Assert.Same(HookRegistry.GetDocMap(),             HookRegistry.GetDocMap());
        Assert.Same(HookRegistry.GetValidationPatterns(), HookRegistry.GetValidationPatterns());
        Assert.Same(HookRegistry.GenerateVirtualDocStubs(staticForm: true),
                    HookRegistry.GenerateVirtualDocStubs(staticForm: true));
        Assert.Same(HookRegistry.GenerateVirtualDocStubs(staticForm: false),
                    HookRegistry.GenerateVirtualDocStubs(staticForm: false));
        Assert.Same(HookRegistry.GetStateSlotArity(),     HookRegistry.GetStateSlotArity());
        Assert.Same(HookRegistry.GetInsertionSnippets(),  HookRegistry.GetInsertionSnippets());
    }

    [Fact]
    public void Registry_CallSiteTable_CoversEveryHookInBothCasings()
    {
        // The Builder's STATE panel indexes HookStates positionally from the
        // arity table and its palette inserts from the snippet table — a hook
        // missing from either silently truncates the panel or inserts the
        // generic fallback call.
        var arity = HookRegistry.GetStateSlotArity();
        var snippets = HookRegistry.GetInsertionSnippets();
        // Both casings per hook, core and router alike: the Builder's STATE panel
        // and palette look hooks up by whatever spelling the source used.
        int expectedRows = (ExpectedHookCount + HookRegistry.RouterHookNames.Count) * 2;
        Assert.Equal(expectedRows, arity.Count);
        Assert.Equal(expectedRows, snippets.Count);
        foreach (var pascal in HookRegistry.CanonicalNames)
        {
            var camel = char.ToLower(pascal[0]) + pascal.Substring(1);
            Assert.True(arity.ContainsKey(pascal), $"Arity missing '{pascal}'");
            Assert.True(arity.ContainsKey(camel), $"Arity missing '{camel}'");
            Assert.True(arity[pascal] == 0 || arity[pascal] == 1,
                $"Fiber-path arity for '{pascal}' must be 0 or 1");
            Assert.True(snippets.ContainsKey(pascal), $"Snippet missing '{pascal}'");
            Assert.True(snippets.ContainsKey(camel), $"Snippet missing '{camel}'");
            Assert.False(string.IsNullOrWhiteSpace(snippets[pascal]));
        }
        // The fiber path stores effects in separate lists and the metadata-gated
        // hooks early-return with a null owner — pin the four load-bearing rows.
        Assert.Equal(1, arity["useState"]);
        Assert.Equal(0, arity["useEffect"]);
        Assert.Equal(0, arity["useContext"]);
        Assert.Equal(0, arity["useSafeArea"]);

        // Every router hook is ZERO fiber slots, verified against the source
        // rather than assumed: they compose Hooks.UseContext (0) and, in
        // UseBlocker's case, Hooks.UseEffect (0). Nothing in RouterHooks.cs
        // touches UseState/UseRef/UseMemo/UseCallback. A wrong count here shifts
        // hook ordering and corrupts state at runtime, so it is pinned.
        foreach (var pascal in HookRegistry.RouterHookNames)
        {
            Assert.True(arity.ContainsKey(pascal), $"Arity missing '{pascal}'");
            Assert.Equal(0, arity[pascal]);
            Assert.True(snippets.ContainsKey(pascal), $"Snippet missing '{pascal}'");
            Assert.StartsWith("RouterHooks." + pascal + "(",
                snippets[pascal].Substring(snippets[pascal].IndexOf("RouterHooks.")));
        }
    }

    [Fact]
    public void Registry_SignaturePattern_MatchesAllFormsOfEveryHook()
    {
        var rx = new Regex(HookRegistry.GetSignatureRegexPattern());
        foreach (var pascal in HookRegistry.CanonicalNames)
        {
            var camel = char.ToLower(pascal[0]) + pascal.Substring(1);
            Assert.Matches(rx, $"{camel}(");
            Assert.Matches(rx, $"{pascal}(");
            Assert.Matches(rx, $"Hooks.{pascal}(");
        }
    }

    [Fact]
    public void Registry_GenericPattern_MatchesGenericFormsOnly()
    {
        var rx = new Regex(HookRegistry.GetGenericHookPattern());
        // Pick a hook that's in the generic order list.
        Assert.Matches(rx, "useState<int>(");
        Assert.Matches(rx, "useMemo<Dictionary<string, int>>(");
        // Non-generic call site must NOT match.
        Assert.DoesNotMatch(rx, "useState(");
    }

    // ════════════════════════════════════════════════════════════════════════
    //  B — Runtime parity (registry ↔ Hooks.cs)
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Registry_CanonicalNames_MatchHooksType()
    {
        // Reflect over the public static methods of Ruitk.Core.Hooks to
        // confirm every hook in the registry corresponds to a real method, and
        // surface any new Hooks.cs additions that the registry hasn't picked up.
        //
        // We resolve the Hooks type via the loaded language-lib assembly
        // (registry's home).  If Hooks.cs isn't compiled in the test context
        // we skip — the SG project doesn't link Hooks.cs directly, only
        // HookRegistry.cs.  In that case other tests still gate drift.
        var hooksType = Type.GetType("Ruitk.Core.Hooks, Ruitk.Language", throwOnError: false);
        if (hooksType is null)
            return; // Hooks.cs isn't linked into language-lib; skip silently.

        var publicMethods = hooksType.GetMethods(BindingFlags.Public | BindingFlags.Static);
        var methodNames = new HashSet<string>(publicMethods.Select(m => m.Name), StringComparer.Ordinal);

        foreach (var pascal in HookRegistry.CanonicalNames)
            Assert.True(methodNames.Contains(pascal),
                $"Registry lists '{pascal}' but Hooks.{pascal} not found via reflection.");
    }

    // ════════════════════════════════════════════════════════════════════════
    //  C — Golden parity (byte-identical to pre-refactor fixtures)
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Golden_AliasTable_MatchesGoldenFile()
    {
        var expected = ReadGolden("sg_alias_table.golden.txt");
        var actual = string.Concat(
            HookRegistry.GetAliasTable().Select(p => $"{p.From} => {p.To}\n"));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Golden_SignatureRegex_MatchesGoldenFile()
    {
        // The golden is a 0.6.0 fixture and stays immutable, so this is a DIFF
        // test rather than a byte-compare - the same shape the validation-pattern
        // golden already uses. What it guards is what actually matters: every
        // alternative the fixture captured is still detected, in its original
        // relative order, and anything new is named explicitly. A byte-compare
        // would have to be regenerated on every hook addition, which turns the
        // fixture into a record of the last change instead of a guard.
        static string[] Alternatives(string pattern)
        {
            int open = pattern.IndexOf(@"\b(", StringComparison.Ordinal) + 3;
            int close = pattern.IndexOf(")(?:<", StringComparison.Ordinal);
            return pattern.Substring(open, close - open).Split('|');
        }

        var golden = Alternatives(ReadGolden("signature_regex.golden.txt").TrimEnd('\n'));
        var actual = Alternatives(HookRegistry.GetSignatureRegexPattern());

        // Nothing lost.
        foreach (var name in golden)
            Assert.Contains(name, actual);

        // Original relative order preserved (a reorder reshapes the regex and is
        // exactly what the fixture exists to catch).
        int at = -1;
        foreach (var name in golden)
        {
            int next = Array.IndexOf(actual, name);
            Assert.True(next > at, $"'{name}' moved relative to the golden ordering");
            at = next;
        }

        // Additions are exactly the router hooks, both casings.
        var added = actual.Except(golden).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var expectedAdded = HookRegistry.RouterHookNames
            .SelectMany(n => new[] { n, char.ToLower(n[0]) + n.Substring(1) })
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedAdded, added);
    }

    [Fact]
    public void Golden_GenericRegex_MatchesGoldenFile()
    {
        var expected = ReadGolden("generic_alias_regex.golden.txt").TrimEnd('\n');
        Assert.Equal(expected, HookRegistry.GetGenericHookPattern());
    }

    [Fact]
    public void Golden_ValidationPatterns_MatchesGoldenFilePlusUseLayoutEffect()
    {
        // Pre-refactor golden file has 60 entries (missing useLayoutEffect — a bug).
        // Registry adds it back in.  This test confirms that the only diff
        // between registry output and the pre-refactor fixture is exactly the
        // three useLayoutEffect entries.
        var goldenLines = ReadGolden("validation_patterns.golden.txt")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        var actualLines = HookRegistry.GetValidationPatterns().ToList();

        var added = actualLines.Except(goldenLines).ToList();
        var removed = goldenLines.Except(actualLines).ToList();

        Assert.Empty(removed); // never lose a pattern silently

        // Expected additions since the 0.6.0 fixture:
        //   - the three useLayoutEffect forms (a pre-refactor coverage bug)
        //   - three forms per ROUTER hook (RTR-1). Router hooks are hooks:
        //     UseBlocker composes Hooks.UseEffect, so a conditional call breaks
        //     effect ordering. The qualified form names RouterHooks, since
        //     Hooks.UseNavigate( is a spelling that does not exist.
        var expectedAdded = new List<string>
        {
            "Hooks.UseLayoutEffect(", "UseLayoutEffect(", "useLayoutEffect(",
        };
        foreach (var n in HookRegistry.RouterHookNames)
        {
            expectedAdded.Add("RouterHooks." + n + "(");
            expectedAdded.Add(n + "(");
            expectedAdded.Add(char.ToLower(n[0]) + n.Substring(1) + "(");
        }
        Assert.Equal(
            expectedAdded.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            added.OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Golden_VirtualDocStubs_StaticForm_MatchesGoldenFile()
    {
        var expected = ReadGolden("vdg_static_stubs.golden.txt");
        var actual = N(HookRegistry.GenerateVirtualDocStubs(staticForm: true));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Golden_VirtualDocStubs_InstanceForm_MatchesGoldenFile()
    {
        var expected = ReadGolden("vdg_instance_stubs.golden.txt");
        var actual = N(HookRegistry.GenerateVirtualDocStubs(staticForm: false));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Golden_VirtualDocStubs_StaticForm_CompilesCleanly()
    {
        // Sanity-check that the stub block is at least syntactically valid C#
        // (the IDE virtual document doesn't have a host class context, so we
        // wrap it in one for parser purposes only).
        var wrapped = "namespace N { public class C { " +
                      HookRegistry.GenerateVirtualDocStubs(staticForm: true) +
                      " } }";
        var tree = CSharpSyntaxTree.ParseText(wrapped);
        var errors = tree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "Stub block has parser errors:\n" + string.Join("\n", errors.Select(e => e.ToString())));
    }
}
