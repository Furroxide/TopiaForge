import {
  existsSync,
  lstatSync,
  readFileSync,
  readdirSync,
  rmSync,
  writeFileSync,
} from 'node:fs';
import { spawnSync } from 'node:child_process';
import { dirname, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { dartReferencePackages } from './build-dart-reference.mjs';

const scriptPath = fileURLToPath(import.meta.url);
const websiteRoot = resolve(dirname(scriptPath), '..');
const siteRoot = resolve(websiteRoot, 'dist');

export function addPagefindBody(html, tagName, id) {
  const escapedId = id.replace(/[.*+?^${}()|[\]\\]/gu, '\\$&');
  const openingTag = new RegExp(
    `<${tagName}\\b[^>]*\\bid=["']${escapedId}["'][^>]*>`,
    'iu',
  );
  const match = openingTag.exec(html);
  if (!match) {
    return { html, found: false, changed: false };
  }
  if (/\bdata-pagefind-body(?:\s|=|>)/iu.test(match[0])) {
    return { html, found: true, changed: false };
  }
  const replacement = match[0].replace(/>$/u, ' data-pagefind-body>');
  return {
    html: `${html.slice(0, match.index)}${replacement}${html.slice(match.index + match[0].length)}`,
    found: true,
    changed: true,
  };
}

export function addDocfxPagefindBody(html) {
  const openingTag = /<article\b[^>]*>/iu;
  const match = openingTag.exec(html);
  if (!match) {
    return { html, found: false, changed: false };
  }
  const existingId = /\bid=["']([^"']+)["']/iu.exec(match[0]);
  if (existingId && existingId[1] !== '_content') {
    return { html, found: false, changed: false };
  }
  const hasMarker = /\bdata-pagefind-body(?:\s|=|>)/iu.test(match[0]);
  const attributes = [
    existingId ? '' : ' id="_content"',
    hasMarker ? '' : ' data-pagefind-body',
  ].join('');
  const replacement = match[0].replace(/>$/u, `${attributes}>`);
  let updated = attributes === ''
    ? html
    : `${html.slice(0, match.index)}${replacement}${html.slice(match.index + match[0].length)}`;
  const htmlTag = /<html\b[^>]*>/iu.exec(updated);
  if (htmlTag && !/\blang=["'][^"']+["']/iu.test(htmlTag[0])) {
    const replacementHtmlTag = htmlTag[0].replace(/>$/u, ' lang="en">');
    updated = `${updated.slice(0, htmlTag.index)}${replacementHtmlTag}${updated.slice(htmlTag.index + htmlTag[0].length)}`;
  }
  return {
    html: updated,
    found: true,
    changed: updated !== html,
  };
}

export function isRedirectDocument(html) {
  return /<meta\b[^>]*\bhttp-equiv=["']?refresh["']?[^>]*>/iu.test(html);
}

export function isStandaloneDocument(html) {
  return /<html\b/iu.test(html);
}

export function injectApiSearchBodies(root = siteRoot) {
  const scopes = [
    {
      label: 'DocFX C#',
      root: resolve(root, 'api', 'csharp'),
      tagName: 'article',
      id: '_content',
      transform: addDocfxPagefindBody,
    },
    ...dartReferencePackages.map((entry) => ({
      label: `dartdoc ${entry.name}`,
      root: resolve(root, 'api', 'dart', entry.name),
      tagName: 'div',
      id: 'dartdoc-main-content',
      transform: (html) => addPagefindBody(html, 'div', 'dartdoc-main-content'),
    })),
  ];

  const counts = new Map();
  for (const scope of scopes) {
    if (!existsSync(scope.root) || !lstatSync(scope.root).isDirectory()) {
      throw new Error(`${scope.label} output is missing: ${scope.root}`);
    }
    let marked = 0;
    for (const path of collectHtml(scope.root)) {
      const html = readFileSync(path, 'utf8');
      if (!isStandaloneDocument(html) || isRedirectDocument(html)) {
        continue;
      }
      const result = scope.transform(html);
      if (!result.found) {
        throw new Error(
          `${display(root, path)} is missing ${scope.tagName}#${scope.id}; generator markup changed.`,
        );
      }
      if (result.changed) {
        writeFileSync(path, result.html, 'utf8');
      }
      marked++;
    }
    if (marked === 0) {
      throw new Error(`${scope.label} has no searchable HTML documents.`);
    }
    counts.set(scope.label, marked);
  }
  return counts;
}

export function buildSearch(root = siteRoot) {
  const portal = resolve(root, 'index.html');
  if (!existsSync(portal) || !/\bdata-pagefind-body\b/u.test(readFileSync(portal, 'utf8'))) {
    throw new Error('The Astro portal is missing its Pagefind body marker.');
  }
  const counts = injectApiSearchBodies(root);
  const output = resolve(root, 'pagefind');
  rmSync(output, { recursive: true, force: true });

  const runner = resolve(websiteRoot, 'node_modules', 'pagefind', 'lib', 'runner', 'bin.cjs');
  if (!existsSync(runner)) {
    throw new Error('Pinned Pagefind runner is missing; run `npm ci`.');
  }
  const result = spawnSync(
    process.execPath,
    [runner, '--site', root, '--output-path', output, '--silent'],
    { cwd: websiteRoot, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] },
  );
  const commandOutput = `${result.stdout ?? ''}${result.stderr ?? ''}`;
  if (commandOutput.length > 0) {
    process.stdout.write(commandOutput);
  }
  if (result.error) {
    throw new Error(`Unable to start Pagefind: ${result.error.message}`);
  }
  if (result.status !== 0) {
    throw new Error(`Pagefind failed with exit code ${result.status}.`);
  }
  if (!existsSync(resolve(output, 'pagefind.js'))) {
    throw new Error('Pagefind did not generate pagefind/pagefind.js.');
  }

  console.log(
    `Unified search: pass (${[...counts.entries()].map(([name, count]) => `${name}: ${count}`).join(', ')})`,
  );
}

function collectHtml(root) {
  const found = [];
  const pending = [root];
  while (pending.length > 0) {
    const current = pending.pop();
    for (const entry of readdirSync(current, { withFileTypes: true })) {
      const path = resolve(current, entry.name);
      if (entry.isDirectory()) {
        pending.push(path);
      } else if (entry.isFile() && entry.name.endsWith('.html')) {
        found.push(path);
      }
    }
  }
  return found.sort();
}

function display(root, path) {
  return relative(root, path).replaceAll('\\', '/');
}

if (process.argv[1] && resolve(process.argv[1]) === scriptPath) {
  try {
    buildSearch();
  } catch (error) {
    console.error(error instanceof Error ? error.message : error);
    process.exitCode = 1;
  }
}
