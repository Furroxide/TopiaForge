import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

const packagePath = fileURLToPath(new URL('../package.json', import.meta.url));
const packageManifest = JSON.parse(readFileSync(packagePath, 'utf8'));

test('documentation generation is explicit and the full check validates it once', () => {
  assert.equal(packageManifest.engines.node, '>=24.16.0');
  assert.equal(packageManifest.scripts.prepare, undefined);
  assert.equal(
    packageManifest.scripts['docs:prepare'],
    'node scripts/prepare-docs.mjs',
  );
  assert.doesNotMatch(packageManifest.scripts.check, /check:content/);
  assert.equal(
    packageManifest.scripts.check.match(/docs:prepare/gu)?.length,
    1,
  );
});
