using System;
using System.Collections.Generic;
using System.Net;
using TopiaForge.Multiplayer.NatTraversal.Stun;
using TopiaForge.Multiplayer.Transport;

namespace TopiaForge.Multiplayer.NatTraversal.Turn
{
    internal enum TurnAllocationState
    {
        Idle = 0,
        Allocating = 1,
        Ready = 2,
        Failed = 3,
        Released = 4,

        /// <summary>The release is in flight: until the server answers or it times out, the 5-tuple may still be taken.</summary>
        Releasing = 5
    }

    /// <summary>Why an allocation failed or ended. None of these names an address.</summary>
    internal enum TurnFailure
    {
        None = 0,
        Timeout = 1,
        Unauthorized = 2,
        Forbidden = 3,
        AllocationMismatch = 4,
        QuotaReached = 5,
        InsufficientCapacity = 6,
        Unsupported = 7,
        ServerError = 8,
        Malformed = 9
    }

    /// <summary>Counters for tests and diagnostics. None of them names a peer.</summary>
    internal sealed class TurnStatistics
    {
        internal int Allocations { get; set; }

        internal int Refreshes { get; set; }

        internal int PermissionRequests { get; set; }

        internal int ChannelBinds { get; set; }

        internal int StaleNonceRetries { get; set; }

        internal int Releases { get; set; }

        internal long ChannelDataSent { get; set; }

        internal long SendIndicationsSent { get; set; }

        internal long ChannelDataReceived { get; set; }

        internal long DataIndicationsReceived { get; set; }

        /// <summary>Gets or sets the datagrams that could not go out yet: no permission, or no allocation.</summary>
        internal long Held { get; set; }

        internal long Keepalives { get; set; }
    }

    /// <summary>
    /// One TURN allocation over UDP (RFC 8656) on the shared game socket, sans-I/O: requests travel through the shared
    /// <see cref="StunClient"/>, and data is wrapped into caller buffers. It allocates with the long-term credential
    /// mechanism (the 401 challenge, then MESSAGE-INTEGRITY keyed by MD5 of user, realm and password), retries a 438
    /// with the fresh nonce, refreshes the allocation a minute before it lapses, keeps permissions alive every four
    /// minutes (the server holds them five) and channel bindings every eight (it holds them ten), and sends a Binding
    /// indication after 15 quiet seconds so the NAT in front of it keeps the mapping. Responses to authenticated
    /// requests count only with valid integrity. Releasing is a transaction of its own, retransmitted until the server
    /// answers or it times out, so whoever allocates next on the same 5-tuple can wait for it to settle.
    /// </summary>
    internal sealed partial class TurnClient : IStunTransactionOwner
    {
        internal const byte UdpProtocol = 17;
        internal const long PermissionLifetimeMicros = 300_000_000;
        internal const long PermissionRefreshMicros = 240_000_000;
        internal const long ChannelRefreshMicros = 480_000_000;
        internal const long RefreshMarginMicros = 60_000_000;
        internal const long KeepaliveMicros = 15_000_000;
        internal const long RetryMicros = 5_000_000;
        internal const int MaximumStaleNonceRetries = 3;
        internal const uint MinimumLifetimeSeconds = 30;
        internal const uint MaximumLifetimeSeconds = 3600;
        internal const int MaximumRealmLength = 127;

        private readonly StunClient stun;
        private readonly string username;
        private readonly string password;
        private readonly StunMessageWriter writer = new StunMessageWriter();
        private readonly byte[] requestBuffer = new byte[StunMessage.MaximumBytes];
        private string? realm;
        private string? nonce;
        private byte[]? key;
        private long refreshAt = long.MaxValue;
        private long lastSentAt;
        private StunTransaction? refreshing;

        /// <summary>Whether the server may hold an allocation this client asked for, until a release is answered.</summary>
        private bool holds;

        internal TurnClient(int allocation, TransportPath server, string username, string password, StunClient stun)
        {
            Server = server ?? throw new ArgumentNullException(nameof(server));
            if (!StunCredentials.IsPrintableAscii(username, IceServerSet.MaximumCredentialLength)) throw new ArgumentException("The TURN username must be printable ASCII.", nameof(username));
            if (!StunCredentials.IsPrintableAscii(password, IceServerSet.MaximumCredentialLength)) throw new ArgumentException("The TURN credential must be printable ASCII.", nameof(password));
            this.username = username;
            this.password = password;
            this.stun = stun ?? throw new ArgumentNullException(nameof(stun));
            Allocation = allocation;
            peersByChannel = new Peer?[ChannelData.ChannelCount];
        }

        /// <summary>Gets this allocation's number, which its relayed paths carry.</summary>
        internal int Allocation { get; }

        /// <summary>Gets the path to the TURN server: the one place this allocation's traffic goes.</summary>
        internal TransportPath Server { get; }

        internal TurnAllocationState State { get; private set; }

        internal TurnFailure Failure { get; private set; }

        /// <summary>Gets whether the server answered anything this client asked: proof that UDP reaches it.</summary>
        internal bool Answered { get; private set; }

        /// <summary>Gets whether a release went out and the server never answered it, so it may still hold the allocation.</summary>
        internal bool ReleaseUnconfirmed => State == TurnAllocationState.Released && holds;

        /// <summary>Gets the relayed address the server allocated: this side's relay candidate. It never leaves the NAT layer except as one.</summary>
        internal IPEndPoint? RelayedAddress { get; private set; }

        /// <summary>Gets this side's address as the server saw it: a server-reflexive candidate.</summary>
        internal IPEndPoint? MappedAddress { get; private set; }

        /// <summary>Gets when the allocation lapses unless refreshed.</summary>
        internal long ExpiresAt { get; private set; } = long.MaxValue;

        internal TurnStatistics Statistics { get; } = new TurnStatistics();

        /// <summary>Starts the allocation: the first Allocate goes out unauthenticated and draws the server's challenge.</summary>
        internal void Start(long now)
        {
            if (State != TurnAllocationState.Idle) throw new InvalidOperationException("An allocation starts once.");
            State = TurnAllocationState.Allocating;
            Statistics.Allocations++;
            if (Send(new Request(RequestKind.Allocate, false), now) == null) Fail(TurnFailure.ServerError);
        }

        /// <summary>
        /// Deletes the allocation with a zero-lifetime Refresh. When the server may hold one (this client allocated, or
        /// asked to), the release is a transaction: it retransmits until the server answers, or gives up after five and a
        /// half seconds, and the client stays <see cref="TurnAllocationState.Releasing"/> until then. Otherwise it ends at once.
        /// </summary>
        internal void Release(long now)
        {
            if (State == TurnAllocationState.Released || State == TurnAllocationState.Releasing) return;
            stun.CancelAll(this);
            refreshing = null;
            ClearPeers();
            SendRelease(now);
        }

        /// <summary>
        /// Sends the release again when an earlier one went unanswered and the 5-tuple is still taken (a 437); false when
        /// there is nothing to release.
        /// </summary>
        internal bool ReleaseAgain(long now)
        {
            if (!ReleaseUnconfirmed) return false;
            SendRelease(now);
            return State == TurnAllocationState.Releasing;
        }

        internal void Poll(long now)
        {
            if (State != TurnAllocationState.Ready) return;
            if (now >= ExpiresAt)
            {
                Fail(TurnFailure.Timeout);
                return;
            }

            if (now >= refreshAt && refreshing == null)
            {
                refreshAt = long.MaxValue;
                refreshing = Send(new Request(RequestKind.Refresh, true), now);
                if (refreshing == null) refreshAt = now + RetryMicros;
                else Statistics.Refreshes++;
            }

            PollPermissions(now);
            PollChannels(now);
        }

        internal long NextTimeoutMicros()
        {
            if (State != TurnAllocationState.Ready) return long.MaxValue;
            var next = Math.Min(ExpiresAt, refreshing == null ? refreshAt : long.MaxValue);
            next = Math.Min(next, lastSentAt + KeepaliveMicros);
            return Math.Min(next, NextPeerTimeout());
        }

        /// <summary>Writes a Binding indication when the server has heard nothing for 15 seconds; false otherwise.</summary>
        internal bool TryWriteKeepalive(long now, byte[] output, out int length)
        {
            length = 0;
            if (State != TurnAllocationState.Ready || now - lastSentAt < KeepaliveMicros) return false;
            writer.Begin(output, StunMethod.Binding, StunClass.Indication, stun.NewTransactionId());
            length = writer.Finish(null, true);
            lastSentAt = now;
            Statistics.Keepalives++;
            return true;
        }

        public bool Accepts(StunTransaction transaction, StunMessage response)
        {
            var request = (Request)transaction.Tag!;
            if (!request.Authenticated) return true;
            if (response.HasIntegrity) return response.VerifyIntegrity(key!);

            // An error answering an authenticated request may lack integrity (401 and 438 do by design); only an
            // on-path attacker who already sees the transaction id could forge one, and it could drop traffic anyway.
            return response.Class == StunClass.Error;
        }

        public void OnResponse(StunTransaction transaction, StunMessage response, long now)
        {
            var request = (Request)transaction.Tag!;
            Answered = true;
            if (ReferenceEquals(transaction, refreshing)) refreshing = null;
            if (request.Kind == RequestKind.Release)
            {
                OnReleaseAnswered(request, response, now);
                return;
            }

            if (State != TurnAllocationState.Allocating && State != TurnAllocationState.Ready) return;
            if (response.Class == StunClass.Error)
            {
                if (!response.TryGetErrorCode(out var code)) code = 500;
                if (TryAuthenticate(request, response, code, now)) return;
                OnError(request, code, now);
                return;
            }

            if (response.UnknownRequired.Count > 0)
            {
                OnError(request, 500, now);
                return;
            }

            switch (request.Kind)
            {
                case RequestKind.Allocate:
                    OnAllocated(response, now);
                    break;
                case RequestKind.Refresh:
                    OnRefreshed(response, now);
                    break;
                case RequestKind.Permission:
                    OnPermitted(request, now);
                    break;
                case RequestKind.Channel:
                    OnBound(request, now);
                    break;
            }
        }

        public void OnTimeout(StunTransaction transaction, long now)
        {
            var request = (Request)transaction.Tag!;
            if (ReferenceEquals(transaction, refreshing)) refreshing = null;
            if (request.Kind == RequestKind.Release)
            {
                // Nobody answered: the allocation may still be there, so a 437 later can send the release again.
                if (State == TurnAllocationState.Releasing) State = TurnAllocationState.Released;
                return;
            }

            if (State != TurnAllocationState.Allocating && State != TurnAllocationState.Ready) return;
            switch (request.Kind)
            {
                case RequestKind.Allocate:
                case RequestKind.Refresh:
                    Fail(TurnFailure.Timeout);
                    break;
                case RequestKind.Permission:
                    OnPermissionFailed(request, false, now);
                    break;
                case RequestKind.Channel:
                    OnChannelFailed(request, true, now);
                    break;
            }
        }

        private void SendRelease(long now)
        {
            if (!holds || key == null || realm == null || nonce == null)
            {
                State = TurnAllocationState.Released;
                return;
            }

            State = TurnAllocationState.Releasing;
            Statistics.Releases++;
            if (Send(new Request(RequestKind.Release, true), now) == null) State = TurnAllocationState.Released;
        }

        /// <summary>Any answer settles a release: success deletes the allocation, and a 437 says there was none left.</summary>
        private void OnReleaseAnswered(Request request, StunMessage response, long now)
        {
            if (State != TurnAllocationState.Releasing) return;
            if (response.Class == StunClass.Error && response.TryGetErrorCode(out var code) && TryAuthenticate(request, response, code, now)) return;
            holds = false;
            State = TurnAllocationState.Released;
        }

        /// <summary>Answers the server's challenge: a first 401 brings realm and nonce, a 438 a fresh nonce. True when retried.</summary>
        private bool TryAuthenticate(Request request, StunMessage response, int code, long now)
        {
            if (code == 401 && !request.Authenticated)
            {
                if (!response.TryGetText(StunAttribute.Realm, StunMessage.MaximumTextBytes, out var offeredRealm) ||
                    !StunCredentials.IsPrintableAscii(offeredRealm, MaximumRealmLength) ||
                    !response.TryGetText(StunAttribute.Nonce, StunMessage.MaximumTextBytes, out var offeredNonce))
                {
                    Fail(TurnFailure.Unsupported);
                    return true;
                }

                realm = offeredRealm;
                nonce = offeredNonce;
                key = StunCredentials.LongTermKey(username, realm, password);
                Retry(request.Authenticate(), now);
                return true;
            }

            if (code == 438 && request.Authenticated && request.StaleRetries < MaximumStaleNonceRetries)
            {
                if (!response.TryGetText(StunAttribute.Nonce, StunMessage.MaximumTextBytes, out var freshNonce)) return false;
                if (response.TryGetText(StunAttribute.Realm, StunMessage.MaximumTextBytes, out var sameRealm) && sameRealm != realm) return false;
                nonce = freshNonce;
                Statistics.StaleNonceRetries++;
                Retry(request.Stale(), now);
                return true;
            }

            return false;
        }

        private void Retry(Request request, long now)
        {
            var transaction = Send(request, now);
            if (request.Kind == RequestKind.Refresh) refreshing = transaction;
            if (transaction != null) return;
            if (request.Kind == RequestKind.Release) State = TurnAllocationState.Released;
            else OnError(request, 500, now);
        }

        private void OnError(Request request, int code, long now)
        {
            // A 437 to anything on a live allocation means the server holds none on this 5-tuple any more: it is lost.
            if (code == 437 && request.Kind != RequestKind.Allocate)
            {
                Fail(TurnFailure.AllocationMismatch);
                return;
            }

            switch (request.Kind)
            {
                case RequestKind.Allocate:
                    // An Allocate the server refused created nothing; one already there belongs to someone else (437).
                    holds = false;
                    Fail(FailureFor(code));
                    break;
                case RequestKind.Refresh:
                    Fail(FailureFor(code));
                    break;
                case RequestKind.Permission:
                    OnPermissionFailed(request, code == 403, now);
                    break;
                case RequestKind.Channel:
                    OnChannelFailed(request, false, now);
                    break;
            }
        }

        private static TurnFailure FailureFor(int code)
        {
            switch (code)
            {
                case 401:
                case 441:
                    return TurnFailure.Unauthorized;
                case 403:
                    return TurnFailure.Forbidden;
                case 437:
                    return TurnFailure.AllocationMismatch;
                case 300:
                case 440:
                case 442:
                    return TurnFailure.Unsupported;
                case 486:
                    return TurnFailure.QuotaReached;
                case 508:
                    return TurnFailure.InsufficientCapacity;
                default:
                    return TurnFailure.ServerError;
            }
        }

        private void OnAllocated(StunMessage response, long now)
        {
            if (State != TurnAllocationState.Allocating || !response.TryGetAddress(StunAttribute.XorRelayedAddress, out var relayed))
            {
                Fail(TurnFailure.Malformed);
                return;
            }

            RelayedAddress = new IPEndPoint(NatAddresses.Unmap(relayed.Address), relayed.Port);
            if (response.TryGetAddress(StunAttribute.XorMappedAddress, out var mapped)) MappedAddress = new IPEndPoint(NatAddresses.Unmap(mapped.Address), mapped.Port);
            State = TurnAllocationState.Ready;
            SetLifetime(response, now);
        }

        private void OnRefreshed(StunMessage response, long now)
        {
            if (State == TurnAllocationState.Ready) SetLifetime(response, now);
        }

        private void SetLifetime(StunMessage response, long now)
        {
            var seconds = response.TryGetUInt32(StunAttribute.Lifetime, out var lifetime) ? lifetime : 600u;
            seconds = Math.Max(MinimumLifetimeSeconds, Math.Min(MaximumLifetimeSeconds, seconds));
            var micros = seconds * 1_000_000L;
            ExpiresAt = now + micros;
            refreshAt = now + (micros > 2 * RefreshMarginMicros ? micros - RefreshMarginMicros : micros / 2);
        }

        /// <summary>The allocation is lost. The server may still hold it (a refresh that timed out), so it can still be released.</summary>
        private void Fail(TurnFailure failure)
        {
            if (State != TurnAllocationState.Allocating && State != TurnAllocationState.Ready && State != TurnAllocationState.Idle) return;
            stun.CancelAll(this);
            refreshing = null;
            State = TurnAllocationState.Failed;
            Failure = failure;
            ClearPeers();
        }

        /// <summary>
        /// Writes and starts one request; authenticated requests carry user, realm, nonce and integrity. Returns null,
        /// sending nothing, when the client is full or the request cannot fit a datagram, so no server's answer can make
        /// this throw.
        /// </summary>
        private StunTransaction? Send(Request request, long now)
        {
            var id = stun.NewTransactionId();
            int length;
            try
            {
                writer.Begin(requestBuffer, request.Method, StunClass.Request, id);
                switch (request.Kind)
                {
                    case RequestKind.Allocate:
                        writer.AddRequestedTransport(UdpProtocol);
                        break;
                    case RequestKind.Release:
                        writer.AddUInt32(StunAttribute.Lifetime, 0);
                        break;
                    case RequestKind.Permission:
                        foreach (var permission in request.Permissions!) writer.AddAddressBytes(StunAttribute.XorPeerAddress, permission.AddressBytes, 1);
                        break;
                    case RequestKind.Channel:
                        writer.AddChannelNumber(request.Peer!.Channel);
                        writer.AddAddressBytes(StunAttribute.XorPeerAddress, request.Peer.AddressBytes, request.Peer.EndPoint.Port);
                        break;
                }

                if (request.Authenticated)
                {
                    writer.AddText(StunAttribute.Username, username);
                    writer.AddText(StunAttribute.Realm, realm!);
                    writer.AddText(StunAttribute.Nonce, nonce!);
                }

                length = writer.Finish(request.Authenticated ? key : null, true);
            }
            catch (InvalidOperationException)
            {
                // The server's realm and nonce left no room for the request: it is never sent.
                return null;
            }

            lastSentAt = now;
            var timing = request.Kind == RequestKind.Release || request.Kind == RequestKind.Allocate ? StunTiming.Gathering : StunTiming.Relay;
            var transaction = stun.Begin(Server, requestBuffer, length, id, timing, this, request, now);
            if (transaction != null && request.Kind == RequestKind.Allocate && request.Authenticated) holds = true;
            return transaction;
        }

        private enum RequestKind
        {
            Allocate,
            Refresh,
            Release,
            Permission,
            Channel
        }

        /// <summary>What a transaction asked for, so its answer, a retry or a timeout lands in the right place.</summary>
        private sealed class Request
        {
            internal Request(RequestKind kind, bool authenticated, int staleRetries = 0, List<Permission>? permissions = null, Peer? peer = null)
            {
                Kind = kind;
                Authenticated = authenticated;
                StaleRetries = staleRetries;
                Permissions = permissions;
                Peer = peer;
            }

            internal RequestKind Kind { get; }

            internal bool Authenticated { get; }

            internal int StaleRetries { get; }

            internal List<Permission>? Permissions { get; }

            internal Peer? Peer { get; }

            internal int Method
            {
                get
                {
                    switch (Kind)
                    {
                        case RequestKind.Allocate:
                            return StunMethod.Allocate;
                        case RequestKind.Permission:
                            return StunMethod.CreatePermission;
                        case RequestKind.Channel:
                            return StunMethod.ChannelBind;
                        default:
                            return StunMethod.Refresh;
                    }
                }
            }

            internal Request Authenticate() => new Request(Kind, true, StaleRetries, Permissions, Peer);

            internal Request Stale() => new Request(Kind, true, StaleRetries + 1, Permissions, Peer);
        }
    }
}
