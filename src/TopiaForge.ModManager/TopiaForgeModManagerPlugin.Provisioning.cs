using System;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager
{
    public sealed partial class TopiaForgeModManagerPlugin
    {
        private bool ObserveProvisioningOnly()
        {
            var requestId = Environment.GetEnvironmentVariable(ProvisioningRuntimeObservation.EnvironmentVariable);
            if (requestId == null) return false;
            // Any selected provisioning mode remains inert, including malformed/refused requests.
            // No manager storage, remembered state, packages, UI or acceptance ACK is initialized.
            try
            {
                var root = new ManagerPaths(BepInEx.Paths.BepInExRootPath).Root;
                initializationLifetime.ObserveProvisioning(() => ProvisioningRuntimeObservation.Observe(requestId,
                    Environment.GetEnvironmentVariable(AcceptanceIsolationGate.EnvironmentVariable),
                    Environment.GetEnvironmentVariable(ProfileLaunchConfigurationV4.EnvironmentVariable),
                    BepInEx.Paths.GameRootPath, BepInEx.Paths.BepInExRootPath, root,
                    () => AcceptanceRuntimeProbe.Read(BepInEx.Paths.GameRootPath, BepInEx.Paths.BepInExRootPath,
                        root, UnityEngine.Application.persistentDataPath), DateTimeOffset.UtcNow));
                LogProvisioningPhase("Observation recorded; quit deferred until first Update.");
            }
            catch (Exception error)
            {
                LogProvisioningPhase("Observation refused (" + error.GetType().Name + "). Manager remains inactive.");
                // Refusal cannot arm a quit request; the launcher retains its bounded original-process ownership.
            }
            return true;
        }

        private void RequestProvisioningQuitOnUpdate()
        {
            if (!initializationLifetime.ProvisioningQuitPending) return;
            try
            {
                initializationLifetime.TryRequestProvisioningQuit(() =>
                {
                    LogProvisioningPhase("First Update reached; requesting Unity quit once.");
                    // The successful observation names this exact original native process.
                    UnityEngine.Application.Quit();
                    LogProvisioningPhase("Unity quit call returned; original process exit remains unconfirmed.");
                });
            }
            catch (Exception error)
            {
                LogProvisioningPhase("Unity quit request threw (" + error.GetType().Name + "); no retry.");
            }
        }

        private void OnApplicationQuit()
        {
            if (initializationLifetime.ProvisioningObservationRecorded)
                LogProvisioningPhase("Unity OnApplicationQuit reached; original process exit remains unconfirmed.");
        }

        private void LogProvisioningDestroyPhase()
        {
            if (initializationLifetime.ProvisioningObservationRecorded)
                LogProvisioningPhase("Unity OnDestroy reached; original process exit remains unconfirmed.");
        }

        private void LogProvisioningPhase(string phase)
        {
            // UTC stamps order the quit phases against the parent's diagnostics; they are not exit receipts.
            try { Logger.LogInfo("Provisioning: " + DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + " " + phase); }
            catch { /* Diagnostics cannot prevent a quit request or create another startup path. */ }
        }
    }
}
