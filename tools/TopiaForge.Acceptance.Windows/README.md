# Private Windows Sandbox observation broker

This non-distributable Windows x64 developer tool belongs to the [Sandbox automation handoff](../../docs/internal/launch/sandbox-automation-stages-3-6.md). It is not a launcher, release qualifier or general desktop automation endpoint.

```powershell
dotnet run --project tools/TopiaForge.Acceptance.Windows -c Release -- --self-test
dotnet publish tools/TopiaForge.Acceptance.Windows -c Release -r win-x64 --self-contained false -o '<new-private-tool-directory>'
```

The contract tests use synthetic inputs and WAV bytes; they do not open a device, inject input or start a game. The published executable requires the .NET 10 Windows x64 runtime.

## Native execution

Use `topiaforge acceptance sandbox run` through the admitted [local lane](../run-sandbox-local-lane.ps1). The CLI retains the original game process, installs exact package bytes, verifies isolation and starts this broker with `--request <private-request.json>`. A request binds the run challenge, manager session, original game creation token, executable path, Windows primary-token identity, reviewed isolation record, device profile and driver manifest. Do not manufacture a request to bypass admission.

`--probe <new-private-observation.json>` can collect actual primary-token/known-folder identity and the default render endpoint from a signed-in interactive QA session. It neither opens a microphone nor approves isolation. First sign-in, actual Unity persistence-root observation and the private provisioning review remain separate prerequisites.

The broker opens a query-only handle to the exact game process and checks its foreground window, client geometry, DPI, monitor and session before actuation. It only sends declared keys, measured widget clicks and bounded synthetic text. Missing, disabled or clipped controls stop the run; scrolling and unmeasured coordinates are not silently substituted. A challenge-bound local named pipe identifies the same game process and exposes only finite fixture operations. Native progress hints schedule actions; they do not establish passing results.

Screenshots capture the actual foreground game client as bounded BMP files. WASAPI captures the approved render endpoint's loopback mix, which can include other applications or sessions using that endpoint. The microphone is never opened. Keep that endpoint reserved during a run. Independent verification reads the actual WAV and BMP bytes, checks reported measurements and detects the fixture graph's 599 Hz cue and its disappearance after stop. These checks do not replace human judgments about image or sound quality.

Persistence observation is read-only and limited to the admitted QA root and explicit file allowlist. It records before/after hashes, watcher events and overflow. No save deletion, credential access, global mutation bridge or live backend call is part of this tool.

## Cancellation and evidence

The CLI writes a challenge-bound, immutable `broker-cancel.json` on interruption, output overflow or timeout. The broker cancels work, attempts bounded fixture cleanup and releases its own keys/buttons in `finally`. The parent waits twenty seconds before a last-resort termination of its original broker process. Forced termination cannot establish successful cleanup. The broker never terminates a game by PID; the parent owns game exit through its original launch receipt.

Private `broker-transcript.json`, `broker-result.json`, screenshots and WAV files preserve correlated actions, observations, first failures and cleanup status. Existing outputs are not overwritten. The independent CLI verifier binds source, runtime, package and artifact hashes and recomputes per-scenario outcomes. All evidence remains supplementary development evidence with `qualifiesRelease: false`; unavailable or unexecuted branches remain incomplete.

Native execution and end-to-end validation are pending QA admission. Current limitations and exact recorded tests belong in the [launch evidence](../../docs/internal/launch/Evidence.md), not an inferred release approval.
