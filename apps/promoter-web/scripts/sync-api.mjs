#!/usr/bin/env node
// Vendors the API contract this app is built against (contract/openapi.yaml + contract/VERSION) and regenerates
// src/api/schema.d.ts from it. The backend owns openapi.yaml and releases every version pushed to main as
// `contract-v<version>` (docs/plans/2026-10-31-repo-split.md, "The contract between repos").
//
//   npm run sync:api            while this app lives in the backend's repository: copies ../../openapi.yaml
//   npm run sync:api -- 1.3.0   from the release contract-v1.3.0 (gh release download; gh must be signed in)
//
// CI runs the first form and fails if contract/ or schema.d.ts then differ from what was committed.
import { spawnSync } from 'node:child_process';
import { copyFileSync, existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const BACKEND_REPOSITORY = 'masidav/firstline';

const app = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const contractDir = resolve(app, 'contract');
const target = resolve(contractDir, 'openapi.yaml');
const requested = process.argv[2];

function fail(message) {
  console.error(message);
  process.exit(1);
}

function run(command, args) {
  const result = spawnSync(command, args, { stdio: 'inherit', cwd: app });
  if (result.error) fail(`${command}: ${result.error.message}`);
  if (result.status !== 0) process.exit(result.status ?? 1);
}

mkdirSync(contractDir, { recursive: true });
if (requested) {
  if (!/^\d+\.\d+\.\d+$/.test(requested)) fail(`Expected a contract version like 1.3.0, got '${requested}'.`);
  run('gh', ['release', 'download', `contract-v${requested}`, '--repo', BACKEND_REPOSITORY,
    '--pattern', 'openapi.yaml', '--output', target, '--clobber']);
} else {
  const local = resolve(app, '../../openapi.yaml');
  if (!existsSync(local)) {
    fail('No ../../openapi.yaml next to this app: name the contract version to vendor, e.g. `npm run sync:api -- 1.3.0`.');
  }
  copyFileSync(local, target);
}

// `version:` directly under `info:` (two-space indent), the contract's version (openapi.yaml, "Contract version").
const declared = /^info:\n(?:  .*\n)*?  version:\s*["']?([^"'\s]+)/m.exec(readFileSync(target, 'utf8'))?.[1];
if (!declared) fail('contract/openapi.yaml has no info.version.');
if (requested && declared !== requested) fail(`Release contract-v${requested} declares info.version ${declared}.`);
writeFileSync(resolve(contractDir, 'VERSION'), `${declared}\n`);

run('npm', ['run', 'generate:api']);
console.log(`Contract ${declared} vendored in contract/, src/api/schema.d.ts regenerated.`);
