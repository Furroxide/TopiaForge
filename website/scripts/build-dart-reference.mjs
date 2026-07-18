import {
  existsSync,
  mkdirSync,
  rmSync,
  writeFileSync,
} from 'node:fs';
import { spawnSync } from 'node:child_process';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptPath = fileURLToPath(import.meta.url);
const websiteRoot = resolve(dirname(scriptPath), '..');
const repositoryRoot = resolve(websiteRoot, '..');
const outputRoot = resolve(websiteRoot, 'dist', 'api', 'dart');

export const dartReferencePackages = Object.freeze([
  Object.freeze({
    name: 'launcher_domain',
    label: 'Launcher domain',
    description: 'Framework-independent launcher models, planning, and policy.',
    dependencyCommand: 'dart',
    enforceLockfile: true,
  }),
  Object.freeze({
    name: 'launcher_data',
    label: 'Launcher data',
    description: 'Filesystem, process, archive, persistence, and repository services.',
    dependencyCommand: 'dart',
    enforceLockfile: true,
  }),
  Object.freeze({
    name: 'launcher_ui',
    label: 'Launcher UI',
    description: 'Shared Flutter BLoC state and presentation components.',
    dependencyCommand: 'flutter',
    enforceLockfile: false,
  }),
]);

export function createDartdocPlan(
  root = repositoryRoot,
  destination = outputRoot,
) {
  return dartReferencePackages.map((entry) => ({
    ...entry,
    packageRoot: resolve(root, 'packages', entry.name),
    output: resolve(destination, entry.name),
    dependencyArgs: [
      'pub',
      'get',
      ...(entry.enforceLockfile ? ['--enforce-lockfile'] : []),
    ],
    dartdocArgs: [
      'doc',
      '--validate-links',
      `--output=${resolve(destination, entry.name)}`,
      '.',
    ],
  }));
}

export function dartdocHasDiagnostics(output) {
  if (/^\s*(?:warning|error):/imu.test(output)) {
    return true;
  }
  return /\b[1-9]\d*\s+(?:warnings?|errors?)\b/iu.test(output);
}

export function renderDartReferenceLanding(packages = dartReferencePackages) {
  const cards = packages.map((entry) => `
        <li>
          <a href="/api/dart/${entry.name}/">${entry.label}</a>
          <p>${entry.description}</p>
        </li>`).join('');
  return `<!doctype html>
<html lang="en">
  <head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <title>TopiaForge Dart API reference</title>
    <meta name="description" content="API reference for the shared TopiaForge launcher packages.">
    <style>
      :root { color-scheme: light dark; font-family: system-ui, sans-serif; }
      body { margin: 0 auto; max-width: 72rem; padding: 2rem; line-height: 1.55; }
      nav { display: flex; flex-wrap: wrap; gap: 1rem; }
      ul { display: grid; gap: 1rem; padding: 0; list-style: none; }
      li { border: 1px solid currentColor; border-radius: .5rem; padding: 1rem; }
      li p { margin-bottom: 0; }
      a { font-weight: 650; }
    </style>
  </head>
  <body>
    <nav aria-label="Documentation sections">
      <a href="/">Guides</a>
      <a href="/api/csharp/">C# API</a>
    </nav>
    <main id="dart-reference" data-pagefind-body>
      <h1>TopiaForge Dart API reference</h1>
      <p>Public API documentation for the shared launcher packages. Application and CLI internals are intentionally excluded.</p>
      <ul>${cards}
      </ul>
    </main>
  </body>
</html>
`;
}

export function buildDartReference() {
  rmSync(outputRoot, { recursive: true, force: true });
  mkdirSync(outputRoot, { recursive: true });

  for (const entry of createDartdocPlan()) {
    if (!existsSync(resolve(entry.packageRoot, 'pubspec.yaml'))) {
      throw new Error(`Dart reference package is missing: ${entry.packageRoot}`);
    }
    run(toolCommand(entry.dependencyCommand), entry.dependencyArgs, entry.packageRoot);
    const dartdocOutput = run(toolCommand('dart'), entry.dartdocArgs, entry.packageRoot);
    if (dartdocHasDiagnostics(dartdocOutput)) {
      throw new Error(`dartdoc reported warnings or errors for ${entry.name}.`);
    }
    requireGeneratedFile(resolve(entry.output, 'index.html'));
    requireGeneratedFile(resolve(entry.output, entry.name, 'index.html'));
  }

  writeFileSync(
    resolve(outputRoot, 'index.html'),
    renderDartReferenceLanding(),
    'utf8',
  );
  console.log(`Dart reference: pass (${dartReferencePackages.length} packages)`);
}

function toolCommand(name) {
  if (name === 'dart') {
    return process.env.TOPIAFORGE_DART_BIN || 'dart';
  }
  if (name === 'flutter') {
    return process.env.TOPIAFORGE_FLUTTER_BIN || 'flutter';
  }
  return name;
}

function run(command, args, cwd) {
  const result = spawnSync(command, args, {
    cwd,
    encoding: 'utf8',
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  const output = `${result.stdout ?? ''}${result.stderr ?? ''}`;
  if (output.length > 0) {
    process.stdout.write(output);
  }
  if (result.error) {
    throw new Error(`Unable to start ${command}: ${result.error.message}`);
  }
  if (result.status !== 0) {
    throw new Error(`${command} ${args.join(' ')} failed with exit code ${result.status}.`);
  }
  return output;
}

function requireGeneratedFile(path) {
  if (!existsSync(path)) {
    throw new Error(`dartdoc did not generate the required file: ${path}`);
  }
}

if (process.argv[1] && resolve(process.argv[1]) === scriptPath) {
  try {
    buildDartReference();
  } catch (error) {
    console.error(error instanceof Error ? error.message : error);
    process.exitCode = 1;
  }
}
