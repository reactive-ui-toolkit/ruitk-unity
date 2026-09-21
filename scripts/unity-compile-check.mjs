#!/usr/bin/env node
// Compile-checks the Unity C# tree against the REAL Unity assemblies, without launching
// the Editor.
//
// Why this exists: Shared/, Runtime/ and Editor/ are only ever compiled by Unity, so a
// wrong UnityEngine API in an element adapter or a host config is invisible to CI and
// surfaces only when a human opens the project. This catches that class of error in
// seconds.
//
// It compiles, it never executes. Unity's assemblies bind to native ECalls that only
// exist inside the Unity runtime -- probed on 6000.5.6f1, Debug.Log, Time.realtimeSinceStartup,
// GUID.Generate and `new VisualElement()` all throw SecurityException outside the editor.
// Running is the job of SharedTests~, which uses a shim instead.
//
// It compiles TWICE, which is the point: once with no version defines (the package floor,
// proving gated code compiles OUT) and once with the 6.4/6.5 defines set (proving gated
// code compiles IN). That is the manual "open on the floor / open on 6.5" step, automated.
//
// Unity is DISCOVERED, never hardcoded: $RUITK_UNITY_EDITOR -> .ruitk-local.json
// "unityEditor" -> the standard Hub install roots. See the machine-local-paths gate.

import { execFileSync } from 'node:child_process'
import { existsSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..')

const CONFIGURATIONS = [
  { name: 'floor (no version defines)', defines: [] },
  { name: 'Unity 6.5 (6.4 + 6.5 gates on)', defines: ['UNITY_6000_4_OR_NEWER', 'UNITY_6000_5_OR_NEWER'] },
]

function fail(message) {
  console.error(`x ${message}`)
  process.exit(1)
}

// ---------------------------------------------------------------------------
// Unity discovery: env var -> .ruitk-local.json -> standard install roots
// ---------------------------------------------------------------------------

function candidateRoots() {
  const roots = []
  if (process.platform === 'win32') {
    roots.push('C:\\Program Files\\Unity\\Hub\\Editor', 'C:\\Program Files (x86)\\Unity\\Hub\\Editor')
  } else if (process.platform === 'darwin') {
    roots.push('/Applications/Unity/Hub/Editor')
  } else {
    roots.push(join(process.env.HOME ?? '', 'Unity/Hub/Editor'), '/opt/unity/Hub/Editor')
  }
  return roots.filter((r) => existsSync(r))
}

function managedDirFor(editorRoot) {
  const candidates = [
    join(editorRoot, 'Editor', 'Data', 'Managed', 'UnityEngine'),
    join(editorRoot, 'Contents', 'Managed', 'UnityEngine'),
  ]
  return candidates.find((c) => existsSync(c))
}

function discoverManagedDir() {
  const fromEnv = process.env.RUITK_UNITY_EDITOR
  if (fromEnv) {
    const dir = managedDirFor(fromEnv)
    if (dir) return { dir, how: '$RUITK_UNITY_EDITOR' }
    fail(`$RUITK_UNITY_EDITOR is set to "${fromEnv}" but no Managed/UnityEngine directory was found under it.`)
  }

  const localConfigPath = join(repoRoot, '.ruitk-local.json')
  if (existsSync(localConfigPath)) {
    try {
      const cfg = JSON.parse(readFileSync(localConfigPath, 'utf8'))
      if (cfg.unityEditor) {
        // The key may point at the executable or at the install root; accept both.
        for (const base of [cfg.unityEditor, dirname(cfg.unityEditor), dirname(dirname(cfg.unityEditor))]) {
          const dir = managedDirFor(base)
          if (dir) return { dir, how: '.ruitk-local.json "unityEditor"' }
        }
      }
    } catch {
      // A malformed local config should not mask the standard discovery path.
    }
  }

  for (const root of candidateRoots()) {
    const versions = readdirSync(root, { withFileTypes: true })
      .filter((e) => e.isDirectory())
      .map((e) => e.name)
      .sort()
      .reverse() // newest first
    for (const version of versions) {
      const dir = managedDirFor(join(root, version))
      if (dir) return { dir, how: `discovered under ${root} (${version})` }
    }
  }

  const message =
    'No Unity installation found. Set $RUITK_UNITY_EDITOR, or add "unityEditor" to .ruitk-local.json, ' +
    'or install a Unity editor under the standard Hub location.'

  // CI runners have no licensed editor. --allow-missing lets the gate be wired into CI
  // now and start working for free the day a Unity-enabled image is used, without
  // pretending to have passed in the meantime.
  if (process.argv.includes('--allow-missing')) {
    console.log(`- SKIPPED: ${message}`)
    console.log('- This check did NOT run. It is a local pre-push gate; run it before opening a PR.')
    process.exit(0)
  }

  fail(message)
}

// ---------------------------------------------------------------------------
// Compile
// ---------------------------------------------------------------------------

function sourcesFor() {
  // Editor/ and Ugui/ are deliberately out of scope for now: they need UnityEditor and
  // uGUI module references and their own define sets. Shared/ + Runtime/ is where the
  // reconciler and every element adapter live, which is what this gate is protecting.
  //
  // Signals/ is its own assembly (Ruitk.Signals) but belongs in the SAME csproj here:
  // this gate compiles source, not assemblies, and Shared/ names signal types. Leaving
  // it out turns every Shared/ file that touches a Signal into a compile error that has
  // nothing to do with what the gate is checking.
  return ['Shared', 'Runtime', 'Signals']
}

function run(config, managedDir) {
  const work = mkdtempSync(join(tmpdir(), 'ruitk-compilecheck-'))
  try {
    const refs = readdirSync(managedDir)
      .filter((f) => f.endsWith('.dll'))
      .map((f) => join(managedDir, f))

    const proj = `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
    <LangVersion>9</LangVersion>
    <Nullable>disable</Nullable>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <AssemblyName>RuitkCompileCheck</AssemblyName>
    <DefineConstants>${config.defines.join(';')}</DefineConstants>
    <NoWarn>CS0618;CS0612;CS0067;CS0414;CS0169;CS1591;CS0108;CS0114</NoWarn>
    <ProduceReferenceAssembly>false</ProduceReferenceAssembly>
    <DebugType>none</DebugType>
  </PropertyGroup>
  <ItemGroup>
${sourcesFor()
  .map((d) => `    <Compile Include="${join(repoRoot, d).replace(/\\/g, '/')}/**/*.cs" />`)
  .join('\n')}
  </ItemGroup>
  <ItemGroup>
${refs
  .map(
    (r) =>
      `    <Reference Include="${r.split(/[\\/]/).pop().replace(/\.dll$/, '')}"><HintPath>${r.replace(/\\/g, '/')}</HintPath><Private>false</Private></Reference>`
  )
  .join('\n')}
  </ItemGroup>
</Project>
`
    const projPath = join(work, 'CompileCheck.csproj')
    writeFileSync(projPath, proj, 'utf8')

    try {
      execFileSync('dotnet', ['build', projPath, '-v', 'q', '--nologo'], {
        stdio: 'pipe',
        encoding: 'utf8',
      })
      console.log(`  OK    ${config.name}`)
      return true
    } catch (err) {
      const out = `${err.stdout ?? ''}${err.stderr ?? ''}`
      const errors = out
        .split('\n')
        .filter((l) => l.includes('error CS'))
        .slice(0, 25)
      console.error(`  FAIL  ${config.name}`)
      for (const line of errors) console.error(`        ${line.trim()}`)
      const total = out.split('\n').filter((l) => l.includes('error CS')).length
      if (total > errors.length) console.error(`        ... and ${total - errors.length} more`)
      return false
    }
  } finally {
    rmSync(work, { recursive: true, force: true })
  }
}

const { dir: managedDir, how } = discoverManagedDir()
console.log(`Unity managed assemblies: ${how}`)
console.log(`  ${managedDir}`)
console.log(`Compiling ${sourcesFor().join(', ')} against them (compile only, never executed):`)

let ok = true
for (const config of CONFIGURATIONS) {
  if (!run(config, managedDir)) ok = false
}

if (!ok) {
  console.error('\nx unity compile check failed - the Unity C# tree does not compile against the real assemblies.')
  process.exit(1)
}
console.log('\n\u2713 unity compile check: all configurations compile.')
