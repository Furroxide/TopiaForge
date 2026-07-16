import {
  existsSync,
  readFileSync,
  readdirSync,
} from 'node:fs';
import {
  dirname,
  extname,
  isAbsolute,
  relative,
  resolve,
  sep,
} from 'node:path';

export class TemplateSnippetRenderer {
  #failures;
  #repositoryRoot;
  #verifiedProjects = new Set();
  #renderedCount = 0;

  constructor(repositoryRoot, failures) {
    this.#repositoryRoot = repositoryRoot;
    this.#failures = failures;
  }

  get renderedCount() {
    return this.#renderedCount;
  }

  get verifiedProjectCount() {
    return this.#verifiedProjects.size;
  }

  render(pagePath, rawSnippetPath, requestedLanguage = '') {
    const snippetPath = rawSnippetPath.replaceAll('\\', '/');
    if (!snippetPath.startsWith('templates/mod/')) {
      this.#failures.push(
        `${pagePath}: snippets must come from a compiled mod template: ${snippetPath}`,
      );
      return '';
    }

    const absoluteSnippet = resolve(this.#repositoryRoot, snippetPath);
    const repositoryRelative = relative(this.#repositoryRoot, absoluteSnippet);
    if (repositoryRelative.startsWith(`..${sep}`) || repositoryRelative === '..') {
      this.#failures.push(`${pagePath}: snippet escapes the repository: ${snippetPath}`);
      return '';
    }

    if (!existsSync(absoluteSnippet)) {
      this.#failures.push(`${pagePath}: snippet source does not exist: ${snippetPath}`);
      return '';
    }

    if (!this.#validateCompiledProject(pagePath, snippetPath, absoluteSnippet)) {
      return '';
    }

    let snippet = readFileSync(absoluteSnippet, 'utf8').trimEnd();
    const substitutions = new Map([
      ['{{ASSEMBLY_NAME}}', 'Example.FirstMod'],
      ['{{TYPE_NAME}}', 'FirstMod'],
      ['{{DISPLAY_NAME}}', 'First Mod'],
      ['{{MOD_ID}}', 'example.firstmod'],
    ]);
    for (const [token, replacement] of substitutions) {
      snippet = snippet.replaceAll(token, replacement);
    }

    const unresolved = snippet.match(/\{\{[A-Z0-9_]+\}\}/u);
    if (unresolved) {
      this.#failures.push(
        `${pagePath}: snippet ${snippetPath} has unresolved token ${unresolved[0]}`,
      );
    }

    const language = requestedLanguage || languageFor(snippetPath);
    this.#renderedCount += 1;
    return `\`\`\`${language} title="${snippetPath}"\n${snippet}\n\`\`\``;
  }

  #validateCompiledProject(pagePath, snippetPath, absoluteSnippet) {
    const segments = snippetPath.split('/');
    if (
      segments.length < 4
      || segments[0] !== 'templates'
      || segments[1] !== 'mod'
      || !/^[a-z][a-z0-9-]*$/u.test(segments[2])
    ) {
      this.#failures.push(
        `${pagePath}: snippet is not inside a canonical mod template: ${snippetPath}`,
      );
      return false;
    }

    const templateName = segments[2];
    const templateRoot = resolve(this.#repositoryRoot, 'templates', 'mod', templateName);
    const templateRelative = relative(templateRoot, absoluteSnippet);
    if (
      templateRelative === '..'
      || templateRelative.startsWith(`..${sep}`)
      || templateRelative.length === 0
    ) {
      this.#failures.push(
        `${pagePath}: snippet escapes its template project: ${snippetPath}`,
      );
      return false;
    }

    if (this.#verifiedProjects.has(templateName)) {
      return true;
    }

    const templateManifest = resolve(templateRoot, 'template.json');
    const mainProjects = existsSync(templateRoot)
      ? readdirSync(templateRoot, { withFileTypes: true })
          .filter((entry) => entry.isFile() && entry.name.endsWith('.csproj'))
          .map((entry) => resolve(templateRoot, entry.name))
      : [];
    const testProjects = collectFiles(
      resolve(templateRoot, 'tests'),
      (path) => path.endsWith('.csproj'),
    );

    if (!existsSync(templateManifest) || mainProjects.length !== 1 || testProjects.length !== 1) {
      this.#failures.push(
        `${pagePath}: ${templateName} must have template.json, one main project, and one test project`,
      );
      return false;
    }

    const authoringProjects = collectFiles(
      templateRoot,
      (path) => path.endsWith('.csproj') && !path.includes(`${sep}tests${sep}`),
    );
    const invalidAuthoringProject = authoringProjects.some((projectPath) => {
      const project = readFileSync(projectPath, 'utf8');
      return !project.includes('<PackageReference')
        || !hasOnlyTemplateLocalProjectReferences(projectPath, project, templateRoot);
    });
    if (invalidAuthoringProject) {
      this.#failures.push(
        `${pagePath}: ${templateName} must compile through packaged SDK references; local contract references must remain inside the scaffold`,
      );
      return false;
    }

    const testProject = readFileSync(testProjects[0], 'utf8');
    if (
      !testProject.includes('<PackageReference')
      || !testProject.includes('TopiaForge.Mods.Testing')
      || !testProject.includes('NUnit')
    ) {
      this.#failures.push(
        `${pagePath}: ${templateName} must have a compiled NUnit testing-kit project`,
      );
      return false;
    }

    const workflowPath = resolve(this.#repositoryRoot, '.github/workflows/ci.yml');
    const workflow = existsSync(workflowPath) ? readFileSync(workflowPath, 'utf8') : '';
    const matrixPattern = new RegExp(
      `template:\\s*\\[[^\\]\\r\\n]*\\b${escapeRegex(templateName)}\\b[^\\]\\r\\n]*\\]`,
      'u',
    );
    if (!matrixPattern.test(workflow)) {
      this.#failures.push(
        `${pagePath}: ${templateName} is not compiled by the release scaffold CI matrix`,
      );
      return false;
    }

    this.#verifiedProjects.add(templateName);
    return true;
  }
}

function hasOnlyTemplateLocalProjectReferences(projectPath, project, templateRoot) {
  const references = project.matchAll(/<ProjectReference\s+Include="([^"]+)"/gu);
  for (const match of references) {
    const normalized = match[1].replaceAll('\\', '/');
    if (isAbsolute(normalized)) {
      return false;
    }

    const target = resolve(dirname(projectPath), normalized);
    const templateRelative = relative(templateRoot, target);
    if (
      templateRelative === '..'
      || templateRelative.startsWith(`..${sep}`)
      || !existsSync(target)
    ) {
      return false;
    }
  }
  return true;
}

function collectFiles(directory, predicate) {
  if (!existsSync(directory)) {
    return [];
  }

  const files = [];
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const path = resolve(directory, entry.name);
    if (entry.isDirectory()) {
      files.push(...collectFiles(path, predicate));
    } else if (entry.isFile() && predicate(path)) {
      files.push(path);
    }
  }
  return files;
}

function escapeRegex(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/gu, '\\$&');
}

function languageFor(path) {
  switch (extname(path).toLowerCase()) {
    case '.cs':
      return 'csharp';
    case '.csproj':
      return 'xml';
    case '.json':
      return 'json';
    default:
      return 'text';
  }
}
