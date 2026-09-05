# Slice 5: Binding and world adapters

Begin only after slice 4 merges into `dev`. Read the
[canonical brief](../../GamemodeContractRedesign.md),
[evidence ledger](../Status.md), and [common execution rules](README.md).
Implement production binding and world loading adapters on the new foundations.
Keep first-party V6 activation and the canonical alias flip for slice 6.

## Declaration binding

- Bind a declared implementation only from its owning package's verified assembly.
  Require a public, concrete type implementing the expected interface with a
  public parameterless constructor. Reject foreign assemblies, abstract/open
  generic types, inaccessible constructors, and wrong interface kinds.
- Treat declaration identity as authoritative; do not require factories to declare
  a second ID. Resolve constructor/startup work inside the prepared session scope.
- Publish a package's binding availability and removal atomically. Keep failed
  declarations visible with structured availability reasons; do not leave stale
  launchable entries when a package fails binding or unloads.
- Revalidate immutable package identities and resolve against loaded manifests
  before doing scene work. Report a changed package set as an actionable failure.

## Worlds and discovery

- Implement `IGamemodeFactory.StartAsync(session, cancellationToken)` returning an
  operation result with one controller, and `IWorldContentProvider.LoadAsync`
  returning an owned instance with actual scene identity, resolved spawn, and
  cleanup. Implement bounded discovery and loading through `IWorldDiscoverySource`.
- Await scene, content, player, and spawn readiness before entering gameplay.
  Fail startup for missing or ambiguous authored markers. Do not substitute a
  plausible spawn or confuse the requested scene with the scene actually loaded.
- Implement the actual generated Open Sandbox provider, including its environment
  and kill plane. Restore both existing discovery sources through the new adapter
  contract; preserve family and concrete instance identity and producer ownership.
- Route all provider transitions through the shared executor and make provider
  resources session owned, including partial creation and canceled load cleanup.
- Keep production registration paths reusable by synthetic packages and upcoming
  generated templates; do not expose test-only shortcuts as the acceptance path.

## Acceptance

- Load synthetic verified package assemblies through the same production binder
  and orchestrator. Test valid factories/providers/discovery, wrong-owner and
  wrong-interface types, unavailable bindings, constructor exceptions, unload,
  and atomic publication/removal. No manual test-only factory registration.
- Test readiness ordering, missing/duplicate markers, provider failure after
  allocating resources, cancellation with native work in flight, package-set
  drift before scene work, and cleanup that throws while later cleanup continues.
- Verify bounded discovery cancellation/failure and correct family/instance
  ownership; discoveries cannot create targets or make a disabled package usable.
- Run relevant C# tests, Release, rebuilt API baseline/release-surface checks,
  and any affected Dart fixtures. Keep V5 consumers and templates operational.
- Record Open Sandbox geometry/spawn, imported/discovered content, and actual
  native readiness as game verification pending until live evidence exists.
- Update the ledger and submit only this slice. Branch slice 6 after its merge.
