using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine;

namespace TopiaForge.SandboxAcceptance.Native
{
    public sealed class SandboxAcceptanceNativeMod : TopiaForgeMod
    {
        private ISandboxAcceptanceFixture? fixture;
        private NativeFixtureObjects? objects;
        private NativeSandboxObservations? observations;
        private SandboxPipeServer? server;
        private IDisposable? uiObservation;
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        private readonly SandboxNativeScenarios scenarios = new SandboxNativeScenarios();
        private string challenge = "";
        private string managerSession = "";
        private string preparedScenario = "";
        private int preparedCycle;
        private Task<(bool Ok, string Message)>? launch;
        private bool preparationPending;
        private long preparationDeadline;
        private readonly List<string> failures = new List<string>();
        protected override void OnLoad()
        {
            var loaded = Context.Config.Load(new ConfigDefinition<SandboxAcceptanceConfig>(1, () => new SandboxAcceptanceConfig(), value =>
                !value.Enabled || value.Challenge != null && value.Challenge.Length == 64 && value.Challenge.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f')
                    ? OperationResult<bool>.Success(true) : OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "Invalid native challenge.")));
            if (!loaded.TryGetValue(out var config) || !config.Enabled) return;
            if (Context.Runtime.Platform != "windows") throw new InvalidOperationException("This observer requires the admitted Windows QA process.");
            if (!Context.Extensions.TryGet(out fixture) || fixture == null || fixture.Challenge != config.Challenge)
                throw new InvalidOperationException("The enabled safe fixture must share this challenge.");
            challenge = config.Challenge;
            objects = new NativeFixtureObjects(Context);
            observations = new NativeSandboxObservations(Context, objects);
            uiObservation = TopiaForgeUiDiagnostics.Enable(NativeSandboxObservations.SandboxOwner);
            Context.Events.SubscribeUpdate(_ => { ProgressPreparation(); server?.Pump(); });
            server = new SandboxPipeServer(challenge, Execute);
        }
        private byte[] Execute(SandboxWireRequest request)
        {
            if (Context.Lifetime.IsStopping || fixture == null || objects == null || observations == null) throw new ObjectDisposedException(nameof(SandboxAcceptanceNativeMod));
            var currentManager = NativeOwnerObservations.ManagerSession();
            if (managerSession.Length != 0 && currentManager != managerSession) throw new InvalidDataException("Actual manager generation changed.");
            managerSession = currentManager;
            OperationResult<bool>? result = null;
            if (request.Operation == "prepare")
            {
                if (preparedScenario.Length != 0) throw new InvalidDataException("Cleanup is required before preparing another scenario cycle.");
                if (request.Cycle > SandboxNativeScenarios.Cycles(request.ScenarioId)) throw new InvalidDataException("Unsupported cycle.");
                // Preparation is bound to the actual current Sandbox session, not to an arbitrary scene.
                var safe = fixture.Capture();
                if (safe.SessionPhase != "Idle" && (safe.TargetId != SandboxAcceptanceMod.SandboxTargetId || safe.SessionPhase != "Running"))
                    throw new InvalidDataException("Preparation requires Idle or the actual running Sandbox target.");
                preparedScenario = request.ScenarioId; preparedCycle = request.Cycle;
                preparationPending = true; preparationDeadline = elapsed.ElapsedMilliseconds + 120000;
                if (safe.SessionPhase == "Idle") launch = NativeOwnerObservations.StartSandbox();
                ProgressPreparation();
                result = OperationResult<bool>.Success(true);
            }
            else if (request.Operation == "cleanup")
            {
                preparationPending = false;
                result = fixture.Cleanup();
                try { objects.Dispose(); } catch (Exception exception) { failures.Add("native-fixture-cleanup:" + exception.GetType().Name); }
                if (result.Succeeded) { preparedScenario = ""; preparedCycle = 0; }
            }
            else if (request.Operation != "capture")
            {
                if (preparedScenario != request.ScenarioId || preparedCycle != request.Cycle) throw new InvalidDataException("Request is not bound to the prepared cycle.");
                if (request.Operation == "unregister-source")
                {
                    if (!scenarios.Matches(request.ScenarioId, request.Cycle) || scenarios.ExpectedAction != "unregister-source") throw new InvalidDataException("Source disposal is out of sequence.");
                    result = fixture.UnregisterSource();
                }
                if (request.Operation == "request-session-stop")
                {
                    if (!scenarios.Matches(request.ScenarioId, request.Cycle) || scenarios.ExpectedAction != "stop-world-session") throw new InvalidDataException("Session lifecycle request is out of sequence.");
                    result = request.Cycle == 1 ? fixture.RequestSessionStop() : request.Cycle == 2 ? fixture.RequestSessionRestart() : fixture.RequestReturnToMainMenu();
                }
            }
            var reasons = new List<string>(fixture.Capture().UnavailableReasons);
            reasons.AddRange(failures);
            reasons.Add("native-vehicle-adapter-unavailable"); reasons.Add("global-mutation-bridge-unavailable"); reasons.Add("genuine-remote-transport-unavailable");
            reasons.Add("package-specific-live-unload-unavailable:manager-disable-requires-restart");
            var facts = observations.Capture(fixture.Capture(), reasons);
            facts["prepared"] = !preparationPending && (fixture.Prepared || preparedScenario == "persistence-refusal");
            facts["projectId"] = fixture.ProjectId;
            facts["lifecycleRoute"] = request.ScenarioId == "lifecycle-routes" ? new[] { "stop", "restart", "return-to-main-menu" }[request.Cycle - 1] : "";
            facts["operationAccepted"] = result?.Succeeded ?? true;
            facts["operationErrorCode"] = result != null && !result.Succeeded ? result.ErrorCode.ToString() : "";
            if (result != null && !result.Succeeded) reasons.Add("fixture-operation-failed:" + result.ErrorCode);
            if (request.Operation == "begin")
            {
                if (!fixture.Prepared && request.ScenarioId != "persistence-refusal") throw new InvalidDataException("Fixture preparation is not complete.");
                scenarios.Begin(request.ScenarioId, request.Cycle, Time.frameCount, elapsed.ElapsedMilliseconds, facts);
            }
            if (request.Operation == "advance") scenarios.Advance(request.ScenarioId, request.Cycle, Time.frameCount, elapsed.ElapsedMilliseconds, facts);
            scenarios.Describe(facts);
            return SandboxWireCodec.Serialize(new Dictionary<string, object?> { ["schemaVersion"] = 1, ["challenge"] = challenge,
                ["sequence"] = request.Sequence, ["operation"] = request.Operation, ["scenarioId"] = request.ScenarioId, ["cycle"] = request.Cycle,
                ["frame"] = Time.frameCount, ["managerSessionId"] = managerSession, ["facts"] = facts,
                ["unavailableReasons"] = reasons.Distinct(StringComparer.Ordinal).ToArray() });
        }
        private void ProgressPreparation()
        {
            if (!preparationPending || fixture == null || objects == null) return;
            if (elapsed.ElapsedMilliseconds > preparationDeadline) { preparationPending = false; failures.Add("fixture-preparation-deadline-exceeded"); return; }
            try
            {
                if (launch != null)
                {
                    if (!launch.IsCompleted) return;
                    var launched = launch.GetAwaiter().GetResult(); launch = null;
                    if (!launched.Ok) { preparationPending = false; failures.Add("actual-manager-launch-failed"); return; }
                }
                var safe = fixture.Capture();
                if (safe.SessionPhase != "Running" || safe.TargetId != SandboxAcceptanceMod.SandboxTargetId) return;
                preparationPending = false;
                if (preparedScenario == "persistence-refusal") return;
                var created = objects.PrepareBorrowed(Context);
                if (!created.Succeeded) { failures.Add("borrowed-fixture-unavailable:" + created.ErrorCode); return; }
                var prepared = fixture.Prepare(objects);
                if (!prepared.Succeeded) failures.Add("safe-fixture-preparation-failed:" + prepared.ErrorCode);
            }
            catch (Exception exception) { preparationPending = false; failures.Add("fixture-preparation-failed:" + exception.GetType().Name); }
        }
        protected override void OnUnload()
        {
            // Each disposer is attempted independently; transport failure never skips owned native cleanup.
            try { server?.Dispose(); } finally
            { try { fixture?.Cleanup(); } finally { try { objects?.Dispose(); } finally { uiObservation?.Dispose(); } } }
        }
    }
}
