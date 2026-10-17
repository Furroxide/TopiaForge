using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Multiplayer.Network;
using TopiaForge.Multiplayer.Transport.Crypto;

namespace TopiaForge.Multiplayer.ControlPlane
{
    /// <summary>
    /// The TopiaForge service as the provider uses it (ControlPlaneApi.md): status, host registration through a challenge
    /// and an Ed25519 proof, heartbeats, descriptor updates and ending a session, invites, resolving an invite or a server,
    /// join tickets, and relay credentials. Every body it sends is canonical JSON, byte for byte what the service's shared
    /// fixtures record; every answer is read within its bound and strictly (<see cref="ControlPlaneAnswers"/>), and an
    /// answer that fails is an <see cref="ServiceFailure.InvalidResponse"/>. Methods never throw for what the network or
    /// the service does. Tokens, tickets and invite codes are never printed, and no text the service sent is kept.
    /// </summary>
    internal sealed class ControlPlaneClient
    {
        internal const int SmallAnswerBytes = 4 * 1024;

        /// <summary>A server answer carries a whole signed descriptor as a JSON string, which escaping can nearly double.</summary>
        internal const int ServerAnswerBytes = (2 * SignedServerDescriptor.MaximumBytes) + (64 * 1024);

        internal const int MinimumInviteSeconds = 300;
        internal const int MaximumInviteSeconds = 604_800;
        internal const int MaximumInviteInput = 64;

        private static readonly byte[] RegistrationLabel = Encoding.ASCII.GetBytes("TopiaForge host registration v1\n");

        private readonly IControlPlaneTransport transport;

        internal ControlPlaneClient(IControlPlaneTransport transport)
        {
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        }

        internal Task<ServiceResult<ServiceStatus>> GetStatusAsync(CancellationToken cancellation) =>
            SendAsync(new ControlPlaneRequest(ControlPlaneMethod.Get, "/v1/status", null, null, SmallAnswerBytes), 200, ControlPlaneAnswers.Status, cancellation);

        internal Task<ServiceResult<HostChallenge>> RequestChallengeAsync(byte[] signingKey, CancellationToken cancellation)
        {
            if (signingKey == null || signingKey.Length != Ed25519.PublicKeySize) throw new ArgumentException("A signing key is 32 bytes.", nameof(signingKey));
            var body = new JsonObjectNode().Add("signingKey", WireText.ToBase64Url(signingKey));
            return Post("/v1/hosts/challenge", null, body, 200, ControlPlaneAnswers.Challenge, cancellation);
        }

        /// <summary>
        /// Registers the host with the proof the service asks for: an Ed25519 signature by <paramref name="signingSeed"/>
        /// over the registration label and the canonical body without its proof. The caller keeps and wipes the seed.
        /// </summary>
        internal Task<ServiceResult<HostRegistration>> RegisterAsync(HostChallenge challenge, SignedServerDescriptor descriptor, bool relayOnly, byte[] signingSeed, CancellationToken cancellation)
        {
            if (challenge == null) throw new ArgumentNullException(nameof(challenge));
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            var text = ServiceErrors.Utf8(descriptor.Bytes);
            var unsigned = new JsonObjectNode().Add("challenge", challenge.Challenge).Add("descriptor", text).Add("relayOnly", relayOnly);
            var proof = Ed25519.Sign(signingSeed, Concat(RegistrationLabel, CanonicalJson.Serialize(unsigned)));
            var body = new JsonObjectNode().Add("challenge", challenge.Challenge).Add("descriptor", text).Add("proof", WireText.ToBase64Url(proof)).Add("relayOnly", relayOnly);
            var serverId = descriptor.Descriptor.ServerId;
            var sha = descriptor.Sha256;
            return Post("/v1/hosts", null, body, 201, answer => ControlPlaneAnswers.Registration(answer, serverId, sha), cancellation);
        }

        internal Task<ServiceResult<HostRenewal>> HeartbeatAsync(string serverId, string sessionToken, int players, CancellationToken cancellation)
        {
            if (players < 0 || players > ServerDescriptor.MaximumPlayers) throw new ArgumentOutOfRangeException(nameof(players));
            return Post(HostPath(serverId) + "/heartbeat", sessionToken, new JsonObjectNode().Add("players", players), 200, ControlPlaneAnswers.Heartbeat, cancellation);
        }

        internal Task<ServiceResult<HostRenewal>> UpdateDescriptorAsync(string serverId, string sessionToken, SignedServerDescriptor descriptor, CancellationToken cancellation)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            var body = new JsonObjectNode().Add("descriptor", ServiceErrors.Utf8(descriptor.Bytes));
            var sha = descriptor.Sha256;
            return SendAsync(new ControlPlaneRequest(ControlPlaneMethod.Put, HostPath(serverId) + "/descriptor", sessionToken, CanonicalJson.Serialize(body), SmallAnswerBytes),
                200, answer => ControlPlaneAnswers.DescriptorUpdate(answer, sha), cancellation);
        }

        /// <summary>Ends the host's session now: its listing, invites, tickets and signaling room go with it.</summary>
        internal Task<ServiceResult<ServiceDone>> EndHostAsync(string serverId, string sessionToken, CancellationToken cancellation) =>
            SendAsync(new ControlPlaneRequest(ControlPlaneMethod.Delete, HostPath(serverId), sessionToken, null, SmallAnswerBytes), 204, Empty, cancellation);

        internal Task<ServiceResult<ServiceInvite>> CreateInviteAsync(string serverId, string sessionToken, int ttlSeconds, CancellationToken cancellation)
        {
            if (ttlSeconds < MinimumInviteSeconds || ttlSeconds > MaximumInviteSeconds) throw new ArgumentOutOfRangeException(nameof(ttlSeconds));
            return Post(HostPath(serverId) + "/invites", sessionToken, new JsonObjectNode().Add("ttlSeconds", ttlSeconds), 201, ControlPlaneAnswers.CreatedInvite, cancellation);
        }

        /// <summary>Withdraws one of the session's invites; the code travels in the body, never in a URL.</summary>
        internal Task<ServiceResult<ServiceDone>> RevokeInviteAsync(string serverId, string sessionToken, string code, CancellationToken cancellation)
        {
            if (!InviteCode.IsCanonical(code)) throw new ArgumentException("An invite code is canonical.", nameof(code));
            return Post(HostPath(serverId) + "/invites/revoke", sessionToken, new JsonObjectNode().Add("code", code), 204, Empty, cancellation);
        }

        /// <summary>Resolves an invite. The service reads the code forgivingly, so the text may be what the player typed.</summary>
        internal Task<ServiceResult<ServiceServer>> ResolveInviteAsync(string code, CancellationToken cancellation)
        {
            if (code == null || code.Length == 0 || code.Length > MaximumInviteInput) throw new ArgumentException("An invite code is 1 to 64 characters.", nameof(code));
            return Post("/v1/invites/resolve", null, new JsonObjectNode().Add("code", code), 200, answer => ControlPlaneAnswers.Server(answer, null), cancellation);
        }

        /// <summary>Gets a public server's signed descriptor and listing by its id.</summary>
        internal Task<ServiceResult<ServiceServer>> GetServerAsync(string serverId, CancellationToken cancellation) =>
            SendAsync(new ControlPlaneRequest(ControlPlaneMethod.Get, "/v1/servers/" + ServerIdOf(serverId), null, null, ServerAnswerBytes), 200,
                answer => ControlPlaneAnswers.Server(answer, serverId), cancellation);

        /// <summary>Asks for a join ticket. <paramref name="relayOnly"/> is the joiner's privacy choice, which the ticket carries.</summary>
        internal Task<ServiceResult<ServiceJoinTicket>> CreateJoinTicketAsync(string serverId, string? inviteCode, bool relayOnly, CancellationToken cancellation)
        {
            if (inviteCode != null && !InviteCode.IsCanonical(inviteCode)) throw new ArgumentException("An invite code is canonical.", nameof(inviteCode));
            var body = new JsonObjectNode().Add("invite", inviteCode).Add("relayOnly", relayOnly).Add("serverId", ServerIdOf(serverId));
            return Post("/v1/join-tickets", null, body, 201, answer => ControlPlaneAnswers.Ticket(answer, serverId, relayOnly), cancellation);
        }

        /// <summary>Mints relay credentials with the host's session token or the joiner's ticket.</summary>
        internal Task<ServiceResult<RelayCredentials>> MintRelayCredentialsAsync(string serverId, string credential, CancellationToken cancellation) =>
            Post("/v1/turn/credentials", credential, new JsonObjectNode().Add("serverId", ServerIdOf(serverId)), 201, ControlPlaneAnswers.Credentials, cancellation);

        private Task<ServiceResult<T>> Post<T>(string path, string? bearer, JsonObjectNode body, int expected, Func<byte[], T?> read, CancellationToken cancellation)
            where T : class =>
            SendAsync(new ControlPlaneRequest(ControlPlaneMethod.Post, path, bearer, CanonicalJson.Serialize(body), BoundOf<T>()), expected, read, cancellation);

        /// <summary>How large each kind of answer may be: a server's carries a descriptor, relay credentials their server list.</summary>
        private static int BoundOf<T>() =>
            typeof(T) == typeof(ServiceServer) ? ServerAnswerBytes
            : typeof(T) == typeof(RelayCredentials) ? NatTraversal.Turn.IceServerSet.MaximumBytes
            : SmallAnswerBytes;

        private async Task<ServiceResult<T>> SendAsync<T>(ControlPlaneRequest request, int expected, Func<byte[], T?> read, CancellationToken cancellation) where T : class
        {
            ControlPlaneResponse response;
            try
            {
                response = await transport.SendAsync(request, cancellation).ConfigureAwait(false) ?? ControlPlaneResponse.Failed(ControlPlaneTransportFailure.Malformed);
            }
            catch (OperationCanceledException)
            {
                return ServiceResult<T>.Fail(ServiceFailure.Cancelled);
            }
            catch (Exception)
            {
                // A transport is meant never to throw; if one does, its text is not read either.
                return ServiceResult<T>.Fail(ServiceFailure.Unreachable);
            }

            if (!response.Succeeded) return ServiceResult<T>.Fail(ServiceErrors.Of(response.Failure));
            if (response.Status != expected)
            {
                return response.Status >= 200 && response.Status < 300
                    ? ServiceResult<T>.Fail(ServiceFailure.InvalidResponse)
                    : ServiceResult<T>.Fail(ServiceErrors.Read(response), response.RetryAfterSeconds);
            }

            if (expected != 204 && !response.IsJson) return ServiceResult<T>.Fail(ServiceFailure.InvalidResponse);
            var value = read(response.Body);
            return value == null ? ServiceResult<T>.Fail(ServiceFailure.InvalidResponse) : ServiceResult<T>.Ok(value);
        }

        private static ServiceDone? Empty(byte[] body) => body.Length == 0 ? ServiceDone.Instance : null;

        private static string HostPath(string serverId) => "/v1/hosts/" + ServerIdOf(serverId);

        private static string ServerIdOf(string serverId) =>
            WireText.IsHex(serverId, 32) ? serverId : throw new ArgumentException("A server id is 64 lowercase hexadecimal characters.", nameof(serverId));

        private static byte[] Concat(byte[] first, byte[] second)
        {
            var result = new byte[first.Length + second.Length];
            Buffer.BlockCopy(first, 0, result, 0, first.Length);
            Buffer.BlockCopy(second, 0, result, first.Length, second.Length);
            return result;
        }
    }
}
