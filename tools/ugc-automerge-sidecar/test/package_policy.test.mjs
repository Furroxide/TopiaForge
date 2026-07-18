import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const packageManifest = readJson('../package.json');
const packageLock = readJson('../package-lock.json');

test('sidecar package and lock enforce the supported Node floor', () => {
  assert.equal(packageManifest.engines.node, '>=24.16.0');
  assert.equal(packageLock.packages[''].engines.node, '>=24.16.0');
  assert.equal(packageLock.packages[''].name, packageManifest.name);
  assert.equal(packageLock.packages[''].version, packageManifest.version);
  assert.deepEqual(packageLock.packages[''].dependencies, packageManifest.dependencies);
});

function readJson(relativePath) {
  const path = fileURLToPath(new URL(relativePath, import.meta.url));
  return JSON.parse(readFileSync(path, 'utf8'));
}
