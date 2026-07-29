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
  isRegularFile,
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
let checkedHtmlCount = 0;

for (const source of htmlFiles) {
  const document = readFileSync(source, 'utf8');
  if (!/<html\b/iu.test(document)) {
    continue;
  }
  checkedHtmlCount++;
  const attributes = document.matchAll(
    /(?<![A-Za-z0-9_-])(?:href|src)=["']([^"'<>]+)["']/giu,
  );
  for (const match of attributes) {
    const rawTarget = decodeHtmlAttribute(match[1]);
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

console.log(`Built links: pass (${checkedHtmlCount} standalone HTML files)`);

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
    const status = lstatSync(candidate);
    if (status.isFile()) {
      return candidate;
    }
    if (!status.isDirectory()) {
      return null;
    }
    const index = resolve(candidate, 'index.html');
    return isRegularFile(index) ? index : null;
  }

  if (extname(candidate) === '' && isRegularFile(`${candidate}.html`)) {
    return `${candidate}.html`;
  }

  const index = resolve(candidate, 'index.html');
  return isRegularFile(index) ? index : null;
}

function display(path) {
  return relative(siteRoot, path).split(sep).join('/');
}

function decodeHtmlAttribute(value) {
  return value.replace(
    /&(?:#(\d+)|#x([0-9a-f]+)|(amp|quot|apos|lt|gt));/giu,
    (_entity, decimal, hexadecimal, named) => {
      if (decimal) return String.fromCodePoint(Number.parseInt(decimal, 10));
      if (hexadecimal) return String.fromCodePoint(Number.parseInt(hexadecimal, 16));
      return {
        amp: '&',
        quot: '"',
        apos: "'",
        lt: '<',
        gt: '>',
      }[named.toLowerCase()];
    },
  );
}
