# UGC Automerge sidecar

The **writer** side of Robotopia's UGC Automerge live-sync channel. Publishes a `UgcExportProject` (the JSON the
Unity companion exports; see [`docs/UgcLiveSync.md`](../../docs/UgcLiveSync.md)) to an Automerge document on a
sync server, so the running game and the web editor can live-sync it. The game reads Automerge natively.

> The **local-folder** channel needs none of this. Use the sidecar only for parity with the web editor or
> remote/multi-user collaboration.

## Usage

Prefer the CLI wrapper (it finds this folder, runs `npm install` on first use, and streams output):

```powershell
robotopia ugc publish --file project.json --scene main
robotopia ugc watch   ./watch-folder --scene main
robotopia ugc check
```

Or run directly:

```bash
npm install
node index.mjs --file project.json --sync wss://your-sync-server --scene main
node index.mjs --watch ./watch-folder --doc automerge:existing-doc-id
node index.mjs --help | --check
```

- `--sync` defaults to `https://automerge-repo-sync-server-main.onrender.com` (auto-upgraded to `wss://`).
- Omit `--doc` to create a new document; the sidecar prints the document URL to paste into the in-game
  **UGC Live → Automerge** field.
- `--watch` re-publishes the newest `*.json` / `*.json.gz` in the folder on every change (pairs with the Unity
  companion's Live Sync export).

Requires Node.js 20+.
