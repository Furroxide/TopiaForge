### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
TFMP014 | TopiaForge.Multiplayer | Warning | Declared client-to-server command and object rates must fit the per-connection bandwidth budget.
TFMP015 | TopiaForge.Multiplayer | Warning | Handlers that run only on the session authority should not drive local UI, input, player, audio, or Unity state.
TFMP016 | TopiaForge.Multiplayer | Error | Multiplayer settings such as rates, payload limits, prediction, and delivery stay within their supported ranges.
