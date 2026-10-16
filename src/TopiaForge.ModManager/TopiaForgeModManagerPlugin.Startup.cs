using System;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager
{
    public sealed partial class TopiaForgeModManagerPlugin
    {
        private LaunchStagingStore launchStaging = null!;
        private RuntimeStartupSelection startupSelection = null!;
        private RuntimeLaunchPublisher? launchPublisher;
        private bool hasLauncherCommand;
        private bool acceptanceAdmitted;
        private bool rejectedLauncherCommand;
        private LaunchRequestCorrelation? rejectedCorrelation;
        private string rejectionReason = string.Empty;

        private void AdmitStartup()
        {
            launchStaging = new LaunchStagingStore(paths);
            var acceptanceRequest = Environment.GetEnvironmentVariable(AcceptanceIsolationGate.EnvironmentVariable);
            if (acceptanceRequest == null) return;
            launchProfile = launchStaging.AdmitAcceptance(acceptanceRequest,
                Environment.GetEnvironmentVariable(ProfileLaunchConfigurationV4.EnvironmentVariable),
                () => AcceptanceRuntimeProbe.Read(BepInEx.Paths.GameRootPath, BepInEx.Paths.BepInExRootPath,
                    paths.Root, UnityEngine.Application.persistentDataPath), DateTimeOffset.UtcNow);
            acceptanceAdmitted = true;
            hasLauncherCommand = true;
        }

        private ProfileLaunchConfigurationV4? ConsumeLaunchProfile()
        {
            if (acceptanceAdmitted) return launchProfile;
            var configuredPath = Environment.GetEnvironmentVariable(ProfileLaunchConfigurationV4.EnvironmentVariable);
            hasLauncherCommand = !string.IsNullOrWhiteSpace(configuredPath);
            if (!hasLauncherCommand) return null;
            try
            {
                var request = launchStaging.ConsumeRequest(configuredPath!);
                managerLogger.Info("Using one-shot V4 launch profile " + request.ProfileId + " request " + request.RequestId + ".");
                return request;
            }
            catch (Exception error)
            {
                rejectedLauncherCommand = true;
                rejectionReason = error.Message;
                rejectedCorrelation = launchStaging.TryReadCorrelation(configuredPath!);
                managerLogger.Error(error, "Profile launch configuration was rejected; entering safe mode.");
                return null;
            }
        }

        private void ApplyStartupRecovery()
        {
            var recovery = rejectedLauncherCommand || !CanSaveState
                ? new StartupRecoveryDecision(true, startupRecovery.QuarantineModId, !CanSaveState ? StatePersistenceError : "Rejected launcher command: " + rejectionReason)
                : startupRecovery;
            startupSelection = StartupRecoveryPolicy.Prepare(launchProfile, state, recovery, DateTime.UtcNow);
            if (startupSelection.SafeMode) managerLogger.Warn("This process is running in safe mode; remembered autoload is suppressed.");
            if (startupSelection.QuarantinedPackageId.Length != 0)
                managerLogger.Warn("Automatically quarantined " + startupSelection.QuarantinedPackageId + ": " + recovery.Reason);
        }

        private void PublishRuntimeObservations()
        {
            if (runtime?.SessionBindings == null || launchPublisher == null) return;
            try
            {
                launchPublisher.PublishObservations(runtime.SessionBindings);
            }
            catch (Exception error) { managerLogger.Error(error, "Runtime observations could not be published."); }
        }
    }
}
