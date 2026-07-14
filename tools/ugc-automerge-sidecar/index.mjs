#!/usr/bin/env node
// TopiaForge UGC Automerge sidecar.
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
//     stdout. The same values are also printed on a single `TOPIAFORGE_UGC_SESSION {json}` line.
//   * Heavy deps are imported lazily so --help / --check work before `npm ci`.

import { newestProjectFile, readProject } from './project_file.mjs';
import {
  toWebSocketUrl,
  waitForPeer,
  waitForSocketDrain,
  withTimeout,
} from './network_policy.mjs';
import { establishPublisherSession } from './session_file.mjs';
import {
  createSessionLeaseToken,
  startSessionLeaseMonitor,
} from './session_lease.mjs';

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

function printHelp() {
  process.stdout.write(
    'TopiaForge UGC Automerge sidecar\n\n' +
      'Publish a UgcExportProject to an Automerge document the game can live-sync.\n\n' +
      'Usage:\n' +
      '  node index.mjs --file <project.json> [--sync <url>] [--doc <automerge-url>] [--scene <id>] [--session-file <path>]\n' +
      '  node index.mjs --watch <folder>      [--sync <url>] [--doc <automerge-url>] [--scene <id>] [--session-file <path>]\n' +
      '  node index.mjs --help | --check\n',
  );
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

  const syncUrl = toWebSocketUrl(args.sync, DEFAULT_SYNC);

  if (args.check) {
    let depsOk = true;
    try {
      const repoModule = await import('@automerge/automerge-repo');
      const networkModule = await import(
        '@automerge/automerge-repo-network-websocket'
      );
      depsOk =
        typeof repoModule.Repo === 'function' &&
        typeof networkModule.BrowserWebSocketClientAdapter === 'function';
    } catch {
      depsOk = false;
    }
    process.stdout.write(
      `sync server : ${syncUrl}\n` +
        `mode        : ${args.watch ? 'watch ' + args.watch : args.file ? 'file ' + args.file : '(none)'}\n` +
        `scene       : ${args.scene || '(first)'}\n` +
        `document    : ${args.doc || '(new)'}\n` +
        `deps        : ${depsOk ? 'installed' : 'NOT installed — run `npm ci` in this folder'}\n`,
    );
    if (!depsOk) process.exitCode = 1;
    return;
  }

  if (!args.file && !args.watch) {
    throw new Error('Provide --file <project.json> or --watch <folder> (see --help).');
  }

  const { Repo } = await import('@automerge/automerge-repo');
  const { BrowserWebSocketClientAdapter } = await import(
    '@automerge/automerge-repo-network-websocket'
  );

  const network = new BrowserWebSocketClientAdapter(syncUrl);
  const repo = new Repo({ network: [network] });
  await waitForPeer(network);

  function loadInitialProject() {
    const file = args.file || newestProjectFile(args.watch);
    if (!file) throw new Error(`No *.json/*.json.gz found in ${args.watch}`);
    return readProject(file);
  }

  const initial = loadInitialProject();
  let handle;
  if (args.doc) {
    handle = await withTimeout(repo.find(args.doc), 'Document lookup');
    handle.change((doc) => applyProject(doc, initial));
  } else {
    handle = repo.create(initial);
  }

  await withTimeout(handle.whenReady(), 'Document readiness');
  const editorUrl = `?project=${encodeURIComponent(handle.url)}${args.scene ? `&scene=${encodeURIComponent(args.scene)}` : ''}`;
  const publisherLeaseToken =
    args.sessionFile && args.watch ? createSessionLeaseToken() : '';
  const publicSession = {
    documentUrl: handle.url,
    syncUrl,
    sceneId: args.scene,
    editorUrl,
    lastPublishedUtc: new Date().toISOString(),
  };
  const session = {
    ...publicSession,
    ...(publisherLeaseToken
      ? { publisherLeaseToken, publisherPid: process.pid }
      : {}),
  };
  // The machine-readable line is a success signal parsed by the launcher/CLI.
  // Establish the optional watcher lease first so a failed write cannot launch
  // the game with a publisher that is already exiting.
  if (!establishPublisherSession(args.sessionFile, session, publicSession)) {
    throw new Error('Could not establish the publisher session lease.');
  }
  process.stdout.write(`Published to document: ${handle.url}\n`);
  process.stdout.write(`Paste this into the game's UGC Live (Automerge) field: ${handle.url}\n`);
  process.stdout.write(`Or an editor-style URL suffix: ${editorUrl}\n`);

  if (!args.watch) {
    await waitForSocketDrain(network);
    process.stdout.write('Done.\n');
    process.exit(0);
  }

  const chokidar = (await import('chokidar')).default;
  process.stdout.write(`Watching ${args.watch} — re-publishing on change. Ctrl+C to stop.\n`);
  let timer = null;
  let watcher;
  let stopping = false;
  let leaseMonitor;
  const stopPublisher = async (reason, { exitCode = 0 } = {}) => {
    if (stopping) return;
    stopping = true;
    leaseMonitor?.stop();
    if (timer) clearTimeout(timer);
    await watcher?.close();
    network.retryInterval = 0;
    try {
      network.socket?.close();
    } catch {
      // The process exits below even if the socket was never connected.
    }
    process.stdout.write(`Publisher stopped: ${reason}.\n`);
    process.exit(exitCode);
  };
  const republish = () => {
    if (timer) clearTimeout(timer);
    timer = setTimeout(() => {
      try {
        const next = readProject(newestProjectFile(args.watch));
        handle.change((doc) => applyProject(doc, next));
        const publishedAt = new Date().toISOString();
        process.stdout.write(`Re-published ${publishedAt}\n`);
      } catch (error) {
        process.stderr.write(`Skipped a bad snapshot: ${error.message}\n`);
      }
    }, 250);
  };
  watcher = chokidar
    .watch(args.watch, { ignoreInitial: true, awaitWriteFinish: { stabilityThreshold: 200 } })
    .on('add', republish)
    .on('change', republish);

  leaseMonitor = startSessionLeaseMonitor(
    args.sessionFile,
    publisherLeaseToken,
    { onRevoked: stopPublisher },
  );
  process.once('SIGINT', () => {
    void stopPublisher('SIGINT', { exitCode: 130 });
  });
  process.once('SIGTERM', () => {
    void stopPublisher('SIGTERM', { exitCode: 143 });
  });
}

main().catch((error) => {
  process.stderr.write(`${error.message}\n`);
  process.exit(1);
});
