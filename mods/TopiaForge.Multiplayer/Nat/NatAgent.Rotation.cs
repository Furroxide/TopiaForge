using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;
using TopiaForge.Multiplayer.Network;
using TopiaForge.Multiplayer.NatTraversal.Ice;
using TopiaForge.Multiplayer.NatTraversal.Turn;
using TopiaForge.Multiplayer.Transport;

namespace TopiaForge.Multiplayer.NatTraversal
{
    /// <summary>
    /// Credentials and rotation. Relay credentials last ten minutes and their expiry ends the allocation, so the next one
    /// is minted ninety seconds before, and minted again whenever the relay is down, since an allocation can only come
    /// back with a credential that still works. A credential the server refuses is minted again at once the first time
    /// after an allocation worked, as when one is revoked; after that, as with a misconfigured key, the next mint waits
    /// fifteen seconds, doubling to five minutes, so a player stays far inside the control plane's five mints a minute
    /// and twenty an hour. Each TURN server's own name is resolved for it. With the relay up, the new allocation comes
    /// up on another server address of the family that worked (make before break), or replaces the old one on its own
    /// address (break before make); with the relay down, the next allocation simply uses it.
    /// </summary>
    internal sealed partial class NatAgent
    {
        private const long RenewRetryMicros = 15_000_000;
        private const long RenewRetryLongestMicros = 300_000_000;
        private const long FailedTargetMicros = 1_800_000_000;
        private const int MaximumFailedTargets = 32;

        /// <summary>How long a change of relay may keep a joiner's signaling open: long enough to move, never all session.</summary>
        private const long RelayChangeWindowMicros = 120_000_000;

        private readonly Dictionary<TransportPath, long> failedTargets = new Dictionary<TransportPath, long>();
        private Task<IceServerSet?>? renewal;
        private RelayServers? resolving;
        private long credentialExpiresAt = long.MaxValue;
        private long nextRenewalAt = long.MaxValue;
        private long renewRetryDelay = RenewRetryMicros;

        /// <summary>How long the next mint waits once a fresh credential was refused too; only an allocation that comes up resets it.</summary>
        private long refusedRetryDelay = RenewRetryMicros;

        /// <summary>Gets whether a credential was refused since an allocation last came up, or before any has.</summary>
        private bool refusedSinceReady = true;

        /// <summary>Gets whether a fresh credential is being minted or its servers resolved.</summary>
        internal bool Renewing => renewal != null || resolving != null;

        private void PollRotation(NetworkStack stack, long now)
        {
            if (plan?.Renew != null && !Renewing && RelayWanted && now >= nextRenewalAt)
            {
                // A failed mint is tried again with backoff: fifteen seconds doubling, kept short while a rotation is due.
                nextRenewalAt = now + renewRetryDelay;
                renewRetryDelay = Math.Min(renewRetryDelay * 2, RelayUp ? 2 * RenewRetryMicros : RenewRetryLongestMicros);
                try
                {
                    renewal = plan.Renew();
                }
                catch (Exception)
                {
                    renewal = null;
                }
            }

            if (renewal != null && renewal.IsCompleted)
            {
                var minted = renewal.Status == TaskStatus.RanToCompletion ? renewal.Result : null;
                if (renewal.Status != TaskStatus.RanToCompletion) Observe(renewal);
                renewal = null;
                if (minted != null && minted.HasRelay) resolving = new RelayServers(minted, Lookup);
            }

            if (resolving != null && resolving.IsCompleted)
            {
                var fresh = resolving;
                resolving = null;
                var targets = fresh.Targets(lastWorked == null ? (AddressFamily?)null : RelayServers.FamilyOf(lastWorked), options.AcceptLoopback);
                if (targets.Count > 0 && RelayWanted) Adopt(fresh.Set, targets, now);
            }

            if (retiring != null && (now >= retireBy || !AllocationInUse(stack, retiring.Allocation))) DropRetiring(now);

            // A released base's pairs go once no connection runs through its allocation any more, or after a grace.
            for (var index = retiredBases.Count - 1; index >= 0; index--)
            {
                var retired = retiredBases[index];
                if (now < retired.Value && AllocationInUse(stack, retired.Key.Allocation)) continue;
                foreach (var session in sessions) session.Checker.RemoveBase(retired.Key);
                retiredBases.RemoveAt(index);
            }
        }

        /// <summary>A fresh credential: the relay rotates onto it when it is up, and otherwise the next allocation uses it.</summary>
        private void Adopt(IceServerSet set, List<RelayTarget> targets, long now)
        {
            var previousExpiry = credentialExpiresAt;
            servers = set;
            relayTargets = targets;
            relayRefused = false;
            credentialExpiresAt = now + (set.TtlSeconds * 1_000_000L);
            nextRenewalAt = RenewalAt(now);
            renewRetryDelay = RenewRetryMicros;
            if (RelayUp && relayBase != null)
            {
                Rotate(previousExpiry, now);
                return;
            }

            // Down, or still coming up under the old credential: the next allocation starts now with this one, where the
            // next attempt was going, or else on the address that last worked unless it failed lately, once any release
            // there has settled.
            var target = relay != null && relay.State == TurnAllocationState.Idle && IndexOfTarget(relay.Server) >= 0 ? relay.Server : PreferredTarget(now);
            if (relay != null) Release(relay, now);
            reallocateDelay = ReallocateFirstMicros;
            relayChangeSince = now;
            ScheduleRelay(target, set, now);
        }

        /// <summary>Moves to a new allocation under the fresh credential, on another server address when one may work.</summary>
        private void Rotate(long previousExpiry, long now)
        {
            var current = relay!;
            var currentBase = relayBase!;
            DropRetiring(now);
            RetireBase(currentBase);
            var alternative = Alternative(current.Server, now);
            if (alternative != null)
            {
                // Make before break: the new allocation comes up on another server address while this one carries on.
                retiring = current;
                retiringBase = currentBase;
                retireBy = previousExpiry - RetireMarginMicros;
            }
            else
            {
                // Break before make: a 5-tuple holds one allocation, so this one is released first, and the next starts
                // on the same address once the release settles. Its pairs stay a while, so each session can move from them.
                retiredBases.Add(new KeyValuePair<LocalBase, long>(currentBase, now + RetiredBaseMicros));
                Release(current, now);
            }

            relayBase = null;
            relayCandidate = null;
            reallocateDelay = ReallocateFirstMicros;
            relayChangeSince = now;
            ScheduleRelay(alternative ?? current.Server, servers!, now);
            rotations++;
            Report(NatDiagnosticKind.RelayRotated, "The relay moved to a fresh credential.");
        }

        /// <summary>
        /// Another server address for make before break: the family of the one in use, never that one, and not one that
        /// failed lately, so a port that many networks block, as many block Cloudflare's 53, is tried once and then left alone.
        /// </summary>
        private TransportPath? Alternative(TransportPath current, long now)
        {
            var family = RelayServers.FamilyOf(current);
            foreach (var target in relayTargets)
            {
                if (target.Family != family || target.Path.Equals(current) || FailedLately(target.Path, now)) continue;
                return target.Path;
            }

            return null;
        }

        /// <summary>
        /// When to mint the next credential: a margin before the current one expires, but never in the past and never
        /// sooner than one retry interval from now, so a service that hands out a credential whose lifetime is below the
        /// margin cannot make the agent renew without end.
        /// </summary>
        private long RenewalAt(long now) => Math.Max(credentialExpiresAt - options.RenewMarginMicros, now + RenewRetryMicros);

        private void RememberFailedTarget(TransportPath target, long now)
        {
            if (failedTargets.Count >= MaximumFailedTargets) failedTargets.Clear();
            failedTargets[target] = now + FailedTargetMicros;
        }

        private bool FailedLately(TransportPath target, long now) => failedTargets.TryGetValue(target, out var until) && now < until;

        /// <summary>When to mint after the server refused a credential: at once the first time since an allocation came up, then with backoff.</summary>
        private long RefusedMintAt(long now)
        {
            if (!refusedSinceReady)
            {
                refusedSinceReady = true;
                return now;
            }

            var at = now + refusedRetryDelay;
            refusedRetryDelay = Math.Min(refusedRetryDelay * 2, RenewRetryLongestMicros);
            return at;
        }

        /// <summary>Forgets the last session's credentials: a mint or lookup still running is left to finish unobserved.</summary>
        private void ResetRotation()
        {
            Abandon(renewal);
            resolving?.Abandon();
            renewal = null;
            resolving = null;
            failedTargets.Clear();
            credentialExpiresAt = long.MaxValue;
            nextRenewalAt = long.MaxValue;
            renewRetryDelay = RenewRetryMicros;
            refusedRetryDelay = RenewRetryMicros;
            refusedSinceReady = true;
        }

        /// <summary>
        /// Gets whether this side's relay is changing: a credential is being minted, or a rotation or a lost allocation
        /// started lately and is not done (the new allocation is not up, or connections are still moving off the old).
        /// </summary>
        private bool RelayChanging(long now) =>
            Renewing || (now - relayChangeSince < RelayChangeWindowMicros && (retiring != null || (RelayWanted && !RelayUp)));

        private long RenewalTimeout()
        {
            if (Renewing) return currentTime + PendingTaskPollMicros;
            return plan?.Renew != null && RelayWanted ? nextRenewalAt : long.MaxValue;
        }
    }
}
