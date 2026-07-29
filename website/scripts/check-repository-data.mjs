#!/usr/bin/env node

import { spawnSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { dirname, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { checkRepositoryData } from './lib/repository-data.mjs';

const websiteRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const repositoryRoot = resolve(websiteRoot, '..');

let sourcePaths = process.argv.slice(2).map((path) => {
  const absolute = resolve(process.cwd(), path);
  return relative(repositoryRoot, absolute);
});
if (sourcePaths.length === 0) {
  const result = spawnSync(
    'git',
    [
      'ls-files',
      '--cached',
      '--others',
      '--exclude-standard',
      '-z',
      '--',
      '*.json',
      '*.yml',
      '*.yaml',
    ],
    {
      cwd: repositoryRoot,
      encoding: 'buffer',
      maxBuffer: 8 * 1024 * 1024,
      shell: false,
    },
  );
  if (result.error || result.status !== 0) {
    const detail = result.error?.message
      || result.stderr.toString('utf8').trim()
      || `exit ${result.status}`;
    console.error(`Could not inventory JSON and YAML files with Git: ${detail}`);
    process.exit(1);
  }
  sourcePaths = result.stdout
    .toString('utf8')
    .split('\0')
    .filter((path) => path && existsSync(resolve(repositoryRoot, path)));
}

const { failures, parsedCount } = checkRepositoryData({
  repositoryRoot,
  sourcePaths,
});
if (failures.length === 0) {
  console.log(`Repository JSON/YAML: pass (${parsedCount} files)`);
} else {
  for (const failure of failures) {
    console.error(failure);
  }
  console.error(`Repository JSON/YAML: ${failures.length} failure(s)`);
  process.exitCode = 1;
}
