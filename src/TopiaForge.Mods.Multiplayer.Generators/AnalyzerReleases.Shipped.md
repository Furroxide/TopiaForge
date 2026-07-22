## Release 1.0.0

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
TFMP001 | TopiaForge.Multiplayer | Error | Multiplayer contracts must be non-generic top-level partial classes.
TFMP002 | TopiaForge.Multiplayer | Error | Multiplayer commands use the generated transactional handler signature.
TFMP003 | TopiaForge.Multiplayer | Error | Wire payloads use supported deterministic DTO types.
TFMP004 | TopiaForge.Multiplayer | Error | Wire strings and collections have explicit generated bounds.
TFMP005 | TopiaForge.Multiplayer | Error | Owner-predicted handlers cannot use nondeterministic APIs.
TFMP006 | TopiaForge.Multiplayer | Error | Owner-predicted handlers cannot perform process-local side effects.
TFMP007 | TopiaForge.Multiplayer | Error | Contract wire identifiers are unique.
TFMP008 | TopiaForge.Multiplayer | Error | Generated command codecs fit the declared payload limit.
TFMP009 | TopiaForge.Multiplayer | Error | Generated wire identifiers are valid after namespacing.
TFMP010 | TopiaForge.Multiplayer | Error | Replicated-object handlers use the generated transactional signature.
TFMP011 | TopiaForge.Multiplayer | Error | Replicated-state and presentation-event codecs fit the hard transport limit.
TFMP012 | TopiaForge.Multiplayer | Error | Generated contract, command, object, and event member names do not collide.
TFMP013 | TopiaForge.Multiplayer | Error | Multiplayer contracts declare an explicit stable wire id.
