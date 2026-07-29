### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
TF1007 | TopiaForge | Error | Safe mods cannot reference the loader-owned UnityUi renderer; use Context.Ui.
TF1008 | TopiaForge | Error | Mods drain SDK tasks with an IsCompleted poll; blocking on one hangs the game.
