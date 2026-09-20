#!/usr/bin/env node
// Copyright (c) 2026 Yaniv Kalfa. All Rights Reserved.
/**
 * CI gate: no tracked C# rewrites a path's separators to Windows backslashes.
 *
 * WHY THIS EXISTS: #254 was `RegistryFolder.Replace('/', '\\')` before Path.Combine. On Windows a
 * no-op; on macOS and Linux `\` is an ordinary filename character, so Directory.CreateDirectory
 * made ONE directory literally named `Assets\Ruitk\Resources` while AssetDatabase.CreateAsset went
 * on expecting the folder tree. The asset registry was never written and every Asset<T>()/Ast<T>()
 * and @uss lookup returned null in player builds -- on the two platforms this project cannot test.
 * Auditing for the pattern turned up a second, unreported instance the same afternoon
 * (UitkxCsprojPostprocessor turning "/Users/..." into "\Users\..."), which is the whole argument for
 * a gate: the defect is mechanical, invisible on Windows, and nobody finds the second one.
 *
 * THE RULE. Exactly one direction is banned: forward slash TO backslash. Its opposite
 * (`Replace('\\', '/')`, normalising to Unity's asset-path form) is correct and ubiquitous here, as
 * is `Replace('/', Path.DirectorySeparatorChar)`, which asks the platform instead of assuming it.
 * Backslash is never the right literal answer: every .NET path API and every Unity API accepts
 * forward slashes on every platform Unity runs on, Windows included.
 *
 * SCOPE is the shipped package -- the Unity-visible roots plus the `~` trees that are compiled and
 * run (source generator, language lib, LSP server). Test trees are in scope too: a fixture that
 * builds a Windows path to assert Windows-path handling is the thing under test, so it carries an
 * inline opt-out rather than a blanket exemption.
 *
 * A single line opts out with a trailing `separator-gate-allow: <reason>` comment -- for the one
 * legitimate case, code that goes looking for damage a previous version did. The reason lives next
 * to the code, which beats a central list nobody re-reads.
 *
 *   node scripts/check-path-separators.mjs           check (CI gate; exit 1 on violation)
 *   node scripts/check-path-separators.mjs --list    print every hit, with verdict
 */
import { readFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const REPO_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');

const LIST_MODE = process.argv.slice(2).includes('--list');

/**
 * `Replace('/', '\\')` and `Replace("/", "\\")` in C# source, written here against the file's own
 * bytes: a char literal is `'\\'` (four characters on disk) and a string literal is `"\\\\"` (six).
 */
const TO_BACKSLASH = [
	/\.Replace\(\s*'\/'\s*,\s*'\\\\'\s*\)/g,
	/\.Replace\(\s*"\/"\s*,\s*"\\\\\\\\"\s*\)/g,
];

const INLINE_ALLOW = /separator-gate-allow:\s*\S/;

const tracked = execFileSync('git', ['ls-files', '-z', '*.cs'], { cwd: REPO_ROOT, maxBuffer: 64 * 1024 * 1024 })
	.toString('utf8')
	.split('\0')
	.filter(Boolean);

const violations = [];
const listed = [];

for (const file of tracked) {
	let text;
	try {
		text = readFileSync(resolve(REPO_ROOT, file), 'utf8');
	} catch {
		continue;
	}
	const lines = text.split(/\r?\n/);
	for (let i = 0; i < lines.length; i++) {
		const line = lines[i];
		const hits = TO_BACKSLASH.flatMap((re) => [...line.matchAll(re)]);
		if (hits.length === 0) continue;
		const allowed = INLINE_ALLOW.test(line);
		if (LIST_MODE) listed.push({ file, line: i + 1, text: line.trim(), allowed });
		if (!allowed) violations.push({ file, line: i + 1, text: line.trim() });
	}
}

if (LIST_MODE) {
	for (const l of listed) console.error(`${l.file}:${l.line}  [${l.allowed ? 'exempt (inline)' : 'VIOLATION'}]  ${l.text}`);
	console.error(`\n${listed.length} hit(s); ${violations.length} would fail the gate.`);
	process.exit(0);
}

if (violations.length === 0) {
	console.error(`✓ no path separator rewritten to backslash in tracked C# (${tracked.length} files)`);
	process.exit(0);
}

console.error('✗ path separators rewritten to Windows backslashes:\n');
for (const v of violations) console.error(`  ${v.file}:${v.line}\n      ${v.text}`);
console.error(`
${violations.length} violation(s).

On macOS and Linux a backslash is an ordinary filename character, so this does not build a path --
it builds one long filename, and the code around it keeps working with the path it meant to build.
The failure is silent and lands only on the platforms with no CI coverage.

  Combining an asset path with a root  ->  Path.Combine(root, "Assets/Ruitk/Resources"). Every .NET
                                           and Unity path API takes forward slashes on Windows too.
  A path you must hand to a native     ->  Path.GetFullPath(p), which already returns the platform's
  tool in its own spelling                 separator, or Replace('/', Path.DirectorySeparatorChar).

If a line genuinely means backslash -- code looking for damage an earlier version did, or a fixture
asserting Windows-path handling -- end it with a \`separator-gate-allow: <reason>\` comment.`);
process.exit(1);
