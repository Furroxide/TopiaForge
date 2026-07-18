import assert from 'node:assert/strict';
import test from 'node:test';

import {
  createDartdocPlan,
  dartdocHasDiagnostics,
  renderDartReferenceLanding,
} from '../scripts/build-dart-reference.mjs';

test('dartdoc plan includes only the three shared launcher packages', () => {
  const plan = createDartdocPlan('/repo', '/output');
  assert.deepEqual(
    plan.map((entry) => entry.name),
    ['launcher_domain', 'launcher_data', 'launcher_ui'],
  );
  assert.deepEqual(
    plan.map((entry) => entry.dependencyCommand),
    ['dart', 'dart', 'flutter'],
  );
  assert.deepEqual(
    plan.map((entry) => entry.dependencyArgs),
    [
      ['pub', 'get', '--enforce-lockfile'],
      ['pub', 'get', '--enforce-lockfile'],
      ['pub', 'get'],
    ],
  );
  for (const entry of plan) {
    assert.equal(entry.dartdocArgs[0], 'doc');
    assert.ok(entry.dartdocArgs.includes('--validate-links'));
    assert.equal(entry.dartdocArgs.at(-1), '.');
  }
});

test('dartdoc diagnostics fail warnings but accept a clean summary', () => {
  assert.equal(
    dartdocHasDiagnostics('Documented 42 public libraries. 0 errors and 0 warnings.'),
    false,
  );
  assert.equal(dartdocHasDiagnostics('warning: unresolved doc reference'), true);
  assert.equal(dartdocHasDiagnostics('Found 2 errors and 1 warning.'), true);
});

test('Dart landing page links every package and is searchable', () => {
  const html = renderDartReferenceLanding();
  assert.match(html, /data-pagefind-body/u);
  for (const packageName of ['launcher_domain', 'launcher_data', 'launcher_ui']) {
    assert.match(html, new RegExp(`/api/dart/${packageName}/`, 'u'));
  }
  assert.match(html, /Application and CLI internals are intentionally excluded/u);
});
