#!/usr/bin/env node
// Copyright (c) 2026 Yaniv Kalfa. All Rights Reserved.
//
// Builds a shell project that references this package with the IL2CPP backend, which is the
// only way to see an IL2CPP-only defect. #253 was one: a generic tracker whose state
// parameter was constrained to an interface with no `class`, assigned through. Mono and
// plain Roslyn compile it; IL2CPP's generic sharing refuses it and the build stops. Every
// suite in this repository runs on Mono or Roslyn, so nothing could have caught it.
//
// Windows IL2CPP rather than WebGL: the defect is in generic sharing, which is backend-wide,
// and a Windows player builds in a fraction of WebGL's time. WebGL is where it was reported,
// not where it lives.
//
// The shell project gets a probe script that calls ElementRegistryProvider.GetDefaultRegistry(),
// which constructs every element adapter by hand. That is what roots the tracker's generic
// instantiation -- without it, managed stripping removes the adapters, IL2CPP never emits the
// code under test, and the build passes for the wrong reason.
//
// Unity is DISCOVERED, never hardcoded: $RUITK_UNITY_EXE -> .ruitk-local.json "unityEditor"
// -> the standard Hub roots.
//
//   node scripts/unity-il2cpp-check.mjs                    build this working tree
//   node scripts/unity-il2cpp-check.mjs --package <path>   build some other checkout of the
//                                                          package (used to prove a fix by
//                                                          building the commit before it)
//   node scripts/unity-il2cpp-check.mjs --expect-failure    invert the exit code, for that
//                                                          "before" build
//   node scripts/unity-il2cpp-check.mjs --clean            discard the shell project first

import { spawnSync } from 'node:child_process'
import { existsSync, mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const args = process.argv.slice(2)
const CLEAN = args.includes('--clean')
const EXPECT_FAILURE = args.includes('--expect-failure')

const packageIndex = args.indexOf('--package')
const packagePath = packageIndex >= 0 ? resolve(args[packageIndex + 1]) : repoRoot

const shellProject = join(tmpdir(), 'ruitk-il2cpp-check')

// ---------------------------------------------------------------------------
// Unity discovery (same chain as unity-editor-compile.mjs)
// ---------------------------------------------------------------------------

function localConfigEditor() {
  try {
    return JSON.parse(readFileSync(join(repoRoot, '.ruitk-local.json'), 'utf8')).unityEditor || null
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
    for (const version of readdirSync(root).sort().reverse()) {
      const exe = executableIn(join(root, version))
      if (exe) return { exe, version, how: `${root} (${version})` }
    }
  }
  return null
}

// ---------------------------------------------------------------------------
// Shell project
// ---------------------------------------------------------------------------

function builtInModules(unityExe) {
  const roots = [
    resolve(unityExe, '..', 'Data', 'Resources', 'PackageManager', 'BuiltInPackages'),
    resolve(unityExe, '..', '..', 'Resources', 'PackageManager', 'BuiltInPackages'),
    resolve(unityExe, '..', '..', 'Data', 'Resources', 'PackageManager', 'BuiltInPackages'),
  ]
  const root = roots.find(existsSync)
  if (!root) return {}
  const modules = {}
  for (const name of readdirSync(root)) {
    if (name.startsWith('com.unity.modules.') && !name.endsWith('.sha1')) modules[name] = '1.0.0'
  }
  return modules
}

// Rooting the adapters is the whole point -- see the header. GetDefaultRegistry constructs
// MultiColumnListViewElementAdapter, whose Cached field holds the generic tracker.
const PROBE_SOURCE = `using UnityEngine;

public sealed class RuitkIl2cppProbe : MonoBehaviour
{
    private void Awake()
    {
        var registry = Ruitk.Elements.ElementRegistryProvider.GetDefaultRegistry();
        Debug.Log("[RuitkIl2cppProbe] adapters: " + registry.RegisteredNames.Count);
    }
}
`

function writeShellProject(unity) {
  if (CLEAN && existsSync(shellProject)) rmSync(shellProject, { recursive: true, force: true })
  mkdirSync(join(shellProject, 'Assets'), { recursive: true })
  mkdirSync(join(shellProject, 'Packages'), { recursive: true })
  mkdirSync(join(shellProject, 'ProjectSettings'), { recursive: true })

  const declared = JSON.parse(readFileSync(join(packagePath, 'package.json'), 'utf8')).dependencies ?? {}
  const dependencies = {
    'com.reactiveuitoolkit': `file:${packagePath.replace(/\\/g, '/')}`,
    // See PKG-DEPS in Plans~/REMAINING_WORK.md -- the package does not declare these and
    // should. The shell project models a typical consumer rather than pretending otherwise.
    'com.unity.ugui': '2.5.0',
    'com.unity.inputsystem': '1.20.0',
    ...builtInModules(unity.exe),
    ...declared,
  }
  writeFileSync(join(shellProject, 'Packages', 'manifest.json'), JSON.stringify({ dependencies }, null, 2))
  writeFileSync(join(shellProject, 'Assets', 'RuitkIl2cppProbe.cs'), PROBE_SOURCE)
  if (unity.version) {
    writeFileSync(join(shellProject, 'ProjectSettings', 'ProjectVersion.txt'), `m_EditorVersion: ${unity.version}\n`)
  }
}

// ---------------------------------------------------------------------------
// Run
// ---------------------------------------------------------------------------

const unity = discoverUnity()
if (!unity) {
  console.error('x No Unity installation found. Set $RUITK_UNITY_EXE or add "unityEditor" to .ruitk-local.json.')
  process.exit(1)
}

console.log(`Unity:   ${unity.how}`)
console.log(`Package: ${packagePath}`)
console.log(`Shell:   ${shellProject}`)
if (EXPECT_FAILURE) console.log('Expecting the build to FAIL (proving the defect is present).')

writeShellProject(unity)
const logFile = join(shellProject, 'il2cpp-check.log')
if (existsSync(logFile)) rmSync(logFile)

const result = spawnSync(
  unity.exe,
  [
    '-batchmode',
    '-nographics',
    '-quit',
    '-disable-assembly-updater',
    '-projectPath',
    shellProject,
    '-executeMethod',
    'Ruitk.CICD.Il2cppBuildCheck.Run',
    '-buildOut',
    join(shellProject, 'Build'),
    '-logFile',
    logFile,
  ],
  { stdio: 'inherit' }
)

const log = existsSync(logFile) ? readFileSync(logFile, 'utf8') : ''
const lines = log.split('\n')
const verdict = lines.find((l) => l.includes('[IL2CPP-CHECK] RESULT='))
const compileErrors = lines.filter((l) => /\): error CS\d+/.test(l))
const il2cppErrors = lines.filter((l) => /IL2CPP|il2cpp/.test(l) && /error|Error|Exception|failed/.test(l))

console.log('')
for (const l of lines.filter((l) => l.includes('[IL2CPP-CHECK]'))) console.log('  ' + l.trim())

const succeeded = (verdict ?? '').includes('RESULT=SUCCEEDED')

if (!succeeded) {
  console.log('')
  if (compileErrors.length > 0) {
    console.log(`  ${compileErrors.length} C# compile error(s):`)
    for (const l of compileErrors.slice(0, 15)) console.log('    ' + l.trim())
  }
  if (il2cppErrors.length > 0) {
    console.log(`  ${il2cppErrors.length} IL2CPP line(s) mentioning failure:`)
    for (const l of il2cppErrors.slice(0, 15)) console.log('    ' + l.trim())
  }
}

console.log(`\nUnity exit=${result.status}; full log: ${logFile}`)

if (EXPECT_FAILURE) {
  if (succeeded) {
    console.error('\nx Expected this build to FAIL and it succeeded — the defect is not present here.')
    process.exit(1)
  }
  console.log('\n\u2713 build failed as expected — the defect reproduces.')
  process.exit(0)
}

if (!succeeded) {
  console.error('\nx IL2CPP build failed.')
  process.exit(1)
}
console.log('\n\u2713 IL2CPP build succeeded.')
