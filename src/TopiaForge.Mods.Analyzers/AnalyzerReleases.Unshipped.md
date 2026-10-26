### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
TF1007 | TopiaForge | Error | Safe mods cannot reference the loader-owned UnityUi renderer; use Context.Ui.
TF1008 | TopiaForge | Error | Mods await SDK tasks or drain them with an IsCompleted poll; blocking on one hangs the game.
TF1009 | TopiaForge | Warning | Mods read input edges in Update or LateUpdate; FixedUpdate misses or repeats them and never counts as the player's action.
TF1010 | TopiaForge | Error | SDK APIs require a manifest loader floor at least as new as their RequiresTopiaForge metadata.
TF1011 | TopiaForge | Error | Map worlds require a bounded published id or packed map file, a required Worlds dependency and scene replacement.
