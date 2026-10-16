using System;
using System.IO;
using TopiaForge.Mods;

namespace TopiaForge.SandboxAcceptance.Native
{
    // Wire operation handlers. Bounded operations are accepted only for the prepared cycle and only while the
    // observer expects exactly that action; anything else ends the single-use transport.
    public sealed partial class SandboxAcceptanceNativeMod
    {
        private OperationResult<bool> Prepare(SandboxWireRequest request)
        {
            if (preparedScenario.Length != 0) throw new InvalidDataException("Cleanup is required before preparing another scenario cycle.");
            if (request.Cycle > SandboxNativeScenarios.Cycles(request.ScenarioId)) throw new InvalidDataException("Unsupported cycle.");
            // Preparation is bound to the actual current Sandbox session, not to an arbitrary scene.
            var safe = fixture!.Capture();
            if (safe.SessionPhase != "Idle" && (safe.TargetId != SandboxAcceptanceMod.SandboxTargetId || safe.SessionPhase != "Running"))
                throw new InvalidDataException("Preparation requires Idle or the actual running Sandbox target.");
            preparedScenario = request.ScenarioId; preparedCycle = request.Cycle;
            preparationPending = true; preparationDeadline = elapsed.ElapsedMilliseconds + 120000;
            if (safe.SessionPhase == "Idle") launch = NativeOwnerObservations.StartSandbox();
            ProgressPreparation();
            return OperationResult<bool>.Success(true);
        }

        // Cleanup reverses every bounded effect: the safe fixture releases its content, competing host, control cue
        // and control robot; the native side destroys its props and borrowed robot and resets the global theme.
        private OperationResult<bool> Cleanup()
        {
            preparationPending = false;
            var result = fixture!.Cleanup();
            try { objects!.Dispose(); } catch (Exception exception) { failures.Add("native-fixture-cleanup:" + exception.GetType().Name); }
            try { NativeAccessibility.Reset(); } catch (Exception exception) { failures.Add("accessibility-reset-failed:" + exception.GetType().Name); }
            if (result.Succeeded) { preparedScenario = ""; preparedCycle = 0; }
            return result;
        }

        private OperationResult<bool> ExecuteBounded(SandboxWireRequest request)
        {
            if (preparedScenario != request.ScenarioId || preparedCycle != request.Cycle) throw new InvalidDataException("Request is not bound to the prepared cycle.");
            SandboxWireOperations.Authorize(scenarios, request.ScenarioId, request.Cycle, request.Operation);
            switch (request.Operation)
            {
                case "unregister-source": return fixture!.UnregisterSource();
                case "request-session-stop":
                    return request.Cycle == 1 ? fixture!.RequestSessionStop() : request.Cycle == 2 ? fixture!.RequestSessionRestart() : fixture!.RequestReturnToMainMenu();
                case "external-write": return objects!.ExternalWrite();
                case "destroy-borrowed": return objects!.DestroyBorrowed();
                case "register-competing-host": return fixture!.RegisterCompetingHost();
                case "unregister-competing-host": return fixture!.UnregisterCompetingHost();
                case "control-cue": return fixture!.PlayControlCue();
                case "stop-control-cue": return fixture!.StopControlCue();
                case "spawn-control-robot": return fixture!.SpawnControlRobot();
                case "despawn-control-robot": return fixture!.DespawnControlRobot();
                case "accessibility-high-contrast":
                case "accessibility-scale-150":
                case "accessibility-reduced-motion":
                case "accessibility-reset":
                    return NativeAccessibility.Apply(request.Operation);
                default: throw new InvalidDataException("Unsupported wire operation.");
            }
        }
    }
}
