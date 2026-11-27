using System;
using System.Collections.Generic;

namespace TopiaForge.ModManager.Core
{
    /// <summary>
    /// One-shot launch request V5: everything V4 carries, for a host-session or join-session launch, and the
    /// <see cref="Session"/> to host or join. Only those two launches are V5; every other launch stays V4. Safe mode never
    /// hosts or joins, so V5 has no <c>safeMode</c>. A host may start a launch target first; a join carries no plan and
    /// never inherits the manager's mod state. The runtime executes <see cref="Executable"/> through its V4 path and hands
    /// the session to the multiplayer provider.
    /// </summary>
    public sealed class ProfileLaunchConfigurationV5
    {
        public const int SchemaVersion = 5;

        public ProfileLaunchConfigurationV5(string profileId, int profileRevision, string requestId, string command,
            IEnumerable<PackageIdentity> packages, string digest, bool inheritManagerModState, IEnumerable<string> enabledMods,
            IReadOnlyDictionary<string, string> selectedVersions, LaunchSessionRequest session, LaunchPlanDescriptor? plan = null)
        {
            Session = session ?? throw new ArgumentNullException(nameof(session));
            if (LaunchSessionValues.Choice(command, nameof(command), LaunchContractValues.SessionCommands) != session.Command)
                throw new ArgumentException("The command disagrees with the session.", nameof(command));
            Executable = new ProfileLaunchConfigurationV4(profileId, profileRevision, requestId, plan == null ? "main-menu" : "launch-target",
                packages, digest, false, inheritManagerModState, enabledMods, selectedVersions, plan);
            if (session.Role == "join" && (plan != null || inheritManagerModState))
                throw new ArgumentException("A join runs exactly its planned packages and carries no plan.");
        }

        /// <summary>Gets the profile the runtime executes before the session starts: the host's target, or the main menu.</summary>
        public ProfileLaunchConfigurationV4 Executable { get; }
        public LaunchSessionRequest Session { get; }
        public string Command => Session.Command;
        public string ProfileId => Executable.ProfileId;
        public int ProfileRevision => Executable.ProfileRevision;
        public string RequestId => Executable.RequestId;
        public IReadOnlyList<PackageIdentity> Packages => Executable.Packages;
        public string Digest => Executable.Digest;
        public bool InheritManagerModState => Executable.InheritManagerModState;
        public IReadOnlyList<string> EnabledMods => Executable.EnabledMods;
        public IReadOnlyDictionary<string, string> SelectedVersions => Executable.SelectedVersions;
        public LaunchPlanDescriptor? Plan => Executable.Plan;

        public override string ToString() => "launch request V5 " + RequestId + " (" + Command + ")";
    }

    /// <summary>A consumed one-shot request of either version: the profile to execute, and for V5 the session.</summary>
    public sealed class ConsumedLaunchRequest
    {
        internal ConsumedLaunchRequest(ProfileLaunchConfigurationV4 profile, ProfileLaunchConfigurationV5? session)
        {
            Profile = profile;
            SessionRequest = session;
        }

        public ProfileLaunchConfigurationV4 Profile { get; }
        public ProfileLaunchConfigurationV5? SessionRequest { get; }
        public LaunchSessionRequest? Session => SessionRequest?.Session;
        public int SchemaVersion => SessionRequest == null ? ProfileLaunchConfigurationV4.SchemaVersion : ProfileLaunchConfigurationV5.SchemaVersion;

        /// <summary>The manager log's line for this request. It never names the session's target, which may be an address.</summary>
        public string Describe() => SessionRequest == null
            ? "Using one-shot V4 launch profile " + Profile.ProfileId + " request " + Profile.RequestId + "."
            : "Using one-shot V5 launch profile " + Profile.ProfileId + " request " + Profile.RequestId + " to " + SessionRequest.Session.Role
                + " a session.";
    }
}
