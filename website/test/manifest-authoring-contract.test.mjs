import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

function read(relativePath) {
  return readFileSync(new URL(`../../${relativePath}`, import.meta.url), 'utf8');
}

const schemaVersion = JSON.parse(read('schemas/topiaforge.mod.schema.json'))
  .properties.schemaVersion.const;
const sandboxVersion = JSON.parse(read('mods/TopiaForge.Sandbox/topiaforge.mod.json'))
  .schemaVersion;

// These are current authoring instructions, not the historical schema guides.
// Compare their claims with the actual canonical schema and described package.
for (const [guide, expected] of [
  ['Modding.md', schemaVersion],
  ['ModPackaging.md', schemaVersion],
  ['Sandbox.md', sandboxVersion],
]) {
  test(`${guide} describes the schema used by current authoring`, () => {
    const versions = [...read(`docs/${guide}`).matchAll(/schema[- ]V(\d+)\b/giu)]
      .map((match) => Number(match[1]));
    assert.ok(versions.length > 0, `${guide} must state its authoring schema`);
    assert.ok(versions.every((version) => version === expected),
      `${guide} claims ${versions.join(', ')}; current authoring uses V${expected}`);
  });
}
