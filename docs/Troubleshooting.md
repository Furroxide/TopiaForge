---
title: Troubleshooting
description: Diagnose TopiaForge projects, Robotopia detection, platform, and log issues.
---

# Troubleshooting

The first stop for any problem:

```sh
topiaforge doctor
```

It audits the toolchain (with versions and install links), the current project, and Robotopia compatibility, and
ends with a **Recommended actions:** section that maps every finding to a next step — run `topiaforge setup`
for the safe auto-fixes, a pointer to this page when no Robotopia installation is detected, and `No action needed.`
when everything is green. `topiaforge setup` runs the same audit and applies the safe fixes automatically
(for example creating the developer data folder); anything that needs a manual install is
spelled out.

## Robotopia not detected — `ROBOTOPIA_GAME_DIR`

The standalone desktop launcher lists every validated candidate and preserves
the player's selected installation. Its discovery precedence is:

1. The saved selection.
2. **`ROBOTOPIA_GAME_DIR`** environment variable.
3. **Windows default:** `%LOCALAPPDATA%\Tomato Cake\launcher\Robotopia`.
4. **macOS default:** `~/Library/Application Support/Tomato Cake/launcher` (the folder containing
   `Robotopia.app`).
5. Steam libraries declared in `libraryfolders.vdf`, when an app manifest has
   the exact name and install directory `Robotopia`. This also finds the
   Windows game payload installed by Steam on Linux for Proton.

The launcher does not guess a Steam app id, recursively scan Wine/Proton
prefixes, or accept a folder name without validating the Robotopia payload. Use
**Select Folder** for another store or a custom location.

CLI commands use `--game-dir` as an exclusive explicit override. Without that
option they use the same repository adapters and take the highest-precedence
validated result: saved selection, `ROBOTOPIA_GAME_DIR`, Tomato Cake, then
Steam. Commands that can mutate an install should still receive `--game-dir`
when automation must target one exact installation.

`--game-dir` on a command always wins over the environment variable. Point either at the Robotopia folder itself
(the directory containing Robotopia, not a launcher shortcut). Verify with `topiaforge doctor` — it prints
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

Robotopia runs its Windows build under Proton/Wine:

- The desktop launcher can find a Steam-managed install from Steam's declared
  libraries. Otherwise, `ROBOTOPIA_GAME_DIR` / `--game-dir` must point at the
  **Windows-layout Robotopia folder used by Proton/Wine**.
- Run Robotopia with `WINEDLLOVERRIDES="winhttp=n,b"` so the BepInEx doorstop proxy loads.
- In the launcher, select the Robotopia folder inside your prefix and run Repair to install the Windows BepInEx;
  setting `wineCommand` in the launcher settings lets the launcher start Robotopia directly.

## No TopiaForge buttons on the main menu

TopiaForge draws those buttons on its own canvas, so they do not depend on the game's UI. If they
are missing, `manager.log` says so directly - look for the line that begins `Menu entry point`:

```text
Menu entry point mounted in scene 'TestCityStartMenu' on its own canvas at sorting order 30000.
```

A `NOT mounted` line names how many attempts were made and what UI surfaces the game had, and is
followed by a warning. Press F10 to reach the manager while you investigate; the overlay is
independent of the menu buttons. If mounting fails, TopiaForge opens the overlay for you rather than
leaving you with no way in.

## Logs

| Log | Location |
|---|---|
| Launcher | `<launcher data root>/logs/launcher.log` — Windows `%APPDATA%\TopiaForgeLauncher\logs\launcher.log`, macOS/Linux `~/.topiaforge_launcher/logs/launcher.log` |
| Robotopia-side mod manager | `<Robotopia>/BepInEx/TopiaForge/logs/manager.log` |

`manager.log` carries each mod's load lines and staged-action results; attach both files to bug reports.

## CLI exit codes

`0` success · `1` failure · `2` usage error — stable, for scripts and CI.
