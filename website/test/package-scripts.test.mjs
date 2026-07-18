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
  assert.equal(packageManifest.devDependencies.pagefind, '1.5.2');
  assert.equal(
    packageManifest.scripts['build:dart-reference'],
    'node scripts/build-dart-reference.mjs',
  );
  assert.equal(
    packageManifest.scripts['build:search'],
    'node scripts/build-search.mjs',
  );
  assert.equal(
    packageManifest.scripts['pages:assemble'],
    'node scripts/assemble-pages.mjs',
  );

  for (const scriptName of ['build:all', 'check']) {
    const script = packageManifest.scripts[scriptName];
    const steps = [
      script.indexOf(scriptName === 'build:all' ? 'npm run build' : 'astro build'),
      script.indexOf('build:reference'),
      script.indexOf('build:dart-reference'),
      script.indexOf('build:search'),
      script.indexOf('check:built-links'),
    ];
    assert.ok(steps.every((step) => step >= 0), `${scriptName} has every generator`);
    assert.deepEqual(steps, [...steps].sort((left, right) => left - right));
  }
});
