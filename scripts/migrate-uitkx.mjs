#!/usr/bin/env node
// Thin wrapper around the UitkxMigrateImports codemod so users do not have to
// remember the dotnet incantation. Resolves the tool RELATIVE TO THIS SCRIPT
// (machine-paths rule: repo locations are derived, never written down), probes
// dotnet per the standard chain ($RUITK_DOTNET -> .ruitk-local.json dotnetPath
// -> PATH), and forwards every argument verbatim:
//
//   node scripts/migrate-uitkx.mjs <dir> --es-modules [--check] [--report <file>]
//
// Exit code is the tool's own (0 ok, 1 check-dirty, 2 usage, 3 io) or 2 when
// dotnet cannot be found.
import { spawnSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = dirname(dirname(fileURLToPath(import.meta.url)));
const toolProj = join(repoRoot, 'SourceGenerator~', 'Tools', 'UitkxMigrateImports', 'UitkxMigrateImports.csproj');

function resolveDotnet() {
  if (process.env.RUITK_DOTNET && existsSync(process.env.RUITK_DOTNET)) return process.env.RUITK_DOTNET;
  const localCfg = join(repoRoot, '.ruitk-local.json');
  if (existsSync(localCfg)) {
    try {
      const cfg = JSON.parse(readFileSync(localCfg, 'utf8'));
      if (cfg.dotnetPath && existsSync(cfg.dotnetPath)) return cfg.dotnetPath;
    } catch { /* malformed local config - fall through to PATH */ }
  }
  return 'dotnet';
}

if (!existsSync(toolProj)) {
  console.error(`error: codemod project not found at ${toolProj}`);
  process.exit(2);
}

const args = process.argv.slice(2);
if (args.length === 0) {
  console.error('usage: node scripts/migrate-uitkx.mjs <dir> [--es-modules] [--check] [--tidy] [--format] [--report <file>]');
  process.exit(2);
}

const res = spawnSync(resolveDotnet(), ['run', '--project', toolProj, '--', ...args], {
  stdio: 'inherit',
  shell: false,
});
if (res.error) {
  console.error(`error: could not launch dotnet (${res.error.message}) - install the .NET 8 SDK, or set $RUITK_DOTNET / .ruitk-local.json "dotnetPath"`);
  process.exit(2);
}
process.exit(res.status ?? 2);
