#!/usr/bin/env node
// Robotopia UGC Automerge sidecar.
//
// Publishes a UgcExportProject (the exact JSON the game imports — see docs/UgcLiveSync.md) to an Automerge
// document on a sync server, so the running game's UgcLiveSyncController (Automerge channel) and the web editor
// can live-sync it. This is the WRITER side; the game reads Automerge natively. The local-folder channel needs
// none of this — it's only for full web-editor parity / remote collaboration.
//
// Usage:
//   node index.mjs --file <project.json> [--sync <url>] [--doc <automerge-url>] [--scene <id>]
//   node index.mjs --watch <folder>      [--sync <url>] [--doc <automerge-url>] [--scene <id>]
//   node index.mjs --help | --check
//
// Notes:
//   * --sync defaults to https://automerge-repo-sync-server-main.onrender.com (auto-upgraded to wss://).
//   * Omit --doc to create a new document; the sidecar prints the document URL + an editor URL to paste into
//     the game's "UGC Live" panel (Automerge mode).
//   * --watch re-publishes the newest *.json/*.json.gz in <folder> on every change (pairs with the Unity
//     companion exporting to that folder).
//   * --session-file <path> atomically writes the live connection values (document URL, sync URL, scene,
//     editor URL suffix, lastPublishedUtc) as JSON so the launcher/CLI can auto-detect them without parsing
//     stdout. The same values are also printed on a single `ROBOTOPIA_UGC_SESSION {json}` line.
//   * Heavy deps are imported lazily so --help / --check work before `npm install`.

import { readFileSync, writeFileSync, renameSync, existsSync, statSync, readdirSync } from 'node:fs';
import { gunzipSync } from 'node:zlib';
import path from 'node:path';

function parseArgs(argv) {
  const args = {
    sync: '', doc: '', scene: '', file: '', watch: '', sessionFile: '', help: false, check: false,
  };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    switch (a) {
      case '--help': case '-h': args.help = true; break;
      case '--check': args.check = true; break;
      case '--sync': args.sync = argv[++i] ?? ''; break;
      case '--doc': args.doc = argv[++i] ?? ''; break;
      case '--scene': args.scene = argv[++i] ?? ''; break;
      case '--file': args.file = argv[++i] ?? ''; break;
      case '--watch': args.watch = argv[++i] ?? ''; break;
      case '--session-file': args.sessionFile = argv[++i] ?? ''; break;
      default: throw new Error(`Unknown argument: ${a}`);
    }
  }
  return args;
}

const DEFAULT_SYNC = 'https://automerge-repo-sync-server-main.onrender.com';

function toWebSocketUrl(url) {
  const u = (url || DEFAULT_SYNC).trim();
  if (u.startsWith('wss://') || u.startsWith('ws://')) return u;
  if (u.startsWith('https://')) return 'wss://' + u.slice('https://'.length);
  if (u.startsWith('http://')) return 'ws://' + u.slice('http://'.length);
  return 'wss://' + u;
}

function printHelp() {
  process.stdout.write(
    'Robotopia UGC Automerge sidecar\n\n' +
      'Publish a UgcExportProject to an Automerge document the game can live-sync.\n\n' +
      'Usage:\n' +
      '  node index.mjs --file <project.json> [--sync <url>] [--doc <automerge-url>] [--scene <id>] [--session-file <path>]\n' +
      '  node index.mjs --watch <folder>      [--sync <url>] [--doc <automerge-url>] [--scene <id>] [--session-file <path>]\n' +
      '  node index.mjs --help | --check\n',
  );
}

// Atomically writes the live connection values to disk (temp + rename) so a reader never sees a partial file.
function writeSessionFile(sessionPath, session) {
  if (!sessionPath) return;
  try {
    const tmp = sessionPath + '.tmp';
    writeFileSync(tmp, JSON.stringify(session, null, 2));
    renameSync(tmp, sessionPath);
  } catch (error) {
    process.stderr.write(`Could not write session file: ${error.message}\n`);
  }
}

function readProject(filePath) {
  const bytes = readFileSync(filePath);
  const text =
    bytes.length >= 2 && bytes[0] === 0x1f && bytes[1] === 0x8b
      ? gunzipSync(bytes).toString('utf8')
      : bytes.toString('utf8');
  const clean = text.charCodeAt(0) === 0xfeff ? text.slice(1) : text;
  const project = JSON.parse(clean);
  if (typeof project !== 'object' || project === null || Array.isArray(project)) {
    throw new Error('Project JSON must be an object (a UgcExportProject).');
  }
  return project;
}

function newestProjectFile(folder) {
  if (!existsSync(folder)) return '';
  let newest = '';
  let newestMs = -1;
  for (const name of readdirSync(folder)) {
    if (!name.endsWith('.json') && !name.endsWith('.json.gz')) continue;
    const full = path.join(folder, name);
    const ms = statSync(full).mtimeMs;
    if (ms > newestMs) {
      newestMs = ms;
      newest = full;
    }
  }
  return newest;
}

// Replace the document contents with the project (game-side differ handles incremental updates between snapshots).
function applyProject(doc, project) {
  for (const key of Object.keys(doc)) {
    if (!(key in project)) delete doc[key];
  }
  for (const [key, value] of Object.entries(project)) {
    doc[key] = value;
  }
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  if (args.help) {
    printHelp();
    return;
  }

  const syncUrl = toWebSocketUrl(args.sync);

  if (args.check) {
    let depsOk = true;
    try {
      await import('@automerge/automerge-repo');
      await import('@automerge/automerge-repo-network-websocket');
    } catch {
      depsOk = false;
    }
    process.stdout.write(
      `sync server : ${syncUrl}\n` +
        `mode        : ${args.watch ? 'watch ' + args.watch : args.file ? 'file ' + args.file : '(none)'}\n` +
        `scene       : ${args.scene || '(first)'}\n` +
        `document    : ${args.doc || '(new)'}\n` +
        `deps        : ${depsOk ? 'installed' : 'NOT installed — run `npm install` in this folder'}\n`,
    );
    return;
  }

  if (!args.file && !args.watch) {
    throw new Error('Provide --file <project.json> or --watch <folder> (see --help).');
  }

  const { Repo } = await import('@automerge/automerge-repo');
  const { WebSocketClientAdapter } = await import('@automerge/automerge-repo-network-websocket');

  const repo = new Repo({ network: [new WebSocketClientAdapter(syncUrl)] });

  function loadInitialProject() {
    const file = args.file || newestProjectFile(args.watch);
    if (!file) throw new Error(`No *.json/*.json.gz found in ${args.watch}`);
    return readProject(file);
  }

  const initial = loadInitialProject();
  let handle;
  if (args.doc) {
    handle = await repo.find(args.doc);
    handle.change((doc) => applyProject(doc, initial));
  } else {
    handle = repo.create(initial);
  }

  await handle.whenReady();
  const editorUrl = `?project=${encodeURIComponent(handle.url)}${args.scene ? `&scene=${encodeURIComponent(args.scene)}` : ''}`;
  const session = {
    documentUrl: handle.url,
    syncUrl,
    sceneId: args.scene,
    editorUrl,
    lastPublishedUtc: new Date().toISOString(),
  };
  // Machine-readable line the launcher/CLI parse to auto-fill the game's config (so the user never copies a URL).
  process.stdout.write(`ROBOTOPIA_UGC_SESSION ${JSON.stringify(session)}\n`);
  process.stdout.write(`Published to document: ${handle.url}\n`);
  process.stdout.write(`Paste this into the game's UGC Live (Automerge) field: ${handle.url}\n`);
  process.stdout.write(`Or an editor-style URL suffix: ${editorUrl}\n`);
  writeSessionFile(args.sessionFile, session);

  if (!args.watch) {
    // Give the network a moment to flush the initial sync, then exit.
    await new Promise((resolve) => setTimeout(resolve, 1500));
    process.stdout.write('Done.\n');
    process.exit(0);
  }

  const chokidar = (await import('chokidar')).default;
  process.stdout.write(`Watching ${args.watch} — re-publishing on change. Ctrl+C to stop.\n`);
  let timer = null;
  const republish = () => {
    if (timer) clearTimeout(timer);
    timer = setTimeout(() => {
      try {
        const next = readProject(newestProjectFile(args.watch));
        handle.change((doc) => applyProject(doc, next));
        session.lastPublishedUtc = new Date().toISOString();
        process.stdout.write(`Re-published ${session.lastPublishedUtc}\n`);
        writeSessionFile(args.sessionFile, session);
      } catch (error) {
        process.stderr.write(`Skipped a bad snapshot: ${error.message}\n`);
      }
    }, 250);
  };
  chokidar
    .watch(args.watch, { ignoreInitial: true, awaitWriteFinish: { stabilityThreshold: 200 } })
    .on('add', republish)
    .on('change', republish);
}

main().catch((error) => {
  process.stderr.write(`${error.message}\n`);
  process.exit(1);
});
