import { readSync } from 'node:fs';

const defaultFileSystem = { readSync };

export function readBoundedDescriptor(
  descriptor,
  {
    maxBytes,
    chunkBytes = 64 * 1024,
    fileSystem = defaultFileSystem,
    createTooLargeError = () => new Error(`Input exceeds the ${maxBytes}-byte limit.`),
  },
) {
  if (!Number.isSafeInteger(maxBytes) || maxBytes <= 0) {
    throw new TypeError('maxBytes must be a positive safe integer.');
  }
  if (!Number.isSafeInteger(chunkBytes) || chunkBytes <= 0) {
    throw new TypeError('chunkBytes must be a positive safe integer.');
  }

  const chunks = [];
  let total = 0;
  const buffer = Buffer.allocUnsafe(Math.min(chunkBytes, maxBytes + 1));
  while (true) {
    const count = fileSystem.readSync(descriptor, buffer, 0, buffer.length, null);
    if (count === 0) {
      break;
    }
    total += count;
    if (total > maxBytes) {
      throw createTooLargeError();
    }
    chunks.push(Buffer.from(buffer.subarray(0, count)));
  }
  return Buffer.concat(chunks, total);
}
