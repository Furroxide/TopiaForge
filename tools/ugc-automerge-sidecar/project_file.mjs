import {
  closeSync,
  constants,
  existsSync,
  fstatSync,
  lstatSync,
  openSync,
  readdirSync,
} from 'node:fs';
import path from 'node:path';
import { gunzipSync } from 'node:zlib';

import { readBoundedDescriptor } from './bounded_read.mjs';

export const DEFAULT_MAX_PROJECT_BYTES = 16 * 1024 * 1024;

export function readProject(
  filePath,
  { maxBytes = DEFAULT_MAX_PROJECT_BYTES } = {},
) {
  if (!Number.isSafeInteger(maxBytes) || maxBytes <= 0) {
    throw new TypeError('Project snapshot maxBytes must be a positive safe integer.');
  }
  const before = lstatSync(filePath);
  if (!before.isFile()) {
    throw new Error(`Project snapshot must be a regular file: ${filePath}`);
  }

  // O_NOFOLLOW closes the ordinary symlink race on Unix. Comparing the opened
  // descriptor's identity with lstat also protects platforms where that flag is
  // unavailable: replacing a checked file with a link cannot redirect the read
  // to an unrelated local JSON file that would then be published remotely.
  const noFollow = constants.O_NOFOLLOW ?? 0;
  const descriptor = openSync(filePath, constants.O_RDONLY | noFollow);
  let bytes;
  try {
    const opened = fstatSync(descriptor);
    if (!opened.isFile() || opened.dev !== before.dev || opened.ino !== before.ino) {
      throw new Error(`Project snapshot changed while opening: ${filePath}`);
    }
    if (opened.size > maxBytes) {
      throw new Error(
        `Project snapshot exceeds the ${maxBytes}-byte input limit: ${filePath}`,
      );
    }
    bytes = readBoundedDescriptor(descriptor, {
      maxBytes,
      createTooLargeError: () => new Error(
        `Project snapshot exceeds the ${maxBytes}-byte input limit: ${filePath}`,
      ),
    });
  } finally {
    closeSync(descriptor);
  }

  let decoded = bytes;
  if (bytes.length >= 2 && bytes[0] === 0x1f && bytes[1] === 0x8b) {
    try {
      decoded = gunzipSync(bytes, { maxOutputLength: maxBytes });
    } catch (error) {
      throw new Error(
        `Project gzip is invalid or exceeds the ${maxBytes}-byte expanded limit: ${error.message}`,
      );
    }
  }
  const text = decoded.toString('utf8');
  const clean = text.charCodeAt(0) === 0xfeff ? text.slice(1) : text;
  const project = JSON.parse(clean);
  if (typeof project !== 'object' || project === null || Array.isArray(project)) {
    throw new Error('Project JSON must be an object (a UgcExportProject).');
  }
  return project;
}

export function newestProjectFile(folder) {
  if (!existsSync(folder)) return '';
  let newest = '';
  let newestMs = -1;
  for (const name of readdirSync(folder)) {
    const lower = name.toLowerCase();
    if (!lower.endsWith('.json') && !lower.endsWith('.json.gz')) continue;
    const full = path.join(folder, name);
    let stat;
    try {
      stat = lstatSync(full);
    } catch {
      // A concurrently removed export is simply absent from this scan.
      continue;
    }
    if (!stat.isFile()) continue;
    if (stat.mtimeMs > newestMs) {
      newestMs = stat.mtimeMs;
      newest = full;
    }
  }
  return newest;
}
