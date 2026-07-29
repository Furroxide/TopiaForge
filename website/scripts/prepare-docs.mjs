import {
  copyFileSync,
  existsSync,
  mkdirSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import {
  forbiddenPublicContractTerms,
  pages,
  routeForOutput,
} from './docs/catalog.mjs';
import { LocalDocumentationLinkRewriter } from './docs/local-links.mjs';
import { TemplateSnippetRenderer } from './docs/template-snippets.mjs';
import { lineNumberAt } from './lib/link-utils.mjs';

const websiteRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const repositoryRoot = resolve(websiteRoot, '..');
const outputRoot = resolve(websiteRoot, 'src/content/docs');
const publishedSourceRoot = resolve(websiteRoot, 'public/source');
const checkOnly = process.argv.includes('--check');

const snippetPattern = /<!--\s*topiaforge-snippet\s+path="([^"]+)"(?:\s+language="([^"]+)")?\s*-->/gu;
const sourceToRoute = new Map(
  pages.map((entry) => [entry.sourcePath, routeForOutput(entry.outputPath)]),
);
const failures = [];
const renderedPages = [];
const snippets = new TemplateSnippetRenderer(repositoryRoot, failures);
const links = new LocalDocumentationLinkRewriter(
  repositoryRoot,
  sourceToRoute,
  failures,
);

for (const entry of pages) {
  const absoluteSource = resolve(repositoryRoot, entry.sourcePath);
  if (!existsSync(absoluteSource)) {
    failures.push(`${entry.sourcePath}: canonical source does not exist`);
    continue;
  }

  let content = readFileSync(absoluteSource, 'utf8');
  if (!content.startsWith('---\n')) {
    if (entry.frontmatter) {
      content = `${entry.frontmatter}\n\n${content}`;
    } else {
      failures.push(`${entry.sourcePath}: Starlight frontmatter is required`);
    }
  }

  if (!/\bRobotopia\b/u.test(content)) {
    failures.push(`${entry.sourcePath}: public documentation must identify Robotopia by name`);
  }

  if (entry.enforceV1Contract) {
    validatePublicContract(entry.sourcePath, content);
  }

  content = content.replace(
    snippetPattern,
    (_, snippetPath, language) => snippets.render(entry.sourcePath, snippetPath, language),
  );
  content = links.rewrite(entry.sourcePath, content);

  if (content.includes('topiaforge-snippet')) {
    failures.push(`${entry.sourcePath}: unresolved snippet directive`);
  }

  renderedPages.push({ ...entry, content });
}

if (failures.length > 0) {
  for (const failure of failures) {
    console.error(failure);
  }
  process.exitCode = 1;
} else if (checkOnly) {
  console.log(
    `Documentation content: pass (${pages.length} pages, ${snippets.renderedCount} snippets from ${snippets.verifiedProjectCount} CI-compiled template projects)`,
  );
} else {
  writeGeneratedDocumentation();
  console.log(`Prepared ${renderedPages.length} Starlight pages from canonical sources.`);
}

function validatePublicContract(sourcePath, content) {
  for (const [pattern, description] of forbiddenPublicContractTerms) {
    pattern.lastIndex = 0;
    const match = pattern.exec(content);
    if (match) {
      failures.push(
        `${sourcePath}:${lineNumberAt(content, match.index)}: ${description}: ${match[0]}`,
      );
    }
  }
}

function writeGeneratedDocumentation() {
  rmSync(outputRoot, { recursive: true, force: true });
  rmSync(publishedSourceRoot, { recursive: true, force: true });
  for (const entry of renderedPages) {
    const destination = resolve(outputRoot, entry.outputPath);
    mkdirSync(dirname(destination), { recursive: true });
    writeFileSync(destination, entry.content, 'utf8');
  }
  for (const [sourcePath, absoluteSource] of links.publishedAssets) {
    const destination = resolve(publishedSourceRoot, sourcePath);
    mkdirSync(dirname(destination), { recursive: true });
    copyFileSync(absoluteSource, destination);
  }
}
