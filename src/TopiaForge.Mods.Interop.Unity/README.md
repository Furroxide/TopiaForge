# TopiaForge Unity interop (unstable)

This package is an explicit escape hatch for advanced compatibility and performance patches. It exposes native Unity
objects, requires the `unsafe-native` manifest capability, and is deliberately excluded from TopiaForge's V1 API
compatibility guarantee. It is never added by ordinary templates.

Prefer the safe services on `IModContext`. Use this package only when the framework cannot express the required native
operation, and isolate native code behind a small adapter so it can be replaced when game internals change.
