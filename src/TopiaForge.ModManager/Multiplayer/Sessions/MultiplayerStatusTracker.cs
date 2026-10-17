using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>
    /// Follows the control's status for the screens without allocating every frame. The control's
    /// <see cref="IInternalMultiplayerControl.GetStatus"/> builds a new snapshot each call, so it is read only when a cheap
    /// signal moves (the role, live availability, the readiness hold or the session snapshot's identity), when an action
    /// asks, and at most five times a second while hosting starts, a join runs, or a hosted session's listing with the
    /// TopiaForge service registers or retries. In a settled state, solo, hosting or joined, a frame reads four fields and
    /// allocates nothing. Main thread only.
    /// </summary>
    internal sealed class MultiplayerStatusTracker
    {
        internal const float InFlightRefreshSeconds = 0.2f;

        private static readonly InternalLiveStatus Empty = new InternalLiveStatus();

        private InternalLiveStatus status = Empty;
        private InternalSessionRole role;
        private bool available;
        private bool held;
        private MultiplayerSessionSnapshot? snapshot;
        private bool hasControl;
        private bool forced = true;
        private float sinceRefresh;

        /// <summary>Gets the last status read; an empty one before the first.</summary>
        internal InternalLiveStatus Status => status;

        /// <summary>Gets the role as of this frame.</summary>
        internal InternalSessionRole Role => role;

        /// <summary>Gets whether the control offers live sessions, as of this frame.</summary>
        internal bool IsLiveAvailable => available && hasControl;

        /// <summary>Gets the session snapshot as of this frame.</summary>
        internal MultiplayerSessionSnapshot? Snapshot => snapshot;

        /// <summary>Gets a counter that moves whenever the status read differs from the one before.</summary>
        internal int Version { get; private set; }

        /// <summary>Gets how many full reads ran, for tests.</summary>
        internal int Reads { get; private set; }

        /// <summary>Asks for a full read at the next tick, as after an action the control has taken.</summary>
        internal void RequestRefresh() => forced = true;

        /// <summary>Gets whether hosting is starting, a join is running or a listing is not settled yet, when the status is polled.</summary>
        internal static bool IsInFlight(InternalLiveStatus value) =>
            value.HostPhase == InternalHostPhase.Preparing ||
            (value.JoinPhase != InternalJoinPhase.None && value.JoinPhase != InternalJoinPhase.InSession && value.JoinPhase != InternalJoinPhase.Ended) ||
            value.Internet.ListingPhase == InternalListingPhase.Registering || value.Internet.ListingPhase == InternalListingPhase.Retrying;

        internal void Tick(IInternalMultiplayerControl? control, MultiplayerSessionSnapshot? session, float deltaTime)
        {
            if (control == null)
            {
                if (hasControl || status != Empty)
                {
                    hasControl = false;
                    available = false;
                    role = InternalSessionRole.Solo;
                    status = Empty;
                    Version++;
                }

                return;
            }

            InternalSessionRole nextRole;
            bool nextAvailable;
            bool nextHeld;
            try
            {
                nextRole = control.Role;
                nextAvailable = control.IsLiveAvailable;
                nextHeld = control.IsReadinessHeld;
            }
            catch (Exception)
            {
                // A control that cannot answer is read as offline; the next frame asks again.
                return;
            }

            var moved = forced || !hasControl || nextRole != role || nextAvailable != available || nextHeld != held || !ReferenceEquals(session, snapshot);
            sinceRefresh += deltaTime > 0f && !float.IsNaN(deltaTime) ? deltaTime : 0f;
            if (!moved && !(IsInFlight(status) && sinceRefresh >= InFlightRefreshSeconds)) return;

            hasControl = true;
            role = nextRole;
            available = nextAvailable;
            held = nextHeld;
            snapshot = session;
            forced = false;
            sinceRefresh = 0f;
            InternalLiveStatus next;
            try
            {
                next = control.GetStatus();
                Reads++;
            }
            catch (Exception)
            {
                return;
            }

            if (Differs(status, next)) Version++;
            status = next;
        }

        private static bool Differs(InternalLiveStatus left, InternalLiveStatus right) =>
            left.Role != right.Role || left.IsLiveAvailable != right.IsLiveAvailable || left.HostPhase != right.HostPhase ||
            left.HostFailure != right.HostFailure || left.JoinPhase != right.JoinPhase || left.JoinFailure != right.JoinFailure ||
            !string.Equals(left.JoinDetail, right.JoinDetail, StringComparison.Ordinal) ||
            !string.Equals(left.HostMessage, right.HostMessage, StringComparison.Ordinal) || left.RejectionCode != right.RejectionCode ||
            left.ContentProblemsFromHost != right.ContentProblemsFromHost || !Same(left.ContentProblems, right.ContentProblems) ||
            !string.Equals(left.ChangedServerId, right.ChangedServerId, StringComparison.Ordinal) ||
            !string.Equals(left.Server?.ServerId, right.Server?.ServerId, StringComparison.Ordinal) ||
            !string.Equals(left.Server?.Name, right.Server?.Name, StringComparison.Ordinal) || left.IsBrowsing != right.IsBrowsing ||
            left.ConnectedPlayers != right.ConnectedPlayers || left.MaxPlayers != right.MaxPlayers || left.IsReadinessHeld != right.IsReadinessHeld ||
            Differs(left.Internet, right.Internet);

        private static bool Differs(InternalInternetStatus left, InternalInternetStatus right) =>
            left.ServiceConfigured != right.ServiceConfigured || left.ListingPhase != right.ListingPhase || left.ListingFailure != right.ListingFailure ||
            !string.Equals(left.Invite?.Code, right.Invite?.Code, StringComparison.Ordinal) || left.InviteFailure != right.InviteFailure ||
            left.Listed != right.Listed || left.RelayReady != right.RelayReady || left.RetryAfterSeconds != right.RetryAfterSeconds ||
            left.JoinRoute != right.JoinRoute || left.HostAllowsDirect != right.HostAllowsDirect;

        private static bool Same(IReadOnlyList<string> left, IReadOnlyList<string> right)
        {
            if (ReferenceEquals(left, right)) return true;
            if (left.Count != right.Count) return false;
            for (var index = 0; index < left.Count; index++)
            {
                if (!string.Equals(left[index], right[index], StringComparison.Ordinal)) return false;
            }

            return true;
        }
    }
}
