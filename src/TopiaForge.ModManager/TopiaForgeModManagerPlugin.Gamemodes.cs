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
        private WorldLaunchIntent? pendingWorldLaunch;
        private float pendingWorldLaunchWait;
        private LegacyLaunchDiscovery launchDiscovery = null!;
        public IReadOnlyList<ModLaunchTargetDeclaration> GetLaunchTargets() => runtime == null
            ? Array.Empty<ModLaunchTargetDeclaration>() : runtime.LaunchTargets;
        public IWorldSessionService? GetSessionService() => runtime == null ? null : runtime.Sessions;
        public WorldLaunchSettings ReadWorldLaunchSettings() => state?.WorldLaunch ?? new WorldLaunchSettings();
        public void SaveWorldLaunchSettings(WorldLaunchSettings settings)
        {
            state.WorldLaunch = settings ?? throw new ArgumentNullException(nameof(settings));
            SaveState();
        }
        private void ArmWorldLaunch()
        {
            pendingWorldLaunch = WorldLaunchArming.Resolve(launchProfile, state?.WorldLaunch);
            pendingWorldLaunchWait = WorldLaunchMaxWaitSeconds;
            launchDiscovery = new LegacyLaunchDiscovery(DiscoverForLaunchAsync);
        }
        private void UpdatePendingWorldLaunch(float deltaTime)
        {
            var scene = SceneManager.GetActiveScene().name;
            var atMenu = GameScenes.IsMainMenuScene(scene);
            if (atMenu) _ = launchDiscovery.Start();
            var intent = pendingWorldLaunch;
            if (intent == null) return;
            pendingWorldLaunchWait -= deltaTime;
            if (!atMenu && pendingWorldLaunchWait > 0f) return;
            pendingWorldLaunch = null;
            if (!atMenu && GameScenes.IsNonGameplayScene(scene))
            {
                managerLogger.Warn("Launch selection was not started because the menu was never reached.");
                return;
            }
            _ = StartLegacyIntentAsync(intent);
        }
        private async Task DiscoverForLaunchAsync()
        {
            try { await runtime.DiscoverWorldsAsync(); }
            catch (Exception error) { managerLogger.Error(error, "World discovery failed."); }
        }
        private async Task StartLegacyIntentAsync(WorldLaunchIntent intent)
        {
            try
            {
                await launchDiscovery.AfterAsync(async () =>
                {
                    // Shutdown may finish the discovery that this one-shot launch was awaiting.
                    if (!ready) return;
                    var snapshot = runtime.CaptureSessionRuntime();
                    var translated = LegacyWorldLaunchAdapter.Resolve(snapshot.Profile, snapshot.Observation, intent);
                    if (!translated.TryGetValue(out var request)) { managerLogger.Warn(translated.ErrorMessage); return; }
                    var result = await runtime.LaunchTargetAsync(request);
                    if (result.Succeeded) managerLogger.Info("Launch target '" + request.TargetId + "' reached Running.");
                    else managerLogger.Warn("Launch target failed: " + result.ErrorMessage);
                });
            }
            catch (Exception error) { managerLogger.Error(error, "The legacy launch selection could not start."); }
        }
        public async Task<(bool Ok, string Message)> LaunchTarget(string targetId,
            string? worldOverride = null, string? transitionOverride = null)
        {
            try
            {
                var result = await runtime.LaunchTargetAsync(new LaunchRequest(targetId, worldOverride, transitionOverride));
                return (result.Succeeded, result.Succeeded ? "Launch target reached Running." : result.ErrorMessage);
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
                var result = await runtime.Sessions.ReturnToMainMenuAsync();
                return (result.Succeeded, result.Succeeded ? "Returned to the main menu." : result.ErrorMessage);
            }
            catch (Exception error) { return (false, "Return to menu failed: " + error.Message); }
        }
    }
}
