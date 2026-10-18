### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
TF1007 | TopiaForge | Error | Safe mods cannot reference the loader-owned UnityUi renderer; use Context.Ui.
TF1008 | TopiaForge | Error | Mods drain SDK tasks with an IsCompleted poll; blocking on one hangs the game.
TF1009 | TopiaForge | Warning | Mods read input edges in Update or LateUpdate; FixedUpdate misses or repeats them and never counts as the player's action.
