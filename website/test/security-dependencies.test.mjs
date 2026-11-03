import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const lock = JSON.parse(readFileSync(new URL('../package-lock.json', import.meta.url), 'utf8'));
// Reviewed GitHub advisories are named beside each floor. Keep every installed
// occurrence beyond the patched floor in its reviewed series.
for (const [name, floor] of Object.entries({
  'fast-uri': [3, 1, 9], // GHSA-w74x-v6f9-j3xg, GHSA-wjr9-j795-jxcr and related URI normalization fixes
  nanoid: [3, 3, 18], // GHSA-2v37-7h3g-55p8
  'js-yaml': [4, 3, 2], // GHSA-2883-xcg3-v3hh, GHSA-5p4m-2wfm-xmqj
  postcss: [8, 5, 23], // GHSA-fxqj-rqcc-2cmp
  astro: [7, 3, 3], // GHSA-98hj-9722-w3j8, GHSA-mpr7-6m5w-76rw and earlier fixes
  sharp: [0, 35, 5], // GHSA-wq5f-xc86-pv6w, GHSA-rgj7-g3m4-5g8c
  'smol-toml': [1, 9, 0], // GHSA-r4xh-jqrq-34v2, GHSA-7w5x-hrqm-74c2
  svgo: [4, 1, 0], // GHSA-w27v-7q3p-w38r, GHSA-4vpr-x523-8j87
  devalue: [5, 9, 3], // GHSA-x5rw-q4pp-hg5g, GHSA-mcm9-63f2-9j32 and 5 related
  'source-map-js': [1, 2, 2], // GHSA-68fv-2mgg-jv7q
  'postcss-selector-parser': [6, 1, 5], // GHSA-rj75-hqrm-r3gf
  vite: [8, 1, 6], // GHSA-587h-7wmj-h5pm, GHSA-698v-4r3x-3vcv and related editor/server fixes
  zod: [4, 6, 6], // GHSA-mrhx-c7xc-m3w5
  undici: [8, 12, 2], // GHSA-2m5v-pch6-74hx, GHSA-4w7w-5xrc-9h26 and related transport fixes
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
