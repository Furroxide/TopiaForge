import assert from 'node:assert/strict';
import test from 'node:test';

import {
  createDartdocPlan,
  dartdocHasDiagnostics,
  renderDartReferenceLanding,
  resolveDartCommand,
} from '../scripts/build-dart-reference.mjs';

test('dartdoc plan includes only the three shared launcher packages', () => {
  const plan = createDartdocPlan('/repo', '/output');
  assert.deepEqual(
    plan.map((entry) => entry.name),
    ['launcher_domain', 'launcher_data', 'launcher_ui'],
  );
  assert.deepEqual(
    plan.map((entry) => entry.dependencyCommand),
    ['dart', 'dart', 'dart'],
  );
  assert.deepEqual(
    plan.map((entry) => entry.dependencyArgs),
    [
      ['pub', 'get'],
      ['pub', 'get'],
      ['pub', 'get'],
    ],
  );
  for (const entry of plan) {
    assert.equal(entry.dartdocArgs[0], 'doc');
    assert.ok(entry.dartdocArgs.includes('--validate-links'));
    assert.equal(entry.dartdocArgs.at(-1), '.');
  }
});

test('Dart command prefers an override and then the project FVM SDK', () => {
  assert.equal(
    resolveDartCommand('/repo', { TOPIAFORGE_DART_BIN: '/custom/dart' }),
    '/custom/dart',
  );
  let detectedProjectDart;
  const projectDart = resolveDartCommand(
    '/repo',
    {},
    'win32',
    (candidate) => {
      detectedProjectDart = candidate;
      return true;
    },
  );
  assert.equal(projectDart, detectedProjectDart);
  assert.match(
    projectDart,
    /[\\/]\.fvm[\\/]flutter_sdk[\\/]bin[\\/]cache[\\/]dart-sdk[\\/]bin[\\/]dart\.exe$/u,
  );
  assert.equal(resolveDartCommand('/repo', {}, 'linux', () => false), 'dart');
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
