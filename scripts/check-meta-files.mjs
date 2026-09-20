#!/usr/bin/env node
// Copyright (c) 2026 Yaniv Kalfa. All Rights Reserved.
/**
 * CI gate: every Unity-visible tracked asset carries a tracked `.meta`, every tracked `.meta` has an
 * asset, and no two metas claim the same guid. Sibling of check-machine-paths.mjs, same shape: data
 * over code, reasons over rules.
 *
 * WHY THIS EXISTS: Unity cannot write a `.meta` into an IMMUTABLE package. Install this package by
 * git URL and it lands in `Library/PackageCache`, read-only, so a `.cs` file with no committed meta
 * is never imported -- its assembly definition fails to compile, and a failed assembly means the
 * editor loads NONE of the package's assemblies. The package is simply dead, with an error that
 * names a missing type rather than a missing file. Three Builder files shipped that way (#256);
 * nobody noticed, because embedded and Asset Store installs are both writable and Unity silently
 * generates the metas there. The blind spot is structural, so the answer is a gate, not more care.
 *
 * WHAT COUNTS AS AN ASSET. Unity's own hidden-name rules decide, and they are the only exemption
 * mechanism this gate has or needs: a path segment that starts with `.`, ends with `~`, ends with
 * `.tmp`, or is named `cvs` is invisible to the Asset Database, and so is everything beneath it.
 * That is exactly why this repo's tooling lives under `SourceGenerator~/`, `ide-extensions~/` and
 * friends. Anything else that is tracked IS imported and therefore needs a meta -- including
 * DIRECTORIES, which is the half that hand-checking always misses (a folder whose files all have
 * metas can still be missing its own).
 *
 * Directories are derived from every tracked path, including hidden FILES: a folder kept alive by a
 * lone `.gitkeep` is still a visible folder to Unity and still needs its meta. `Shared/Elements/
 * Pools` is the live example -- git tracks only its dotfile, Unity imports the folder.
 *
 * GUIDS are checked for collisions as well as for existence, because the cheap way to add a meta is
 * to copy the one next door, and Unity resolves a guid to exactly one asset -- so the loser of a
 * duplicate silently drops every reference pointing at it. `--fix` mints 128 random bits and checks
 * them against every guid already in the repo.
 *
 * GUID CHURN is the reason to do this once. A committed GUID becomes canonical, and a developer
 * with the repo embedded already has a locally generated meta with a DIFFERENT guid, which this
 * overwrites on their next pull. Harmless for plain classes (nothing can reference them by guid),
 * which is what these are -- but it is why the gate exists rather than a periodic sweep.
 *
 *   node scripts/check-meta-files.mjs           check (CI gate; exit 1 on violation)
 *   node scripts/check-meta-files.mjs --list    print every visible asset and its meta verdict
 *   node scripts/check-meta-files.mjs --fix     write the missing metas, then re-check
 *
 * `--fix` never invents an importer. It copies the importer block from the metas this repo already
 * has for that extension (most common wins), so generated metas match what Unity itself wrote next
 * door, and a new extension teaches the script its shape by example rather than by a table here
 * that would drift from Unity's actual behaviour.
 */
import { readFileSync, writeFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { resolve, dirname, extname } from 'node:path';
import { fileURLToPath } from 'node:url';

const REPO_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');

const args = process.argv.slice(2);
const LIST_MODE = args.includes('--list');
const FIX_MODE = args.includes('--fix');

/** Unity's Asset Database hidden-name rules. A hidden segment hides everything under it. */
const isHiddenSegment = (seg) => {
	const lower = seg.toLowerCase();
	return seg.startsWith('.') || seg.endsWith('~') || lower === 'cvs' || lower.endsWith('.tmp');
};
const isVisible = (path) => !path.split('/').some(isHiddenSegment);

const tracked = execFileSync('git', ['ls-files', '-z'], { cwd: REPO_ROOT, maxBuffer: 64 * 1024 * 1024 })
	.toString('utf8')
	.split('\0')
	.filter(Boolean);

const trackedSet = new Set(tracked);

/**
 * `assets` holds every path Unity imports: visible files, plus every visible ancestor directory of
 * any tracked path. The ancestor walk deliberately runs over hidden files too -- see the header.
 */
const assets = new Set();
const metaTargets = new Set();

for (const path of tracked) {
	if (path.endsWith('.meta')) {
		const target = path.slice(0, -'.meta'.length);
		if (isVisible(target)) metaTargets.add(target);
		continue;
	}
	if (isVisible(path)) assets.add(path);
	const segments = path.split('/');
	for (let i = 1; i < segments.length; i++) {
		const dir = segments.slice(0, i).join('/');
		if (isVisible(dir)) assets.add(dir);
	}
}

/** A visible asset that git does not track as a file is a directory the ancestor walk derived. */
const directories = new Set([...assets].filter((a) => !trackedSet.has(a)));

/** Hoisted above the check/fix body: the fix path calls learnImporterBlocks() at module top level. */
let importerBlockCache = null;

const missing = [...assets].filter((a) => !trackedSet.has(a + '.meta')).sort();
const orphaned = [...metaTargets].filter((t) => !assets.has(t)).sort();
const duplicated = findDuplicateGuids();

if (LIST_MODE) {
	for (const asset of [...assets].sort()) {
		const kind = directories.has(asset) ? 'dir ' : 'file';
		const verdict = trackedSet.has(asset + '.meta') ? 'ok' : 'MISSING .meta';
		console.error(`${kind}  ${asset}  [${verdict}]`);
	}
	for (const target of orphaned) console.error(`meta  ${target}.meta  [ORPHAN - no such asset]`);
	for (const [guid, holders] of duplicated) console.error(`guid  ${guid}  [DUPLICATE]  ${holders.join(', ')}`);
	console.error(
		`\n${assets.size} visible asset(s); ${missing.length} missing, ${orphaned.length} orphaned, ${duplicated.length} duplicate guid(s).`
	);
	process.exit(0);
}

if (FIX_MODE && missing.length > 0) {
	const taken = new Set([...allGuids().keys()]);
	for (const asset of missing) {
		const body = importerBlockFor(asset);
		let guid = newGuid();
		while (taken.has(guid)) guid = newGuid();
		taken.add(guid);
		writeFileSync(resolve(REPO_ROOT, asset + '.meta'), `fileFormatVersion: 2\nguid: ${guid}\n${body}`, 'utf8');
		console.error(`  wrote ${asset}.meta`);
	}
	console.error(`\n${missing.length} meta file(s) written. Stage them, then re-run without --fix.`);
	process.exit(0);
}

if (missing.length === 0 && orphaned.length === 0 && duplicated.length === 0) {
	console.error(`✓ every Unity-visible tracked asset has a tracked .meta (${assets.size} checked)`);
	process.exit(0);
}

if (missing.length > 0) {
	console.error('✗ Unity-visible tracked assets with no tracked .meta:\n');
	for (const asset of missing) console.error(`  ${directories.has(asset) ? '[dir] ' : '[file]'} ${asset}`);
}
if (orphaned.length > 0) {
	console.error('\n✗ tracked .meta files whose asset is not tracked:\n');
	for (const target of orphaned) console.error(`  ${target}.meta`);
}
if (duplicated.length > 0) {
	console.error('\n✗ the same guid is claimed by more than one asset:\n');
	for (const [guid, holders] of duplicated) console.error(`  ${guid}\n${holders.map((h) => `      ${h}`).join('\n')}`);
}
console.error(`
${missing.length} missing, ${orphaned.length} orphaned, ${duplicated.length} duplicate guid(s).

  MISSING    Unity cannot generate a .meta inside an immutable package (Library/PackageCache), so a
             .cs file without one never compiles and takes every other package assembly down with it.
             Run \`node scripts/check-meta-files.mjs --fix\` and commit the result -- or, if the file
             is TOOLING rather than shipped content, move it under a \`~\` folder where Unity will not
             look for it at all (see the "~ folder convention" section of CLAUDE.md).
  ORPHAN     the asset was deleted but its .meta was left behind. Delete the .meta.
  DUPLICATE  a .meta was copied instead of generated. Unity resolves a guid to exactly one asset, so
             one of the claimants loses every reference pointing at it. Give the newer one a fresh
             guid -- 32 random hex characters.`);
process.exit(1);

function newGuid() {
	return randomBytes(16).toString('hex');
}

/** guid -> the tracked meta files claiming it. --fix keeps its own set for the guids it is minting. */
function allGuids() {
	const byGuid = new Map();
	for (const path of tracked) {
		if (!path.endsWith('.meta')) continue;
		let text;
		try {
			text = readFileSync(resolve(REPO_ROOT, path), 'utf8');
		} catch {
			continue;
		}
		const guid = /^guid:\s*([0-9a-fA-F]+)\s*$/m.exec(text)?.[1]?.toLowerCase();
		if (!guid) continue;
		if (!byGuid.has(guid)) byGuid.set(guid, []);
		byGuid.get(guid).push(path);
	}
	return byGuid;
}

function findDuplicateGuids() {
	return [...allGuids().entries()].filter(([, holders]) => holders.length > 1).sort((a, b) => a[0].localeCompare(b[0]));
}

/**
 * The importer block (everything after the guid line) for an asset, learned from the metas this
 * repo already has. Directories are their own case because `folderAsset: yes` sits ABOVE the
 * importer name, so a folder meta is not just a DefaultImporter meta.
 */
function importerBlockFor(asset) {
	const key = directories.has(asset) ? '<dir>' : extname(asset).toLowerCase();
	const learned = learnImporterBlocks().get(key);
	if (learned) return learned;
	return 'DefaultImporter:\n  externalObjects: {}\n  userData:\n  assetBundleName:\n  assetBundleVariant:\n';
}

function learnImporterBlocks() {
	if (importerBlockCache) return importerBlockCache;
	const tally = new Map();
	for (const path of tracked) {
		if (!path.endsWith('.meta')) continue;
		const target = path.slice(0, -'.meta'.length);
		if (!assets.has(target)) continue;
		let text;
		try {
			text = readFileSync(resolve(REPO_ROOT, path), 'utf8');
		} catch {
			continue;
		}
		const lines = text.replace(/\r\n/g, '\n').split('\n');
		const guidAt = lines.findIndex((l) => l.startsWith('guid:'));
		if (guidAt < 0) continue;
		const block = lines
			.slice(guidAt + 1)
			.map((l) => l.replace(/[ \t]+$/, ''))
			.join('\n')
			.replace(/\n+$/, '\n');
		if (!block.trim()) continue;
		const key = directories.has(target) ? '<dir>' : extname(target).toLowerCase();
		if (!tally.has(key)) tally.set(key, new Map());
		const counts = tally.get(key);
		counts.set(block, (counts.get(block) ?? 0) + 1);
	}
	importerBlockCache = new Map();
	for (const [key, counts] of tally) {
		const best = [...counts.entries()].sort((a, b) => b[1] - a[1])[0];
		importerBlockCache.set(key, best[0]);
	}
	return importerBlockCache;
}
