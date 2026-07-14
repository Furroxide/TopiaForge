# Your First Mod

A start-to-finish walkthrough: from nothing to a mod running inside Robotopia. Takes about ten minutes.

## Prerequisites

- **Robotopia installed** (the launcher detects the standard install location).
- **The `topiaforge` CLI** — extract the release zip and add its root folder to `PATH`
  (see [Modding.md → Install the CLI](Modding.md#install-the-cli)).
- **.NET SDK 10.0.301** — the repository-pinned tool required to build mods. Node.js and Unity are optional and only used for
  UGC live-sync authoring; you don't need them today.

## 1. Check your machine

```sh
topiaforge doctor
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
`topiaforge setup` applies the safe fixes automatically and tells you exactly what to install by hand.

## 2. Create the mod

```sh
topiaforge new mod yourname.firstmod --name "First Mod" --author "You" --license MIT
```

```text
Created C:\...\yourname.firstmod
Next: edit topiaforge.mod.json (or use `topiaforge mod set|add|remove`), then validate with `topiaforge check package ...`.
```

You get a complete, buildable project — no renaming or find-and-replace needed:

```text
yourname.firstmod/
├── .gitignore                 # ignores bin/, obj/, build artifacts
├── README.md
├── topiaforge.mod.json         # the manifest ($schema included, so your editor autocompletes it)
├── topiaforge.project.json     # dependency management (topiaforge add package / restore)
├── YournameFirstmod.csproj
└── YournameFirstmodMod.cs     # the entry point: logs load, scene, and update events
```

Pick a different starting point with `--template gameplay|gamemode|service|ui|asset|world`
(`topiaforge list templates` describes each).

## 3. Validate and pack

From inside `yourname.firstmod/`:

```sh
topiaforge check package .
```

```text
First Mod 0.1.0 (yourname.firstmod)
```

No issues listed means the manifest and layout are valid. Now build it into an installable package —
`pack` compiles the C# project and zips it into a `.topiaforgemod`:

```sh
topiaforge pack
```

```text
C:\...\yourname.firstmod-0.1.0.topiaforgemod
```

## 4. Install and run

```sh
topiaforge install        # packs the current folder and installs it into the detected game
topiaforge launch
```

If the game isn't auto-detected, set the `ROBOTOPIA_GAME_DIR` environment variable to your game folder and
retry (`topiaforge doctor` shows what was detected). [Troubleshooting.md](Troubleshooting.md) covers the
per-platform paths, shell pitfalls, and `--game-dir`.

## 5. See it in game

In the main menu, click the **TopiaForge** button or press **F10** to open the mod manager. Your mod is
listed and enabled; its log line ("Loaded") shows in the mod's log view.

## 6. Iterate

1. Edit `YournameFirstmodMod.cs` — say, change the log message.
2. `topiaforge install` again (rebuilds and reinstalls).
3. `topiaforge restart` — restarts the game with the new build.

Manage the manifest without hand-editing JSON — every change is validated before it's written:

```sh
topiaforge mod set version 0.2.0
topiaforge mod add tag physics
topiaforge mod add dependency io.github.furroxide.topiaforge.worlds@">=0.3.0"
```

## 7. Publish it

Worth sharing? Publish a self-hosted registry or package source: validate to zero findings, pack, host the immutable
file, generate an index, and test its public URL. Official community submissions are closed for the initial release.
The full walkthrough is
[PublishingYourMod.md](PublishingYourMod.md).

## Where next

- [Modding.md](Modding.md) — the full SDK reference: manifest fields, services, permissions, packaging.
- [UiKit.md](UiKit.md) — branded in-game UI (windows, HUDs, modals, toasts); press **F8** in game for the live gallery.
- [CustomWorlds.md](CustomWorlds.md) — ship a Unity world as a mod.
- [RobotKit.md](RobotKit.md) — spawn and control robots and standard agents.
- [UgcLiveSync.md](UgcLiveSync.md) — hot-reload level content into the running game.
