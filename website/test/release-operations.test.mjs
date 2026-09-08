import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const read = (path) => readFileSync(new URL(`../../${path}`, import.meta.url), 'utf8');
const source = read('docs/ReleaseOperations.md').replace(/\s+/gu, ' ');
const policy = JSON.parse(read('release/release-policy.json'));
const redesign = JSON.parse(read('tests/gamemode-release-acceptance.json'));
const sdk = JSON.parse(read('tests/live-game-acceptance.json'));
const readiness = JSON.parse(read('release/release-readiness.json'));

test('release operations keeps RC1 platform scope aligned with policy', () => {
  assert.deepEqual(policy.artifactPolicy.platformArchives, ['TopiaForge-windows-x64.zip']);
  assert.match(source, /RC1 is Windows x64 only/u);
  assert.match(source, /Linux\/Proton acceptance is unavailable until a reviewed native isolation implementation and its exact-candidate evidence path exist/u);
  assert.match(source, /retired runner cannot be restored by a policy change/u);
  assert.match(source, /Any future same-host evidence must disclose that it is non-independent/u);
  assert.doesNotMatch(source, /Linux\/Proton acceptance is deferred to RC2/u);
  assert.doesNotMatch(source, /runs the RC1 Proton acceptance journey/u);
});

test('release operations requires the tracked game and authoring inventories', () => {
  assert.ok(source.includes(`all ${redesign.cases.length} redesign cases`));
  assert.ok(source.includes(`all ${sdk.cases.length} SDK cases`));
  assert.ok(source.includes(`${redesign.requiredLifecycleCycles} game lifecycle cycles`));
  assert.ok(source.includes(`${redesign.requiredAuthoringCycles} Unity authoring cycles`));
  assert.match(source, /isolated Windows user\/session or VM/u);
  assert.doesNotMatch(source, /local Windows Creator-workbench evidence bundle/u);
});

test('release operations qualifies exact bytes before staging', () => {
  assert.match(source, /preflight → platforms-built → built → accepted → staged → dispatch-requested → published/u);
  assert.match(source, /release-candidate-readiness-v1\.json/u);
  assert.match(source, /release-candidate-acceptance-v1\.json/u);
  assert.match(source, /`qualify`/u);
  assert.match(source, /In unsigned mode/u);
  assert.match(source, /In signed mode/u);
  assert.match(source, /Ed25519 update signing remains mandatory/u);
});

test('release operations does not reopen the recorded support ownership decision', () => {
  assert.equal(readiness.gates.find((gate) => gate.id === 'P1-SUPPORT-01').status, 'approved');
  assert.match(source, /Support ownership is recorded as approved/u);
  assert.doesNotMatch(source, /first-RC support gate remains open/u);
});

for (const path of [
  'docs/AdminRelease.md',
  'docs/LaunchBlockers.md',
  'release/notes/v0.1.0-rc.1.md',
]) {
  test(`${path} records unsigned authorization without granting qualification`, () => {
    const guide = read(path).replace(/\s+/gu, ' ');
    assert.equal(policy.signingIdentities.windowsDistribution, 'unsigned');
    assert.match(guide, /Unsigned Windows RC1 is authorized/u);
    assert.match(guide, /[Nn]o (?:final )?(?:candidate|archive) is qualified/u);
    assert.doesNotMatch(guide, /(?:authorization|policy decision) (?:for unsigned RC1 |to select unsigned `0\.1\.0-rc\.1` )?(?:is |remains |is still )?pending/u);
  });
}

test('release checklist names the complete eleven-package public SDK release surface', () => {
  const guide = read('docs/ReleaseChecklist.md').replace(/\s+/gu, ' ');
  assert.match(guide, /All 11 public SDK projects/u);
  assert.doesNotMatch(guide, /All 12 public SDK projects/u);
});

test('RC1 notes point authors at the current V6 multiplayer contract', () => {
  const notes = read('release/notes/v0.1.0-rc.1.md').replace(/\s+/gu, ' ');
  assert.match(notes, /through Manifest V6/u);
  assert.doesNotMatch(notes, /keeps a V5 mod standalone-only/u);
});
