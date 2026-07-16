import {
  existsSync,
  lstatSync,
  readFileSync,
  readdirSync,
} from 'node:fs';
import { dirname, extname, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

import {
  escapeRegExp,
  isExternalOrEmbedded,
  isWithinRoot,
} from './lib/link-utils.mjs';

const websiteRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const siteRoot = resolve(websiteRoot, 'dist');
if (!existsSync(resolve(siteRoot, 'index.html'))) {
  console.error('Built site is missing. Run `npm run build:all` first.');
  process.exit(1);
}

const htmlFiles = collectHtml(siteRoot);
const failures = new Set();
const documentCache = new Map();

for (const source of htmlFiles) {
  const document = readFileSync(source, 'utf8');
  const attributes = document.matchAll(/\b(?:href|src)=["']([^"'<>]+)["']/giu);
  for (const match of attributes) {
    const rawTarget = match[1];
    if (isExternalOrEmbedded(rawTarget)) {
      continue;
    }

    const [rawPath, rawFragment] = rawTarget.split('#', 2);
    let pathPart;
    let fragment;
    try {
      pathPart = decodeURIComponent(rawPath.split('?', 1)[0]);
      fragment = rawFragment ? decodeURIComponent(rawFragment) : '';
    } catch (error) {
      failures.add(`${display(source)}: invalid URL encoding in ${rawTarget}: ${error.message}`);
      continue;
    }

    const candidate = pathPart === ''
      ? source
      : pathPart.startsWith('/')
        ? resolve(siteRoot, `.${pathPart}`)
        : resolve(dirname(source), pathPart);
    if (!isWithinRoot(siteRoot, candidate)) {
      failures.add(`${display(source)}: local URL escapes the built site: ${rawTarget}`);
      continue;
    }

    const destination = resolveDestination(candidate);
    if (!destination) {
      failures.add(`${display(source)}: missing built target: ${rawTarget}`);
      continue;
    }

    if (fragment && extname(destination).toLowerCase() === '.html') {
      const targetDocument = documentCache.get(destination)
        ?? readFileSync(destination, 'utf8');
      documentCache.set(destination, targetDocument);
      const escapedFragment = escapeRegExp(fragment);
      if (!new RegExp(`\\bid=["']${escapedFragment}["']`, 'u').test(targetDocument)) {
        failures.add(`${display(source)}: missing built anchor #${fragment}: ${rawTarget}`);
      }
    }
  }
}

if (failures.size > 0) {
  for (const failure of [...failures].sort()) {
    console.error(failure);
  }
  console.error(`Built links: ${failures.size} failure(s)`);
  process.exit(1);
}

console.log(`Built links: pass (${htmlFiles.length} HTML files)`);

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

function resolveDestination(candidate) {
  if (existsSync(candidate)) {
    return lstatSync(candidate).isDirectory() ? resolve(candidate, 'index.html') : candidate;
  }

  if (extname(candidate) === '' && existsSync(`${candidate}.html`)) {
    return `${candidate}.html`;
  }

  const index = resolve(candidate, 'index.html');
  return existsSync(index) ? index : null;
}

function display(path) {
  return relative(siteRoot, path).split(sep).join('/');
}
