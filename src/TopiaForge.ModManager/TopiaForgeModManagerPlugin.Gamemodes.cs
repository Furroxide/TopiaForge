using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using UnityEngine.SceneManagement;

namespace TopiaForge.ModManager
{
    public sealed partial class TopiaForgeModManagerPlugin
    {
        private const float WorldLaunchMaxWaitSeconds = 12f;
        private bool pendingStartupLaunch;
        private bool startupCommandIssued;
        private LaunchSelection? pendingRememberedSelection;
        private float pendingWorldLaunchWait;
        private LaunchDiscoveryGate launchDiscovery = null!;
        public IReadOnlyList<ModLaunchTargetDeclaration> GetLaunchTargets() => runtime == null
            ? Array.Empty<ModLaunchTargetDeclaration>() : runtime.LaunchTargets;
        public IReadOnlyList<LaunchTargetPreview> GetLaunchPreviews()
        {
            var snapshot = runtime.CaptureSessionRuntime();
            return LaunchTargetPreviewBuilder.Build(snapshot.Profile, snapshot.Observation, snapshot.Bindings);
        }
        public IWorldSessionService? GetSessionService() => runtime == null ? null : runtime.Sessions;
        public LaunchSelection ReadLaunchSelection() => state.LaunchSelection ?? LaunchSelection.UnresolvedLegacy("{}");
        public LaunchSelectionResolution ResolveRememberedSelection()
        {
            var snapshot = runtime.CaptureSessionRuntime();
            return LaunchSelectionResolver.Resolve(ReadLaunchSelection(), snapshot.Profile, snapshot.Observation, snapshot.Bindings);
        }
        public void SaveLaunchSelection(LaunchSelection selection, bool autoLoadOnStart)
        {
            if (!CanSaveState) throw new InvalidOperationException(StatePersistenceError);
            state.LaunchSelection = selection ?? throw new ArgumentNullException(nameof(selection));
            state.AutoLoadOnStart = autoLoadOnStart;
            SaveState();
        }
        private void ArmWorldLaunch()
        {
            pendingRememberedSelection = WorldLaunchArming.Resolve(startupSelection, state.LaunchSelection, state.AutoLoadOnStart);
            pendingStartupLaunch = hasLauncherCommand || startupSelection.SafeMode || pendingRememberedSelection != null;
            pendingWorldLaunchWait = WorldLaunchMaxWaitSeconds;
            launchDiscovery = new LaunchDiscoveryGate(runtime.NativeDispatcher, DiscoverForLaunchAsync);
        }
        private void UpdatePendingWorldLaunch(float deltaTime)
        {
            var scene = SceneManager.GetActiveScene().name;
            var atMenu = GameScenes.IsMainMenuScene(scene);
            if (atMenu) _ = launchDiscovery.Start();
            if (!pendingStartupLaunch) return;
            var menuCommand = rejectedLauncherCommand || startupSelection.SafeMode
                || launchProfile?.Command == "main-menu" || pendingRememberedSelection?.Kind == "main-menu";
            pendingWorldLaunchWait -= deltaTime;
            if (!menuCommand && !atMenu && pendingWorldLaunchWait > 0f) return;
            pendingStartupLaunch = false;
            _ = DispatchStartupAsync(!menuCommand && !atMenu && GameScenes.IsNonGameplayScene(scene), !menuCommand);
        }
        private async Task DiscoverForLaunchAsync()
        {
            try { await runtime.DiscoverWorldsAsync(); PublishRuntimeObservations(); }
            catch (Exception error) { managerLogger.Error(error, "World discovery failed."); }
        }
        private async Task DispatchStartupAsync(bool menuUnavailable, bool requireDiscovery)
        {
            try
            {
                await launchDiscovery.AfterAsync(async stillCurrent =>
                {
                    if (!ready) return;
                    startupCommandIssued = true;
                    if (rejectedLauncherCommand)
                    {
                        var rejectedId = launchProfile?.RequestId ?? rejectedCorrelation?.RequestId;
                        var rejectedCommand = launchProfile?.Command ?? rejectedCorrelation?.Command;
                        if (rejectedId != null && rejectedCommand != null)
                            await runtime.Sessions.RejectCommandAsync(rejectedId, rejectedCommand,
                                ModErrorCode.InvalidArgument, "The launcher request was rejected: " + rejectionReason);
                        if (ready && stillCurrent()) await runtime.Sessions.ReturnToMainMenuAsync();
                        return;
                    }
                    if (launchProfile != null)
                    {
                        var result = menuUnavailable
                            ? await runtime.Sessions.RejectCommandAsync(launchProfile.RequestId, launchProfile.Command, ModErrorCode.TimedOut,
                                "The game menu was not ready before the launch deadline.")
                            : await runtime.Sessions.ExecuteCommandAsync(launchProfile);
                        if (!result.Succeeded) managerLogger.Warn("Launcher session did not start: " + result.ErrorMessage);
                        var recoveryChanged = (startupSelection.SafeMode && !launchProfile.SafeMode)
                            || startupSelection.QuarantinedPackageId.Length != 0;
                        if (!result.Succeeded && recoveryChanged && ready && stillCurrent())
                            await runtime.Sessions.ReturnToMainMenuAsync();
                        return;
                    }
                    if (startupSelection.SafeMode || pendingRememberedSelection?.Kind == "main-menu")
                    { await runtime.Sessions.ReturnToMainMenuAsync(); return; }
                    if (menuUnavailable) { managerLogger.Warn("Remembered selection was not started: the menu was not ready."); return; }
                    var snapshot = runtime.CaptureSessionRuntime();
                    var resolved = LaunchSelectionResolver.Resolve(pendingRememberedSelection!, snapshot.Profile, snapshot.Observation, snapshot.Bindings);
                    if (!resolved.Available || resolved.Request == null)
                    { managerLogger.Warn("Remembered selection needs repair: " + resolved.RepairMessage); return; }
                    var launched = await runtime.LaunchTargetAsync(resolved.Request);
                    if (!launched.Succeeded) managerLogger.Warn("Remembered target did not start: " + launched.ErrorMessage);
                }, requireDiscovery && !menuUnavailable);
            }
            catch (Exception error) { managerLogger.Error(error, "Startup launch command failed."); }
        }
        private void CancelPendingStartup()
        {
            pendingStartupLaunch = false;
            if (!startupCommandIssued && launchProfile != null)
            {
                startupCommandIssued = true;
                _ = runtime.Sessions.RejectCommandAsync(launchProfile.RequestId, launchProfile.Command, ModErrorCode.Cancelled,
                    "A manager command superseded the pending launcher command.");
            }
        }
        public async Task<(bool Ok, string Message)> LaunchTarget(string targetId,
            string? worldOverride = null, string? transitionOverride = null)
        {
            try
            {
                return await launchDiscovery.ExplicitAsync(async () =>
                {
                    CancelPendingStartup();
                    if (!ready) return (false, "The runtime is not ready to launch a target.");
                    var result = await runtime.LaunchTargetAsync(new LaunchRequest(targetId, worldOverride, transitionOverride));
                    return (result.Succeeded, result.Succeeded ? "Launch target reached Running." : result.ErrorMessage);
                });
            }
            catch (Exception error)
            {
                managerLogger.Error(error, "Target launch failed.");
                return (false, "Target launch failed: " + error.Message);
            }
        }
        public async Task<(bool Ok, string Message)> ReturnToMainMenu()
        {
            try
            {
                return await launchDiscovery.ExplicitAsync(async () =>
                {
                    CancelPendingStartup();
                    if (!ready) return (false, "The runtime is not ready to return to the main menu.");
                    var result = await runtime.Sessions.ReturnToMainMenuAsync();
                    return (result.Succeeded, result.Succeeded ? "Returned to the main menu." : result.ErrorMessage);
                });
            }
            catch (Exception error) { return (false, "Return to menu failed: " + error.Message); }
        }
    }
}
