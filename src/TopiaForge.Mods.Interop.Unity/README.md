# TopiaForge Unity interop (unstable)

This package is an explicit escape hatch for advanced compatibility and performance patches. It exposes native Unity
objects, requires the `unsafe-native` manifest capability, and is deliberately excluded from TopiaForge's V1 API
compatibility guarantee. It is never added by ordinary templates.

Prefer the safe services on `IModContext`. Use this package only when the framework cannot express the required native
operation, and isolate native code behind a small adapter so it can be replaced when game internals change.

Harmony patch mods should call `Context.CreateHarmonyLease("purpose")` and apply reflection-resolved prefix,
postfix, transpiler, or finalizer methods through the returned lease. Its id is derived from the manifest owner,
unique within the process, and every patch carrying that id is removed automatically during mod-lifetime cleanup.
Patch application and first teardown run on Robotopia's game thread and are serialized through the lease; queue
worker-originated work through the SDK scheduler. Repeated disposal after teardown is a thread-safe no-op. The
owner id remains runtime-private, and the lease API does not require consumers to resolve 0Harmony at compile
time.
