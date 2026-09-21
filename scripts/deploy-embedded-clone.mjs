#!/usr/bin/env node
// Copyright (c) 2026 Yaniv Kalfa. All Rights Reserved.
//
// Deploys this working tree into an EMBEDDED copy of the package inside a Unity project,
// so the owner can test package changes in a real editor.
//
// Why a script rather than robocopy by hand: the two things that go wrong are both quiet.
//
//   1. STALE FILES. Editing the repo only ADDS to the clone. A file the repo deleted -- or
//      MOVED, which is a delete plus an add -- survives in the clone and keeps compiling.
//      The 0.21.0 signals split is the sharp case: Signal<T> moved from Shared/Core/Signals
//      to Signals/, and a clone that keeps both defines the type twice, so every use becomes
//      an ambiguous reference. The deletion set is not guessed here: it is `git diff` from
//      the clone's own version (read out of its package.json) to this working tree, so it is
//      exactly what the repository removed and nothing else.
//
//   2. THE OWNER'S FILES. The clone legitimately holds files the repo does not: the
//      generator trigger Unity writes, and whatever the owner is experimenting with under
//      Samples. A mirror deletes those. This never deletes a file the repository did not
//      delete -- anything else clone-only is reported and left alone.
//
// LOCKED DLLs: Analyzers/Ruitk.Language.dll is held for the life of the Unity PROCESS by
// UitkxHmrCompiler's Assembly.LoadFrom, and a domain reload does not release it. Deploying
// Editor/Plugins/Ruitk.Language.Editor.dll while HMR runs breaks the Builder outright with a
// type-identity mismatch. So: close Unity before deploying anything that touches those, and
// this script refuses rather than half-succeeding.
//
// The clone path is machine-local and therefore NEVER written down here:
// $RUITK_EMBEDDED_CLONE -> .ruitk-local.json "embeddedClone" -> an error naming both.
//
//   node scripts/deploy-embedded-clone.mjs --dry-run   list every copy and delete
//   node scripts/deploy-embedded-clone.mjs             deploy
//   node scripts/deploy-embedded-clone.mjs --force     deploy even if Unity is running
//                                                       (skips the locked DLLs, says so)

import { execFileSync } from 'node:child_process'
import { copyFileSync, existsSync, mkdirSync, readFileSync, readdirSync, rmSync, statSync } from 'node:fs'
import { dirname, join, relative, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..')
const args = process.argv.slice(2)
const DRY_RUN = args.includes('--dry-run')
const FORCE = args.includes('--force')

/** Folders Unity compiles out of the package. Everything else in the repo is tooling. */
const PAYLOAD = [
  'Runtime',
  'Shared',
  'Signals',
  'Editor',
  'Builder',
  'Ugui',
  'Samples',
  'Diagnostics',
  'CICD',
  'Analyzers',
]

/** Loose files at the package root that Unity reads. */
const ROOT_FILES = ['package.json', 'package.json.meta', 'config.json', 'config.json.meta']

/** Held open by the Unity process; copying them while it runs fails or corrupts the session. */
const LOCKED_WHILE_UNITY_RUNS = [
  'Analyzers/Ruitk.Language.dll',
  'Analyzers/Ruitk.SourceGenerator.dll',
  'Editor/Plugins/Ruitk.Language.Editor.dll',
  'Editor/Plugins/Ruitk.Language.Editor.pdb',
]

// ---------------------------------------------------------------------------
// Resolve the clone
// ---------------------------------------------------------------------------

function resolveClone() {
  const fromEnv = process.env.RUITK_EMBEDDED_CLONE
  if (fromEnv) return { path: resolve(fromEnv), how: '$RUITK_EMBEDDED_CLONE' }
  try {
    const cfg = JSON.parse(readFileSync(join(repoRoot, '.ruitk-local.json'), 'utf8'))
    if (cfg.embeddedClone) return { path: resolve(cfg.embeddedClone), how: '.ruitk-local.json "embeddedClone"' }
  } catch {
    // no local config, or unreadable — fall through to the error below
  }
  return null
}

const clone = resolveClone()
if (!clone) {
  console.error(
    'x No embedded clone configured. Set one of:\n' +
      '    $RUITK_EMBEDDED_CLONE                        (absolute path)\n' +
      '    .ruitk-local.json  { "embeddedClone": ... }  (gitignored; copy .ruitk-local.example.json)\n' +
      '  It points at the PACKAGE folder inside the Unity project, e.g.\n' +
      '    <project>/Packages/com.reactiveuitoolkit'
  )
  process.exit(1)
}
if (!existsSync(join(clone.path, 'package.json'))) {
  console.error(`x ${clone.path} does not look like the package (no package.json). From ${clone.how}.`)
  process.exit(1)
}

// ---------------------------------------------------------------------------
// Is Unity running?
// ---------------------------------------------------------------------------

function unityIsRunning() {
  try {
    if (process.platform === 'win32') {
      const out = execFileSync('tasklist', ['/FI', 'IMAGENAME eq Unity.exe', '/NH'], { encoding: 'utf8' })
      return /Unity\.exe/i.test(out)
    }
    const out = execFileSync('pgrep', ['-x', 'Unity'], { encoding: 'utf8' })
    return out.trim().length > 0
  } catch {
    return false
  }
}

const unityRunning = unityIsRunning()

// ---------------------------------------------------------------------------
// What to delete: exactly what the repository removed since the clone's version
// ---------------------------------------------------------------------------

function cloneVersion() {
  try {
    return JSON.parse(readFileSync(join(clone.path, 'package.json'), 'utf8')).version ?? null
  } catch {
    return null
  }
}

function deletionsSince(version) {
  const tag = `v${version}`
  try {
    execFileSync('git', ['rev-parse', '--verify', `${tag}^{commit}`], { cwd: repoRoot, stdio: 'pipe' })
  } catch {
    return { paths: [], note: `tag ${tag} not found — no deletions computed` }
  }
  // Compared against the WORKING TREE, not HEAD: a deploy exists to test uncommitted work,
  // and a move staged but not committed is exactly the case that leaves a duplicate type.
  const out = execFileSync(
    'git',
    ['diff', '--diff-filter=D', '--no-renames', '--name-only', tag, '--', ...PAYLOAD, ...ROOT_FILES],
    { cwd: repoRoot, encoding: 'utf8', maxBuffer: 32 * 1024 * 1024 }
  )
  return { paths: out.split('\n').map((l) => l.trim()).filter(Boolean), note: `since ${tag}` }
}

// ---------------------------------------------------------------------------
// Walk
// ---------------------------------------------------------------------------

function* walk(dir, base = dir) {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const full = join(dir, entry.name)
    if (entry.isDirectory()) {
      if (entry.name.endsWith('~') || entry.name === '.git') continue
      yield* walk(full, base)
    } else {
      yield relative(base, full).replace(/\\/g, '/')
    }
  }
}

// ---------------------------------------------------------------------------
// Run
// ---------------------------------------------------------------------------

const version = cloneVersion()
const repoVersion = JSON.parse(readFileSync(join(repoRoot, 'package.json'), 'utf8')).version

console.log(`Clone:  ${clone.path}`)
console.log(`        via ${clone.how}`)
console.log(`Version: ${version} -> ${repoVersion}`)
console.log(`Unity:   ${unityRunning ? 'RUNNING' : 'not running'}`)
console.log('')

if (unityRunning && !FORCE && !DRY_RUN) {
  console.error(
    'x Unity is running.\n\n' +
      '  Analyzers/Ruitk.Language.dll is held for the life of the Unity PROCESS (Assembly.LoadFrom\n' +
      '  in UitkxHmrCompiler); a domain reload does not release it. Deploying\n' +
      '  Ruitk.Language.Editor.dll while HMR runs breaks the Builder with a type-identity\n' +
      '  mismatch that reads as MissingMethodException.\n\n' +
      '  Close Unity and run again. Or --force to deploy everything EXCEPT the locked files,\n' +
      '  which leaves the clone running the OLD generator -- wrong whenever the generator changed.'
  )
  process.exit(1)
}

let copied = 0
let skippedLocked = 0
const copies = []

for (const folder of PAYLOAD) {
  const src = join(repoRoot, folder)
  if (!existsSync(src)) continue
  for (const rel of walk(src)) {
    const relFromRoot = `${folder}/${rel}`
    if (unityRunning && LOCKED_WHILE_UNITY_RUNS.includes(relFromRoot)) {
      skippedLocked++
      continue
    }
    copies.push([join(src, rel), join(clone.path, folder, rel), relFromRoot])
  }
}
for (const name of ROOT_FILES) {
  const src = join(repoRoot, name)
  if (existsSync(src)) copies.push([src, join(clone.path, name), name])
}

for (const [from, to, rel] of copies) {
  const changed = !existsSync(to) || statSync(from).size !== statSync(to).size || readFileSync(from).compare(readFileSync(to)) !== 0
  if (!changed) continue
  if (DRY_RUN) {
    console.log(`  copy    ${rel}`)
  } else {
    mkdirSync(dirname(to), { recursive: true })
    copyFileSync(from, to)
  }
  copied++
}

const { paths: deletions, note } = version ? deletionsSince(version) : { paths: [], note: 'clone version unreadable' }
let deleted = 0
for (const rel of deletions) {
  const target = join(clone.path, rel)
  if (!existsSync(target)) continue
  console.log(`  ${DRY_RUN ? 'delete ' : 'deleted'} ${rel}`)
  if (!DRY_RUN) rmSync(target, { force: true })
  deleted++
}

// Deleting the last file in a folder leaves the folder. Unity imports an empty folder and
// mints a .meta for it, so the clone grows phantom assets that exist in no repository.
// Prune upward, stopping the moment a directory still holds something.
if (!DRY_RUN) {
  for (const rel of deletions) {
    let dir = dirname(join(clone.path, rel))
    while (dir.startsWith(clone.path) && dir !== clone.path) {
      let entries
      try { entries = readdirSync(dir) } catch { break }
      if (entries.length > 0) break
      rmSync(dir, { recursive: true, force: true })
      rmSync(dir + '.meta', { force: true })
      console.log(`  pruned  ${relative(clone.path, dir).split('\\').join('/')}/ (emptied)`)
      dir = dirname(dir)
    }
  }
}

// An EMPTY directory that the repository does not have is safe to remove whatever produced
// it, and removing it cannot lose a file. This is the durable half of the cleanup: the
// deletion list above depends on the clone's version resolving to a tag, which it does not
// once the clone has been deployed to an unreleased version -- and an emptied folder would
// then sit there forever, collecting a Unity-generated .meta for an asset that exists in no
// repository.
if (!DRY_RUN) {
  let pruned
  do {
    pruned = false
    for (const folder of PAYLOAD) {
      const dir = join(clone.path, folder)
      if (!existsSync(dir)) continue
      for (const sub of emptyDirsUnder(dir)) {
        const rel = relative(clone.path, sub).split('\\').join('/')
        if (existsSync(join(repoRoot, rel))) continue
        rmSync(sub, { recursive: true, force: true })
        rmSync(sub + '.meta', { force: true })
        console.log(`  pruned  ${rel}/ (empty, not in the repository)`)
        pruned = true
      }
    }
  } while (pruned) // removing one can empty its parent
}

function* emptyDirsUnder(dir) {
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    if (!entry.isDirectory()) continue
    const full = join(dir, entry.name)
    yield* emptyDirsUnder(full)
    try {
      if (readdirSync(full).length === 0) yield full
    } catch {
      // vanished mid-walk; nothing to prune
    }
  }
}

// Anything else the clone has and the repo does not is the owner's, or Unity's. Report only.
const cloneOnly = []
for (const folder of PAYLOAD) {
  const dir = join(clone.path, folder)
  if (!existsSync(dir)) continue
  for (const rel of walk(dir)) {
    const relFromRoot = `${folder}/${rel}`
    if (!existsSync(join(repoRoot, folder, rel)) && !deletions.includes(relFromRoot)) {
      cloneOnly.push(relFromRoot)
    }
  }
}

console.log('')
console.log(`${copied} file(s) ${DRY_RUN ? 'to copy' : 'copied'}, ${deleted} ${DRY_RUN ? 'to delete' : 'deleted'} (${note}).`)
if (skippedLocked > 0) {
  console.log(`${skippedLocked} locked file(s) SKIPPED because Unity is running — the clone is running the old generator.`)
}
if (cloneOnly.length > 0) {
  console.log(`\n${cloneOnly.length} file(s) exist only in the clone and were left alone:`)
  for (const rel of cloneOnly) console.log(`  ${rel}`)
  console.log('These are Unity-generated or yours. Nothing here deletes a file the repository did not.')
}
if (!DRY_RUN) {
  console.log('\nNext: focus the Unity window so it reimports, then check the Console before testing.')
}
