using System;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Sessions;

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

        // A V5 request's session until it is offered: its target can be an address, so from then on only the offer holds it,
        // and the provider's plan after the offer is taken.
        private ProfileLaunchConfigurationV5? launchSession;
        internal bool BlockerDeveloperMode { get; private set; }
        private LaunchSessionIdentity? launchSessionIdentity;
        private string? launchServiceOrigin;
        private SessionLaunch? sessionLaunch;
        private ILaunchedSession? launchedSession;
        private string? launchSessionRefusal;
        // Twice a second: a restart waits for the game to take the request, and a look is one file check.
        private static readonly TimeSpan LauncherQuitInterval = TimeSpan.FromMilliseconds(500);
        private LauncherQuitWatcher? launcherQuit;

        /// <summary>
        /// Watches for a quit request from the launcher that started this process, rejected command or safe mode included. A game
        /// started any other way has no request id, so nothing can ask it to quit.
        /// </summary>
        private void WatchForLauncherQuit()
        {
            var requestId = launchProfile?.RequestId ?? rejectedCorrelation?.RequestId;
            if (requestId == null) return;
            try
            {
                var path = launchStaging.LauncherQuitPath(requestId);
                launcherQuit = new LauncherQuitWatcher(() => System.IO.File.Exists(path), () => launchStaging.TakeLauncherQuit(requestId),
                    () => quitClock.Elapsed, LauncherQuitInterval,
                    error => managerLogger.Error(error, "The launcher's quit request could not be read."));
            }
            catch (Exception error) { managerLogger.Error(error, "The launcher's quit requests cannot be watched; a restart stops this game instead."); }
        }

        /// <summary>Quits as the game's own Quit button would, once the launcher that started this game asks.</summary>
        private bool QuitIfLauncherAsks()
        {
            if (launcherQuit?.Poll() != true) return false;
            try { managerLogger.Info("The launcher that started this game asked it to quit."); }
            catch { /* The quit goes ahead even when logging has stopped. */ }
            UnityEngine.Application.Quit();
            return true;
        }

        private void AdmitStartup()
        {
            launchStaging = new LaunchStagingStore(paths);
            // A join claim a crashed process left behind holds no address; the sweep never throws.
            launchStaging.SweepStaleClaims();
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
                var consumed = launchStaging.ConsumeLaunchRequest(configuredPath!);
                var request = consumed.Profile;
                launchSession = consumed.SessionRequest;
                BlockerDeveloperMode = consumed.Session?.DeveloperMode == true;
                if (launchSession != null)
                {
                    launchSessionIdentity = LaunchSessionIdentity.Of(launchSession);
                    launchServiceOrigin = launchSession.Session.ServiceOrigin;
                }

                // The session's target is never logged: a direct target is an address.
                managerLogger.Info(consumed.Describe());
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

        /// <summary>
        /// Offers a V5 request's session to the multiplayer provider, before mods load, unless the command was rejected or
        /// recovery runs. From here only the offer holds the session's target.
        /// </summary>
        private void OfferLaunchSession()
        {
            var request = launchSession;
            launchSession = null;
            if (request == null) return;
            if (rejectedLauncherCommand || startupSelection.SafeMode)
            {
                managerLogger.Warn("The session launch was not offered to the multiplayer provider: this process starts in recovery.");
                // A rejected command is reported as rejected; crash recovery would otherwise report nothing at all.
                if (!rejectedLauncherCommand)
                    launchSessionRefusal = "The game started in recovery after a crash, so it did not " + request.Session.Role
                        + " the session.";
                return;
            }

            sessionLaunch = new SessionLaunch(LaunchSessionIdentity.Of(request), LaunchSessionOffer.ToSession(request.Session), request.Plan != null);
            managerLogger.Info("Offered the " + request.Session.Role + " session to the multiplayer provider.");
        }

        /// <summary>
        /// The provider takes the offered session as mods finish loading: ModRuntime attaches to its internal control at the
        /// end of the load, and the control plans the session. The plan waits for the in-game flows, which start it.
        /// </summary>
        private void TakeLaunchSession()
        {
            var launch = sessionLaunch;
            if (launch == null || !launch.IsOffered) return;
            if (!launch.Take(runtime.Multiplayer.Control))
            {
                RefuseLaunchSession(launch);
                return;
            }

            var publisher = launchPublisher!;
            launchedSession = new LaunchedSession(launch.Identity, launch.Plan!, publisher, () => GameOf(publisher.CommandState),
                startsGame: launch.StartsGame, handoff: launch.IsHost ? null : LauncherHandoff.Current(), hostPort: launch.HostPort);
            managerLogger.Info("The multiplayer provider planned the " + launch.Identity.Role + " session.");
        }

        /// <summary>Withdraws a session no provider took while mods loaded: its launch fails at once, as unavailable.</summary>
        private void WithdrawLaunchSession()
        {
            var launch = sessionLaunch;
            if (launch == null || !launch.Withdraw()) return;
            managerLogger.Info("The multiplayer provider did not take the session launch during startup; it was withdrawn.");
            RefuseLaunchSession(launch);
        }

        private void RefuseLaunchSession(SessionLaunch launch)
        {
            // The refusal is this game's own words, redacted; the target is never named.
            managerLogger.Warn("The " + launch.Identity.Role + " session cannot start: " + launch.Refusal);
            launchPublisher?.Result(SessionLaunchRecords.Refused(launch.Identity, launch.RefusalCode, launch.Refusal), null);
        }

        private static LaunchedGame GameOf(LaunchCommandState state) =>
            state == LaunchCommandState.Succeeded ? LaunchedGame.Ready : state == LaunchCommandState.Failed ? LaunchedGame.Failed : LaunchedGame.Starting;

        /// <summary>The identity a session launch's records are published under, from its request or its rejected correlation.</summary>
        private LaunchSessionIdentity? LaunchSessionIdentityForPublication() => launchSessionIdentity ?? rejectedCorrelation?.Session;

        private void ApplyStartupRecovery()
        {
            var recovery = rejectedLauncherCommand || !CanSaveState
                ? new StartupRecoveryDecision(true, startupRecovery.QuarantineModId, !CanSaveState ? StatePersistenceError : "Rejected launcher command: " + rejectionReason)
                : startupRecovery;
            startupSelection = StartupRecoveryPolicy.Prepare(launchProfile, state, recovery, DateTime.UtcNow);
            if (startupSelection.SafeMode)
                managerLogger.Warn("This process is running in safe mode: every mod is off for this run and remembered autoload is suppressed."
                    + (recovery.SafeMode && recovery.Reason.Length != 0 ? " " + recovery.Reason : string.Empty));
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
