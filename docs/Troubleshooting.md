# Troubleshooting

The first stop for any problem:

```sh
robotopia doctor
```

It audits the toolchain (with versions and install links), the current project, and game compatibility, and
ends with a **Recommended actions:** section that maps every finding to a next step — run `robotopia setup`
for the safe auto-fixes, a pointer to this page when no game install is detected, and `No action needed.`
when everything is green. `robotopia setup` runs the same audit and applies the safe fixes automatically
(for example installing the Automerge sidecar dependencies); anything that needs a manual install is
spelled out.

## Game not detected — `ROBOTOPIA_GAME_DIR`

Detection order (first hit wins):

1. **`ROBOTOPIA_GAME_DIR`** environment variable — overrides everything.
2. **Windows default:** `%LOCALAPPDATA%\Tomato Cake\launcher\Robotopia`.
3. **macOS default:** `~/Library/Application Support/Tomato Cake/launcher` (the folder containing
   `Robotopia.app`).
4. **Linux:** no auto-detect — always set `ROBOTOPIA_GAME_DIR` or pass `--game-dir`.

`--game-dir` on a command always wins over the environment variable. Point either at the game folder itself
(the directory containing the game, not a launcher shortcut). Verify with `robotopia doctor` — it prints
what was detected.

### Setting the variable per shell

PowerShell, current session only:

```powershell
$env:ROBOTOPIA_GAME_DIR = 'D:\Games\Robotopia'
```

PowerShell, persistent — affects **new terminals only**:

```powershell
setx ROBOTOPIA_GAME_DIR "D:\Games\Robotopia"
```

bash/zsh, current session (add the line to `~/.bashrc` / `~/.zshrc` to persist):

```sh
export ROBOTOPIA_GAME_DIR="$HOME/Games/Robotopia"
```

Pitfalls:

- `setx` (and OS-level environment editing) does **not** update terminals that are already open — open a
  fresh one.
- Some shells and IDEs capture the environment when they were launched; if the variable "isn't there",
  restart the terminal — or sidestep the problem with `--game-dir`, which always wins.

## Linux / Proton

The game is the Windows build running under Proton/Wine:

- `ROBOTOPIA_GAME_DIR` / `--game-dir` must point at the **Windows-layout game folder inside the Proton
  prefix** — there is no auto-detect on Linux.
- Run the game with `WINEDLLOVERRIDES="winhttp=n,b"` so the BepInEx doorstop proxy loads.
- In the launcher, select the game folder inside your prefix and run Repair to install the Windows BepInEx;
  setting `wineCommand` in the launcher settings lets the launcher start the game directly.

## Logs

| Log | Location |
|---|---|
| Launcher | `<launcher data root>/logs/launcher.log` — Windows `%APPDATA%\RobotopiaLauncher\logs\launcher.log`, macOS/Linux `~/.robotopia_launcher/logs/launcher.log` |
| Game-side mod manager | `<game>/BepInEx/RobotopiaModManager/logs/manager.log` |

`manager.log` carries each mod's load lines and staged-action results; attach both files to bug reports.

## CLI exit codes

`0` success · `1` failure · `2` usage error — stable, for scripts and CI.
