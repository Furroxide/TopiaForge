import { lstatSync, readFileSync } from 'node:fs';
import { extname, relative, resolve, sep } from 'node:path';
import { parseAllDocuments } from 'yaml';

import { isWithinRoot } from './link-utils.mjs';

const DEFAULT_MAX_FILE_BYTES = 16 * 1024 * 1024;

export function checkRepositoryData(
  {
    repositoryRoot,
    sourcePaths,
    maxFileBytes = DEFAULT_MAX_FILE_BYTES,
  },
) {
  if (!Number.isSafeInteger(maxFileBytes) || maxFileBytes <= 0) {
    throw new TypeError('maxFileBytes must be a positive safe integer.');
  }

  const failures = [];
  let parsedCount = 0;
  for (const sourcePath of sourcePaths) {
    const source = resolve(repositoryRoot, sourcePath);
    if (!isWithinRoot(repositoryRoot, source)) {
      failures.push(`${sourcePath}: source escapes the repository`);
      continue;
    }
    let stat;
    try {
      stat = lstatSync(source);
    } catch (error) {
      failures.push(`${display(repositoryRoot, source)}: cannot be read: ${error.message}`);
      continue;
    }
    if (!stat.isFile()) {
      failures.push(`${display(repositoryRoot, source)}: must be a regular file`);
      continue;
    }
    if (stat.size > maxFileBytes) {
      failures.push(
        `${display(repositoryRoot, source)}: exceeds the ${maxFileBytes}-byte repository-data limit`,
      );
      continue;
    }

    const content = readFileSync(source, 'utf8');
    const extension = extname(source).toLowerCase();
    try {
      if (extension === '.json') {
        JSON.parse(content);
      } else if (extension === '.yml' || extension === '.yaml') {
        const documents = parseAllDocuments(content, {
          prettyErrors: true,
          strict: true,
          uniqueKeys: true,
        });
        for (const document of documents) {
          if (document.errors.length > 0) {
            throw document.errors[0];
          }
          document.toJS({ maxAliasCount: 100 });
        }
      } else {
        failures.push(`${display(repositoryRoot, source)}: unsupported repository-data extension`);
        continue;
      }
      parsedCount += 1;
    } catch (error) {
      failures.push(`${display(repositoryRoot, source)}: ${error.message}`);
    }
  }

  return { failures, parsedCount };
}

function display(repositoryRoot, path) {
  return relative(repositoryRoot, path).split(sep).join('/');
}
