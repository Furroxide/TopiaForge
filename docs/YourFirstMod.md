# Your First Mod

A start-to-finish walkthrough: from nothing to a mod running inside Robotopia. Takes about ten minutes.

## Prerequisites

- **Robotopia installed** (the launcher detects the standard install location).
- **The `robotopia` CLI** — extract the release zip and add its root folder to `PATH`
  (see [Modding.md → Install the CLI](Modding.md#install-the-cli)).
- **.NET SDK 8+** — the only tool required to build mods. Node.js and Unity are optional and only used for
  UGC live-sync authoring; you don't need them today.

## 1. Check your machine

```sh
robotopia doctor
```

```text
Build mods (.NET, required to develop):
  [OK ] .NET SDK — v10.0.301
UGC live-sync (optional):
  [ X ] Unity Editor — Unity not detected (optional).
         Install Unity via Unity Hub only if you author UGC content or custom worlds. (https://unity.com/download)
  [OK ] Node.js — v23.8.0
Other:
  [OK ] Git — C:\Program Files\Git\cmd\git.exe
```

`[ X ]` on optional rows is fine — only the **.NET SDK** row must be `[OK ]`. If something is missing,
`robotopia setup` applies the safe fixes automatically and tells you exactly what to install by hand.

## 2. Create the mod

```sh
robotopia new mod yourname.firstmod --name "First Mod" --author "You"
```

```text
Created C:\...\yourname.firstmod
Next: edit robotopia.mod.json (or use `robotopia mod set|add|remove`), then validate with `robotopia check package ...`.
```

You get a complete, buildable project — no renaming or find-and-replace needed:

```text
yourname.firstmod/
├── .gitignore                 # ignores bin/, obj/, build artifacts
├── README.md
├── robotopia.mod.json         # the manifest ($schema included, so your editor autocompletes it)
├── robotopia.project.json     # dependency management (robotopia add package / restore)
├── YournameFirstmod.csproj
└── YournameFirstmodMod.cs     # the entry point: logs load, scene, and update events
```

Pick a different starting point with `--template gameplay|gamemode|service|ui|asset|world`
(`robotopia list templates` describes each).

## 3. Validate and pack

From inside `yourname.firstmod/`:

```sh
robotopia check package .
```

```text
First Mod 0.1.0 (yourname.firstmod)
```

No issues listed means the manifest and layout are valid. Now build it into an installable package —
`pack` compiles the C# project and zips it into a `.robotopiamod`:

```sh
robotopia pack
```

```text
C:\...\yourname.firstmod-0.1.0.robotopiamod
```

## 4. Install and run

```sh
robotopia install        # packs the current folder and installs it into the detected game
robotopia launch
```

If the game isn't auto-detected, set the `ROBOTOPIA_GAME_DIR` environment variable to your game folder and
retry (`robotopia doctor` shows what was detected). [Troubleshooting.md](Troubleshooting.md) covers the
per-platform paths, shell pitfalls, and `--game-dir`.

## 5. See it in game

In the main menu, click the **QuantumWorks** button or press **F10** to open the mod manager. Your mod is
listed and enabled; its log line ("Loaded") shows in the mod's log view.

## 6. Iterate

1. Edit `YournameFirstmodMod.cs` — say, change the log message.
2. `robotopia install` again (rebuilds and reinstalls).
3. `robotopia restart` — restarts the game with the new build.

Manage the manifest without hand-editing JSON — every change is validated before it's written:

```sh
robotopia mod set version 0.2.0
robotopia mod add tag physics
robotopia mod add dependency robotopia.worlds@">=0.3.0"
```

## 7. Publish it

Worth sharing? Publish it to the official registry so it shows up in everyone's launcher: validate to zero
findings, pack, host the file, and open a registry PR. The full walkthrough is
[PublishingYourMod.md](PublishingYourMod.md).

## Where next

- [Modding.md](Modding.md) — the full SDK reference: manifest fields, services, permissions, packaging.
- [UiKit.md](UiKit.md) — branded in-game UI (windows, HUDs, modals, toasts); press **F8** in game for the live gallery.
- [CustomWorlds.md](CustomWorlds.md) — ship a Unity world as a mod.
- [RobotKit.md](RobotKit.md) — spawn and control robots and standard agents.
- [UgcLiveSync.md](UgcLiveSync.md) — hot-reload level content into the running game.
