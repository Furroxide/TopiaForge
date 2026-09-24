import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const lock = JSON.parse(readFileSync(new URL('../package-lock.json', import.meta.url), 'utf8'));
// Reviewed GitHub advisories: GHSA-5jgf-p345-68v8 (and four related fast-uri
// advisories), GHSA-2v37-7h3g-55p8, GHSA-5p4m-2wfm-xmqj, GHSA-fxqj-rqcc-2cmp.
// Keep every installed occurrence beyond the patched floor in its reviewed series.
for (const [name, floor] of Object.entries({
  'fast-uri': [3, 1, 6],
  nanoid: [3, 3, 18],
  'js-yaml': [4, 3, 1],
  postcss: [8, 5, 23],
})) {
  test(`locked ${name} clears the reviewed security patch floor`, () => {
    const entries = Object.entries(lock.packages).filter(([path]) =>
      path === `node_modules/${name}` || path.endsWith(`/node_modules/${name}`));
    assert.ok(entries.length > 0, `Expected transitive dependency ${name}`);
    for (const [path, pkg] of entries) {
      assert.match(pkg.version, /^\d+\.\d+\.\d+$/);
      const actual = pkg.version.split('.').map(Number);
      assert.equal(actual[0], floor[0], `Review advisory coverage for new ${name} major`);
      assert.ok(actual[1] > floor[1] ||
        (actual[1] === floor[1] && actual[2] >= floor[2]),
      `${path}@${pkg.version} must be >=${floor.join('.')}`);
    }
  });
}
