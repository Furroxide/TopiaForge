import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

import { pages, routeForOutput } from '../scripts/docs/catalog.mjs';
import { LocalDocumentationLinkRewriter } from '../scripts/docs/local-links.mjs';
import { checkMarkdownLinks } from '../scripts/lib/markdown-links.mjs';

test('communication author guidance resolves in the public guide without publishing internals', () => {
  const repositoryRoot = fileURLToPath(new URL('../../', import.meta.url));
  const sourceToRoute = new Map(
    pages.map((page) => [page.sourcePath, routeForOutput(page.outputPath)]),
  );
  assert.equal(sourceToRoute.get('docs/Multiplayer.md'), '/guides/multiplayer/');
  assert.equal(sourceToRoute.has('docs/internal/GamemodeCommunications.md'), false);
  const sourcePath = 'docs/Multiplayer.md';
  const content = readFileSync(new URL(`../../${sourcePath}`, import.meta.url), 'utf8');
  // Scope publication checks to communication guidance so private-state guidance can evolve independently.
  const security = content.slice(content.indexOf('## Security boundary'));
  const guidance = /^## Gamemode communication groups\r?\n[\s\S]*?(?=^## |$(?![\s\S]))/mu.exec(content)?.[0] ?? '';
  const failures = [];
  const links = new LocalDocumentationLinkRewriter(repositoryRoot, sourceToRoute, failures);
  const rewritten = links.rewrite(sourcePath, security + '\n' + guidance);
  assert.deepEqual(failures, []);
  assert.match(rewritten, /\[host-enforced communication groups\]\(#gamemode-communication-groups\)/u);
  assert.ok(guidance, 'same-page communication section exists');
  assert.deepEqual(checkMarkdownLinks({ repositoryRoot, sourcePaths: [sourcePath] }), []);
  assert.match(guidance, /RouteCommunications\(policy\)/u);
  assert.match(guidance, /routing\.SetPolicy\(/u);
  assert.match(guidance, /not encryption against the host/u);
  assert.doesNotMatch(guidance, /0x04|IInternalSystemChannel|relay flag|wire format/iu);
});
