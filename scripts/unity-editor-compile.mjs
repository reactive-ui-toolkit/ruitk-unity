#!/usr/bin/env node
// Copyright (c) 2026 Yaniv Kalfa. All Rights Reserved.
//
// Compiles the WHOLE package the way a consumer's editor does: a throwaway Unity project
// that references this repo as a local package, imported in batch mode.
//
// Why this exists, next to unity-compile-check.mjs: that gate builds Shared/ + Runtime/ in a
// standalone csproj, which is fast and needs no license, but it cannot reach Editor/ or
// Builder/. Ruitk.Editor references Ruitk.Ugui, Ruitk.Ugui needs UnityEngine.UI and
// Unity.TextMeshPro, and those live in a PROJECT's package cache rather than the editor
// install -- so the cheap route stops at the assembly boundary. Everything under Editor/
// (HMR, the asset registry, the csproj postprocessor) and everything under Builder/
// therefore has zero compile coverage. Both of #254's instances were in Editor/.
//
// It also checks something a csproj never could: that every asmdef in the repo actually
// PRODUCED an assembly. Unity skips a script with no .meta inside an immutable package, and
// the asmdef then fails silently -- which is #256, and which shows up here as a missing DLL.
//
// Unity is DISCOVERED, never hardcoded: $RUITK_UNITY_EXE (CI sets this to `unity-editor`
// inside the game-ci image) -> .ruitk-local.json "unityEditor" -> the standard Hub roots.
//
//   node scripts/unity-editor-compile.mjs            compile, reusing the shell project
//   node scripts/unity-editor-compile.mjs --clean    delete the shell project first
//   node scripts/unity-editor-compile.mjs --print-layout
//                                                    also dump where the editor keeps its
//                                                    bundled runtime -- the #255 question,
//                                                    answered by the platform rather than guessed
//   node scripts/unity-editor-compile.mjs --allow-missing
//                                                    skip (exit 0) when no Unity is installed

import { spawnSync } from 'node:child_process'
import { existsSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { basename, dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const args = process.argv.slice(2)
const CLEAN = args.includes('--clean')
const PRINT_LAYOUT = args.includes('--print-layout')
const ALLOW_MISSING = args.includes('--allow-missing')

const shellProject = join(tmpdir(), 'ruitk-editor-compile')

// ---------------------------------------------------------------------------
// Unity discovery
// ---------------------------------------------------------------------------

function localConfigEditor() {
  try {
    const cfg = JSON.parse(readFileSync(join(repoRoot, '.ruitk-local.json'), 'utf8'))
    return cfg.unityEditor || null
  } catch {
    return null
  }
}

function hubRoots() {
  if (process.platform === 'win32') {
    return ['C:\\Program Files\\Unity\\Hub\\Editor', 'C:\\Program Files (x86)\\Unity\\Hub\\Editor']
  }
  if (process.platform === 'darwin') return ['/Applications/Unity/Hub/Editor']
  return [join(process.env.HOME ?? '', 'Unity/Hub/Editor'), '/opt/unity/Hub/Editor']
}

function executableIn(versionDir) {
  const candidates =
    process.platform === 'win32'
      ? [join(versionDir, 'Editor', 'Unity.exe')]
      : process.platform === 'darwin'
        ? [join(versionDir, 'Unity.app', 'Contents', 'MacOS', 'Unity')]
        : [join(versionDir, 'Editor', 'Unity')]
  return candidates.find((c) => existsSync(c)) ?? null
}

function discoverUnity() {
  const fromEnv = process.env.RUITK_UNITY_EXE
  if (fromEnv) return { exe: fromEnv, version: null, how: '$RUITK_UNITY_EXE' }

  const fromConfig = localConfigEditor()
  if (fromConfig && existsSync(fromConfig)) {
    return { exe: fromConfig, version: null, how: '.ruitk-local.json "unityEditor"' }
  }

  for (const root of hubRoots().filter(existsSync)) {
    const versions = readdirSync(root)
      .filter((v) => existsSync(join(root, v)))
      .sort()
      .reverse()
    for (const version of versions) {
      const exe = executableIn(join(root, version))
      if (exe) return { exe, version, how: `${root} (${version})` }
    }
  }
  return null
}

// ---------------------------------------------------------------------------
// Shell project
// ---------------------------------------------------------------------------

/** Every assembly the repo's asmdefs should produce, derived rather than listed. */
function expectedAssemblies() {
  const out = []
  const walk = (dir) => {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      if (entry.name.startsWith('.') || entry.name.endsWith('~')) continue
      const full = join(dir, entry.name)
      if (entry.isDirectory()) walk(full)
      else if (entry.name.endsWith('.asmdef')) {
        try {
          const name = JSON.parse(readFileSync(full, 'utf8')).name
          if (name) out.push(name)
        } catch {
          // A malformed asmdef is Unity's problem to report, not this script's.
        }
      }
    }
  }
  walk(repoRoot)
  return out
}

/**
 * Every `com.unity.modules.*` package the discovered editor ships, at version 1.0.0 (the
 * version built-in modules always carry). Read from the editor install rather than listed
 * here, so a module added or renamed in a future Unity needs no edit.
 */
function builtInModules() {
  const roots = [
    resolve(unity.exe, '..', 'Data', 'Resources', 'PackageManager', 'BuiltInPackages'),
    resolve(unity.exe, '..', '..', 'Resources', 'PackageManager', 'BuiltInPackages'),
    resolve(unity.exe, '..', '..', 'Data', 'Resources', 'PackageManager', 'BuiltInPackages'),
  ]
  const root = roots.find(existsSync)
  if (!root) return {}
  const modules = {}
  for (const name of readdirSync(root)) {
    if (name.startsWith('com.unity.modules.') && !name.endsWith('.sha1')) modules[name] = '1.0.0'
  }
  return modules
}

function writeShellProject(version) {
  if (CLEAN && existsSync(shellProject)) rmSync(shellProject, { recursive: true, force: true })
  mkdirSync(join(shellProject, 'Assets'), { recursive: true })
  mkdirSync(join(shellProject, 'Packages'), { recursive: true })
  mkdirSync(join(shellProject, 'ProjectSettings'), { recursive: true })

  // A hand-written manifest REPLACES Unity's defaults, so every built-in module the package
  // touches has to be listed or its types vanish (Shared/Core/Media needs UnityEngine.Video,
  // which is how this was found). Enumerating the editor's own BuiltInPackages folder is the
  // only list that cannot drift from the editor in use -- the same "probe, never hardcode"
  // rule the rest of this repo's tooling follows.
  const declared = JSON.parse(readFileSync(join(repoRoot, 'package.json'), 'utf8')).dependencies ?? {}
  const dependencies = {
    'com.reactiveuitoolkit': `file:${repoRoot.replace(/\\/g, '/')}`,
    // NOT declared by package.json, and they should be -- see PKG-DEPS in
    // Plans~/REMAINING_WORK.md. Ruitk.Ugui and Ruitk.Samples reference UnityEngine.UI and
    // Unity.TextMeshPro (com.unity.ugui), and the Doom sample references Unity.InputSystem,
    // so a project without them gets assemblies that do not compile. Both are in every Unity 6
    // template, which is why nobody has hit it; this shell project models that typical
    // consumer rather than pretending the declaration is complete.
    'com.unity.ugui': '2.5.0',
    'com.unity.inputsystem': '1.20.0',
    ...builtInModules(),
    ...declared,
  }
  writeFileSync(join(shellProject, 'Packages', 'manifest.json'), JSON.stringify({ dependencies }, null, 2))

  if (version) {
    writeFileSync(join(shellProject, 'ProjectSettings', 'ProjectVersion.txt'), `m_EditorVersion: ${version}\n`)
  }
}

// ---------------------------------------------------------------------------
// Run
// ---------------------------------------------------------------------------

const unity = discoverUnity()
if (!unity) {
  const message =
    'No Unity installation found. Set $RUITK_UNITY_EXE, add "unityEditor" to .ruitk-local.json, ' +
    `or install an editor under one of: ${hubRoots().join(', ')}.`
  if (ALLOW_MISSING) {
    console.log(`- SKIPPED: ${message}`)
    console.log('- This check did NOT run.')
    process.exit(0)
  }
  console.error(`x ${message}`)
  process.exit(1)
}

console.log(`Unity: ${unity.how}`)
console.log(`  ${unity.exe}`)

writeShellProject(unity.version)
const logFile = join(shellProject, 'editor-compile.log')
if (existsSync(logFile)) rmSync(logFile)

console.log(`Shell project: ${shellProject}`)
console.log('Importing the package in batch mode (compiles every asmdef, Editor/ and Builder/ included)...')

const result = spawnSync(
  unity.exe,
  [
    '-batchmode',
    '-quit',
    '-nographics',
    '-disable-assembly-updater',
    '-projectPath',
    shellProject,
    '-logFile',
    logFile,
  ],
  { stdio: 'inherit' }
)

const log = existsSync(logFile) ? readFileSync(logFile, 'utf8') : ''
const compileErrors = log.split('\n').filter((l) => /\): error CS\d+/.test(l))

// Unity's batch-mode exit code is not a reliable signal for compile failure, so the log is
// the source of truth and the exit code is only reported alongside it.
if (compileErrors.length > 0) {
  console.error(`\nx ${compileErrors.length} compile error(s):\n`)
  for (const line of compileErrors.slice(0, 40)) console.error(`  ${line.trim()}`)
  if (compileErrors.length > 40) console.error(`  ... and ${compileErrors.length - 40} more`)
  console.error(`\nFull log: ${logFile}`)
  process.exit(1)
}

const scriptAssemblies = join(shellProject, 'Library', 'ScriptAssemblies')
const produced = existsSync(scriptAssemblies)
  ? new Set(readdirSync(scriptAssemblies).filter((f) => f.endsWith('.dll')).map((f) => basename(f, '.dll')))
  : new Set()

// Test-only assemblies need the test framework package, which this shell project does not
// pull in; they are Unity's to skip, not a defect here.
const missing = expectedAssemblies().filter((name) => !produced.has(name) && !name.endsWith('.Tests'))

if (missing.length > 0) {
  console.error('\nx asmdefs that produced no assembly:\n')
  for (const name of missing) console.error(`  ${name}`)
  console.error(
    '\nAn asmdef whose scripts did not all import produces nothing, silently. The usual cause is a\n' +
      'tracked .cs with no committed .meta -- run `node scripts/check-meta-files.mjs`.\n' +
      `Full log: ${logFile}`
  )
  process.exit(1)
}

if (result.status !== 0 && result.status !== null) {
  console.error(`\nx Unity exited ${result.status} with no compile errors in the log: ${logFile}`)
  process.exit(1)
}

console.log(`\n\u2713 unity editor compile: ${produced.size} assemblies built, ${missing.length} missing.`)

if (PRINT_LAYOUT) {
  console.log('\nBundled runtime layout (the #255 question, answered by this platform):')
  // The same candidate grid Ruitk.EditorSupport.UnityBundledRuntime walks, so what this
  // prints is what HMR will find. The editor executable sits at a different depth on each
  // platform (Editor/Unity.exe, Unity.app/Contents/MacOS/Unity, Editor/Unity), so the walk
  // starts from its directory and climbs rather than assuming one of them.
  const roots = [dirname(unity.exe), resolve(unity.exe, '..', '..'), resolve(unity.exe, '..', '..', '..')]
  const relatives = ['', 'Data', 'Resources/Scripting', 'Contents', 'Contents/Resources/Scripting']
  let found = 0
  for (const root of roots) {
    for (const relative of relatives) {
      const candidate = relative ? join(root, relative) : root
      const netCore = join(candidate, 'NetCoreRuntime')
      const hit = existsSync(netCore)
      if (hit) found++
      console.log(`  ${hit ? 'FOUND  ' : 'absent '} ${netCore}`)
    }
  }
  if (found === 0) {
    console.error('\nx no NetCoreRuntime under any candidate — HMR cannot start on this platform.')
    console.error('  Add the real location to UnityBundledRuntime.s_relativeRoots and to this list.')
    process.exit(1)
  }
}
