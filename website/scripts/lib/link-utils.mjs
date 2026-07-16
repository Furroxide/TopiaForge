import { relative, sep } from 'node:path';

export function isExternalOrEmbedded(target) {
  return target.startsWith('//')
    || /^[A-Za-z][A-Za-z0-9+.-]*:/u.test(target)
    || target.startsWith('data:');
}

export function isWithinRoot(root, candidate) {
  const rootRelative = relative(root, candidate);
  return rootRelative !== '..'
    && !rootRelative.startsWith(`..${sep}`)
    && !rootRelative.startsWith(`..${sep === '/' ? '\\' : '/'}`);
}

export function escapeRegExp(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/gu, '\\$&');
}

export function lineNumberAt(content, offset) {
  return content.slice(0, offset).split('\n').length;
}
