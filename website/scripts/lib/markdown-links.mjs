import {
  existsSync,
  lstatSync,
  readFileSync,
} from 'node:fs';
import { dirname, extname, relative, resolve, sep } from 'node:path';

import {
  isExternalOrEmbedded,
  isRegularFile,
  isWithinRoot,
} from './link-utils.mjs';

export function checkMarkdownLinks({ repositoryRoot, sourcePaths }) {
  const failures = [];
  const anchorCache = new Map();

  for (const sourcePath of sourcePaths) {
    const source = resolve(repositoryRoot, sourcePath);
    if (!isWithinRoot(repositoryRoot, source)) {
      failures.push(`${sourcePath}: source escapes the repository`);
      continue;
    }
    if (!existsSync(source)) {
      continue;
    }
    if (!isRegularFile(source)) {
      failures.push(`${sourcePath}: source is not a regular file`);
      continue;
    }

    const content = readFileSync(source, 'utf8');
    for (const { line, target: rawTarget } of extractMarkdownTargets(content)) {
      const target = unwrapTarget(rawTarget);
      if (isIgnoredTarget(target)) {
        continue;
      }

      const [rawLocation, rawFragment] = target.split('#', 2);
      const rawPath = rawLocation.split('?', 1)[0];
      let decodedPath;
      let decodedFragment;
      try {
        decodedPath = decodeURIComponent(rawPath);
        decodedFragment = rawFragment == null ? null : decodeURIComponent(rawFragment);
      } catch (error) {
        failures.push(
          `${display(repositoryRoot, source)}:${line}: invalid URL encoding (${error.message}): ${target}`,
        );
        continue;
      }

      const destination = decodedPath === ''
        ? source
        : decodedPath.startsWith('/')
          ? resolve(repositoryRoot, `.${decodedPath}`)
          : resolve(dirname(source), decodedPath);
      if (!isWithinRoot(repositoryRoot, destination)) {
        failures.push(
          `${display(repositoryRoot, source)}:${line}: target escapes the repository: ${target}`,
        );
        continue;
      }
      if (!existsSync(destination)) {
        failures.push(`${display(repositoryRoot, source)}:${line}: missing target: ${target}`);
        continue;
      }
      const destinationStatus = lstatSync(destination);
      if (!destinationStatus.isFile() && !destinationStatus.isDirectory()) {
        failures.push(
          `${display(repositoryRoot, source)}:${line}: target is not a regular file: ${target}`,
        );
        continue;
      }

      if (
        !decodedFragment
        || !destinationStatus.isFile()
        || extname(destination).toLowerCase() !== '.md'
      ) {
        continue;
      }

      let anchors = anchorCache.get(destination);
      if (!anchors) {
        anchors = githubAnchors(readFileSync(destination, 'utf8'));
        anchorCache.set(destination, anchors);
      }
      if (!anchors.has(decodedFragment.toLowerCase())) {
        failures.push(
          `${display(repositoryRoot, source)}:${line}: missing anchor #${decodedFragment} in ${display(repositoryRoot, destination)}`,
        );
      }
    }
  }

  return failures;
}

export function extractMarkdownTargets(content) {
  const targets = [];
  let inFence = false;
  const lines = content.split(/\r?\n/u);
  for (let index = 0; index < lines.length; index += 1) {
    const line = lines[index];
    if (/^\s*(```|~~~)/u.test(line)) {
      inFence = !inFence;
      continue;
    }
    if (inFence) {
      continue;
    }

    const searchable = line.replace(/`[^`]*`/gu, '');
    for (const match of searchable.matchAll(
      /!?\[[^\]]*\]\(\s*(<[^>]+>|[^\s)]+)(?:\s+[^)]*)?\)/gu,
    )) {
      targets.push({ line: index + 1, target: match[1] });
    }

    const definition = searchable.match(/^\s*\[[^\]]+\]:\s*(<[^>]+>|\S+)/u);
    if (definition) {
      targets.push({ line: index + 1, target: definition[1] });
    }

    for (const match of searchable.matchAll(
      /<(?:a|img)\b[^>]+(?:href|src)=["']([^"']+)["'][^>]*>/giu,
    )) {
      targets.push({ line: index + 1, target: match[1] });
    }
  }
  return targets;
}

export function githubAnchors(content) {
  const counts = new Map();
  const anchors = new Set();
  let inFence = false;
  for (const line of content.split(/\r?\n/u)) {
    if (/^\s*(```|~~~)/u.test(line)) {
      inFence = !inFence;
      continue;
    }
    if (inFence) {
      continue;
    }

    const match = line.match(/^\s{0,3}#{1,6}\s+(.+?)\s*#*\s*$/u);
    if (!match) {
      continue;
    }

    const anchor = match[1]
      .replace(/<[^>]+>/gu, '')
      .replace(/!?\[([^\]]+)\]\([^)]*\)/gu, '$1')
      .replace(/[`*_~]/gu, '')
      .toLowerCase()
      .replace(/[^\p{L}\p{N}\-_ ]/gu, '')
      .trim()
      .replace(/\s+/gu, '-');
    if (!anchor) {
      continue;
    }

    const suffix = counts.get(anchor) ?? 0;
    counts.set(anchor, suffix + 1);
    anchors.add(suffix === 0 ? anchor : `${anchor}-${suffix}`);
  }
  return anchors;
}

function unwrapTarget(rawTarget) {
  const target = rawTarget.trim();
  return target.startsWith('<') && target.endsWith('>')
    ? target.slice(1, -1).trim()
    : target;
}

function isIgnoredTarget(target) {
  return target === ''
    || isExternalOrEmbedded(target)
    || target.includes('{{')
    || target.includes('${');
}

function display(repositoryRoot, path) {
  return relative(repositoryRoot, path).split(sep).join('/');
}
