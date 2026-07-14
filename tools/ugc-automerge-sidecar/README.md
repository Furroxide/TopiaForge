# UGC Automerge sidecar

The **writer** side of Robotopia's UGC Automerge live-sync channel. Publishes a `UgcExportProject` (the JSON the
Unity companion exports; see [`docs/UgcLiveSync.md`](../../docs/UgcLiveSync.md)) to an Automerge document on a
sync server, so the running game and the web editor can live-sync it. The game reads Automerge natively.

> The **local-folder** channel needs none of this. Use the sidecar only for parity with the web editor or
> remote/multi-user collaboration.

## Usage

Prefer the CLI wrapper (it finds this folder, runs the lockfile-backed `npm ci` on first use, and streams output):

```powershell
robotopia ugc publish --file project.json --scene main
robotopia ugc watch   ./watch-folder --scene main
robotopia ugc check
```

Or run directly:

```bash
npm ci
node index.mjs --file project.json --sync wss://your-sync-server --scene main
node index.mjs --watch ./watch-folder --doc automerge:existing-doc-id
node index.mjs --help | --check
```

- `--sync` defaults to `https://automerge-repo-sync-server-main.onrender.com` (auto-upgraded to `wss://`).
- Omit `--doc` to create a new document; the sidecar prints the document URL to paste into the in-game
  **UGC Live → Automerge** field.
- `--watch` re-publishes the newest `*.json` / `*.json.gz` in the folder on every change (pairs with the Unity
  companion's Live Sync export).
- Snapshots are capped at 16 MiB both before reading and after gzip expansion, matching the in-game live-sync
  default and preventing an export or compressed input from exhausting the publisher process.
- A detached watcher owns a random lease in its session file and exits when cleanup deletes that file or a newer
  watcher replaces the lease. Cleanup never kills a PID that may have been reused by another process.
- SIGINT/SIGTERM stop the watcher but leave its shared lease file in place. Deleting that path during signal
  handling could race with a replacement publisher; consumers reject the stale PID, and explicit cleanup safely
  revokes the lease.

Requires Node.js 20+.
