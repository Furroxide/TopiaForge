import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const lock = JSON.parse(readFileSync(new URL('../package-lock.json', import.meta.url), 'utf8'));
// Reviewed GitHub advisories are named beside each floor. Keep every installed
// occurrence beyond the patched floor in its reviewed series.
for (const [name, floor] of Object.entries({
  'fast-uri': [3, 1, 8], // GHSA-hrr3-gc8f-f4qj, GHSA-qw65-cvwx-89v3 and 5 related
  nanoid: [3, 3, 18], // GHSA-2v37-7h3g-55p8
  'js-yaml': [4, 3, 2], // GHSA-2883-xcg3-v3hh, GHSA-5p4m-2wfm-xmqj
  postcss: [8, 5, 23], // GHSA-fxqj-rqcc-2cmp
  astro: [7, 2, 8], // GHSA-26w7-cxv4-gfx2, GHSA-376h-93r7-7g6f
  sharp: [0, 35, 5], // GHSA-wq5f-xc86-pv6w, GHSA-rgj7-g3m4-5g8c
  'smol-toml': [1, 9, 0], // GHSA-r4xh-jqrq-34v2, GHSA-7w5x-hrqm-74c2
  svgo: [4, 1, 0], // GHSA-w27v-7q3p-w38r, GHSA-4vpr-x523-8j87
  devalue: [5, 9, 3], // GHSA-x5rw-q4pp-hg5g, GHSA-mcm9-63f2-9j32 and 5 related
  'source-map-js': [1, 2, 2], // GHSA-68fv-2mgg-jv7q
})) {
  test(`locked ${name} clears the reviewed security patch floor`, () => {
    const entries = Object.entries(lock.packages).filter(([path]) =>
      path === `node_modules/${name}` || path.endsWith(`/node_modules/${name}`));
    assert.ok(entries.length > 0, `Expected locked package ${name}`);
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
