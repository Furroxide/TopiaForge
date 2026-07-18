import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';

import {
  addDocfxPagefindBody,
  injectApiSearchBodies,
  isRedirectDocument,
} from '../scripts/build-search.mjs';

test('injects Pagefind bodies into DocFX and all dartdoc packages', (t) => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'topiaforge-search-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  write(root, 'api/csharp/index.html', '<html><article class="content">C#</article></html>');
  for (const packageName of ['launcher_domain', 'launcher_data', 'launcher_ui']) {
    write(
      root,
      `api/dart/${packageName}/index.html`,
      '<html><div class="main-content" id="dartdoc-main-content">Dart</div></html>',
    );
    write(
      root,
      `api/dart/${packageName}/redirect.html`,
      '<html><meta http-equiv="refresh" content="0; url=index.html"></html>',
    );
  }

  const counts = injectApiSearchBodies(root);

  assert.equal(counts.get('DocFX C#'), 1);
  for (const packageName of ['launcher_domain', 'launcher_data', 'launcher_ui']) {
    assert.equal(counts.get(`dartdoc ${packageName}`), 1);
    assert.match(
      fs.readFileSync(path.join(root, `api/dart/${packageName}/index.html`), 'utf8'),
      /id="dartdoc-main-content" data-pagefind-body/u,
    );
  }
  assert.match(
    fs.readFileSync(path.join(root, 'api/csharp/index.html'), 'utf8'),
    /id="_content" data-pagefind-body/u,
  );
  assert.match(
    fs.readFileSync(path.join(root, 'api/csharp/index.html'), 'utf8'),
    /<html lang="en">/u,
  );
});

test('marker injection is idempotent and generator markup changes are fatal', (t) => {
  const initial = '<article id="_content">Reference</article>';
  const first = addDocfxPagefindBody(initial);
  const second = addDocfxPagefindBody(first.html);
  assert.equal(first.changed, true);
  assert.equal(second.changed, false);
  assert.equal(isRedirectDocument('<meta http-equiv="refresh" content="0">'), true);

  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'topiaforge-search-drift-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  write(root, 'api/csharp/index.html', '<html><main>DocFX changed</main></html>');
  for (const packageName of ['launcher_domain', 'launcher_data', 'launcher_ui']) {
    write(
      root,
      `api/dart/${packageName}/index.html`,
      '<html><div id="dartdoc-main-content">Dart</div></html>',
    );
  }
  assert.throws(
    () => injectApiSearchBodies(root),
    /generator markup changed/u,
  );
});

function write(root, relativePath, content) {
  const destination = path.join(root, ...relativePath.split('/'));
  fs.mkdirSync(path.dirname(destination), { recursive: true });
  fs.writeFileSync(destination, content);
}
