using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Multiplayer.NatTraversal;
using TopiaForge.Multiplayer.NatTraversal.Turn;
using TopiaForge.Multiplayer.Network;

namespace TopiaForge.Multiplayer.ControlPlane
{
    /// <summary>What each of the hosted session's requests does when its answer lands on the main thread.</summary>
    internal sealed partial class InternetHost
    {
        private void StatusLanded(ServiceResult<ServiceStatus> result)
        {
            var now = clock();
            if (!result.Succeeded)
            {
                Retry(result, now);
                return;
            }

            var status = result.Value!;
            if (!status.Hosting || !status.Signaling || (inviteOnly && !status.Invites))
            {
                // Hosting, signaling or invites are off: nobody could join through the service, so it is asked again later.
                Wait(LiveFailure.ServiceDisabled, now + DisabledRetryMicros, null);
                return;
            }

            relayAvailable = status.RelayAvailable;
            Phase = InternetListingPhase.Registering;
            Failure = LiveFailure.None;
            step = Step.Challenge;
            nextAt = Math.Max(now, registeredAt + ReregisterSpacingMicros);
        }

        private void ChallengeLanded(ServiceResult<HostChallenge> result)
        {
            var now = clock();
            if (!result.Succeeded)
            {
                Retry(result, now);
                return;
            }

            challenge = result.Value;
            step = Step.Register;
            nextAt = now;
        }

        private void Registered(ServiceResult<HostRegistration> result, SignedServerDescriptor registered)
        {
            registering = null;
            var now = clock();
            if (!result.Succeeded)
            {
                // A challenge is good once and for two minutes, and a refusal's wait can outlast it: any retry starts from a fresh one.
                step = Step.Challenge;
                Retry(result, now);
                return;
            }

            var registration = result.Value!;
            token = registration.SessionToken;
            Invite = registration.Invite;
            Listed = registration.Listed;
            published = registered;
            heartbeatMicros = registration.HeartbeatSeconds * 1_000_000L;
            listingTtlMicros = registration.ListingTtlSeconds * 1_000_000L;
            renewedAt = now;
            registeredAt = now;
            heartbeatAt = now + heartbeatMicros;
            mintAt = now;
            backoff = FirstRetryMicros;
            step = Step.Online;
            Phase = InternetListingPhase.Online;
            Failure = LiveFailure.None;
            RetryAfterSeconds = 0;
        }

        private void Beat(ServiceResult<HostRenewal> result)
        {
            var now = clock();
            if (result.Succeeded)
            {
                renewedAt = now;
                Listed = result.Value!.Listed;
                heartbeatMicros = (result.Value.HeartbeatSeconds ?? 30) * 1_000_000L;
                heartbeatAt = now + heartbeatMicros;
                backoff = FirstRetryMicros;
                RetryAfterSeconds = 0;
                if (Phase == InternetListingPhase.Retrying) Phase = InternetListingPhase.Online;
                Failure = LiveFailure.None;
                return;
            }

            if (result.Failure == ServiceFailure.Unauthorized)
            {
                // The service ended the session, as it does once heartbeats stop for its lifetime: it is registered afresh.
                Lapse(now);
                return;
            }

            // Within the listing's lifetime the heartbeat is tried again soon, never sooner than a delay the service named.
            var wait = result.RetryAfterSeconds.HasValue
                ? Math.Max(FirstRetryMicros, result.RetryAfterSeconds.Value * 1_000_000L)
                : Math.Min(backoff, heartbeatMicros);
            backoff = Math.Min(backoff * 2, LongestRetryMicros);
            heartbeatAt = now + wait;
            Phase = InternetListingPhase.Retrying;
            Failure = InternetFailures.OfService(result.Failure);
            RetryAfterSeconds = result.RetryAfterSeconds ?? 0;
        }

        private void Updated(ServiceResult<HostRenewal> result, SignedServerDescriptor update)
        {
            var now = clock();
            if (result.Succeeded || result.Failure == ServiceFailure.StaleDescriptor)
            {
                // A newer descriptor the service already holds stands; this one is not sent again.
                published = update;
                if (result.Succeeded) Listed = result.Value!.Listed;
                return;
            }

            if (result.Failure == ServiceFailure.Unauthorized)
            {
                Lapse(now);
                return;
            }

            if (ServiceErrors.IsTransient(result.Failure) || result.Failure == ServiceFailure.RateLimited)
            {
                // Tried again after the next heartbeat, and never sooner than a delay the service named; a descriptor the
                // service refuses outright is not sent again.
                descriptorAt = Math.Max(heartbeatAt, now + ((result.RetryAfterSeconds ?? 0) * 1_000_000L));
                return;
            }

            published = update;
        }

        private void Minted(ServiceResult<RelayCredentials> result)
        {
            var now = clock();
            if (result.Succeeded)
            {
                mintBackoff = 30_000_000;
                relayAvailable = true;
                RelayRefused = false;
                StartNat(result.Value!.Servers);
                return;
            }

            if (result.Failure == ServiceFailure.Unauthorized)
            {
                Lapse(now);
                return;
            }

            if (!relayOnly)
            {
                // A host that shows its address goes on without the relay: joiners who allowed direct play still reach it.
                StartNat(NoServers());
                return;
            }

            // Relay-only is honoured whatever it costs: without the relay this host waits, unreachable over the Internet, and
            // never offers a direct path. The mint is asked again with backoff, inside the service's mint limits.
            RelayRefused = true;
            mintAt = now + Math.Max(mintBackoff, (result.RetryAfterSeconds ?? 0) * 1_000_000L);
            mintBackoff = Math.Min(mintBackoff * 2, LongestRetryMicros);
        }

        private void InviteCreated(ServiceResult<ServiceInvite> result)
        {
            if (result.Succeeded)
            {
                Invite = result.Value;
                return;
            }

            InviteRefused(result.Failure);
        }

        private void InviteRevoked(ServiceResult<ServiceDone> result, string code)
        {
            if (result.Succeeded || result.Failure == ServiceFailure.NotFound)
            {
                if (Invite != null && Invite.Code == code) Invite = null;
                return;
            }

            InviteRefused(result.Failure);
        }

        /// <summary>
        /// An invite request failed. A session the service no longer holds registers again, which brings a fresh invite, and
        /// the listing's phase says so; any other refusal is the invite request's own.
        /// </summary>
        private void InviteRefused(ServiceFailure failure)
        {
            if (failure == ServiceFailure.Unauthorized)
            {
                Lapse(clock());
                return;
            }

            InviteFailure = InternetFailures.OfService(failure, invite: true);
        }

        /// <summary>
        /// Starts answering joiners through signaling and the NAT layer: the plan opens the room with the current session
        /// token, renews relay credentials with it, and offers host candidates only for a host that shows its address.
        /// </summary>
        private void StartNat(IceServerSet servers)
        {
            var serverId = descriptor.Descriptor.ServerId;
            var plan = new NatSessionPlan(servers, () => services.OpenSignaling(serverId, Volatile.Read(ref token) ?? string.Empty), services.Resolve)
            {
                Renew = RenewAsync,
                LocalAddresses = relayOnly ? Array.Empty<System.Net.IPAddress>() : localAddresses,
                LocalPort = network.Network.LocalPort
            };
            nat = NatHost.Start(network.Network, network.Agent, plan, mapPorts);
        }

        /// <summary>The next relay credential, minted with the current session token off the main thread; null when it fails.</summary>
        private async Task<IceServerSet?> RenewAsync()
        {
            var current = Volatile.Read(ref token);
            if (current == null || stopped) return null;
            var minted = await services.Client.MintRelayCredentialsAsync(descriptor.Descriptor.ServerId, current, lifetime.Token).ConfigureAwait(false);
            return minted.Succeeded ? minted.Value!.Servers : null;
        }

        /// <summary>The service no longer holds the session: signaling and the relay start over with a new registration and invite.</summary>
        private void Lapse(long now)
        {
            nat?.Dispose();
            nat = null;
            RelayRefused = false;
            token = null;
            Invite = null;
            InviteFailure = LiveFailure.None;
            Listed = false;
            requests.Clear();
            step = Step.Status;
            Phase = InternetListingPhase.Registering;
            nextAt = Math.Max(now, registeredAt + ReregisterSpacingMicros);
        }

        /// <summary>A step failed: the service is asked again later, or, for a refusal nothing here can change, never.</summary>
        private void Retry<T>(ServiceResult<T> result, long now) where T : class
        {
            var failure = result.Failure;
            if (failure == ServiceFailure.Cancelled) return;

            // A delay the service named, with a 429 or a 503 alike, is always honoured; a longer wait of this host's own stands.
            var named = (result.RetryAfterSeconds ?? 0) * 1_000_000L;
            if (failure == ServiceFailure.RateLimited)
            {
                Wait(LiveFailure.ServiceRateLimited, now + Math.Max(FirstRetryMicros, named), result.RetryAfterSeconds);
                return;
            }

            if (failure == ServiceFailure.FeatureDisabled)
            {
                step = Step.Status;
                Wait(LiveFailure.ServiceDisabled, now + Math.Max(DisabledRetryMicros, named), result.RetryAfterSeconds);
                return;
            }

            if (failure == ServiceFailure.ServerBlocked)
            {
                step = Step.Status;
                Wait(LiveFailure.ServerBlocked, now + Math.Max(BlockedRetryMicros, named), result.RetryAfterSeconds);
                return;
            }

            if (failure == ServiceFailure.ChallengeInvalid || ServiceErrors.IsTransient(failure))
            {
                Wait(LiveFailure.ServiceUnavailable, now + Math.Max(backoff, named), result.RetryAfterSeconds);
                backoff = Math.Min(backoff * 2, LongestRetryMicros);
                return;
            }

            // The service refused what this host sent, and sending it again would change nothing.
            Phase = InternetListingPhase.Failed;
            Failure = InternetFailures.OfService(failure);
            nextAt = long.MaxValue;
        }

        private void Wait(LiveFailure failure, long until, int? retryAfterSeconds)
        {
            Phase = InternetListingPhase.Retrying;
            Failure = failure;
            RetryAfterSeconds = retryAfterSeconds ?? 0;
            nextAt = until;
        }
    }
}
