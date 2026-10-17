using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Multiplayer.Engine;
using TopiaForge.Multiplayer.Network;

namespace TopiaForge.Multiplayer
{
    /// <summary>The control's session members, browsing, queries and the mapping to the SPI's types.</summary>
    internal sealed partial class InternalControl
    {
        private IReadOnlyList<LanServer>? mappedServers;
        private IReadOnlyList<InternalLanListing> listings = Array.Empty<InternalLanListing>();

        public IReadOnlyList<InternalLanListing> LanSessions
        {
            get
            {
                engine.RequireOwnerThread();
                var servers = live?.LanServers ?? Array.Empty<LanServer>();

                // The browser replaces its list whenever it changes, so an unchanged list maps to the same listings.
                if (ReferenceEquals(servers, mappedServers)) return listings;
                var mapped = new InternalLanListing[servers.Count];
                for (var index = 0; index < mapped.Length; index++) mapped[index] = ToListing(servers[index]);
                mappedServers = servers;
                listings = mapped;
                return listings;
            }
        }

        public InternalLiveStatus GetStatus()
        {
            engine.RequireOwnerThread();
            var status = new InternalLiveStatus
            {
                Role = Role,
                IsLiveAvailable = live != null,
                IsReadinessHeld = engine.ReadinessHolds > 0,
                ConnectedPlayers = CountConnected(engine.Snapshot)
            };

            if (live == null) return status;
            status.HostPhase = ToPhase(live.HostPhase);
            status.HostFailure = ToFailure(live.HostFailure);
            status.IsBrowsing = live.IsBrowsing;
            if (live.Host != null) status.MaxPlayers = engine.Options.MaximumParticipants;
            var join = live.Join;
            if (pendingJoin != null)
            {
                status.JoinPhase = InternalJoinPhase.Resolving;
            }
            else if (pendingJoinFailure != InternalLiveFailure.None)
            {
                status.JoinPhase = InternalJoinPhase.Ended;
                status.JoinFailure = pendingJoinFailure;
                status.JoinDetail = pendingJoinFailure == InternalLiveFailure.IdentityUnavailable
                    ? "This game's multiplayer identity could not be read or created."
                    : pendingJoinFailure == InternalLiveFailure.Cancelled ? string.Empty : "The join could not start.";
            }
            else if (join != null)
            {
                status.JoinPhase = ToPhase(join.Phase);
                status.JoinFailure = ToFailure(join.Failure);
                status.JoinDetail = join.Detail;
                status.HostMessage = join.HostMessage;
                status.RejectionCode = (InternalAdmissionCode)(int)join.RejectionCode;

                // This game's own check found the problems, or the host's admission report listed them in its own words.
                var ownCheck = join.Content != null && !join.Content.IsReady;
                status.ContentProblems = ownCheck ? join.Content!.Problems : join.RejectionProblems;
                status.ContentProblemsFromHost = !ownCheck && join.RejectionProblems.Count > 0;
                status.ChangedServerId = join.ChangedServerId;
                if (join.Descriptor != null) status.Server = ToServerInfo(join.Descriptor);
            }

            status.Internet = InternetStatus();
            return status;
        }

        public IInternalServerQuery Query(IInternalJoinTarget target, bool allowDirect)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            engine.RequireOwnerThread();
            if (live == null) return ProviderServerQuery.Failed(engine, InternalLiveFailure.NotFound, LiveOff);
            if (!(target is ProviderJoinTarget providerTarget)) return ProviderServerQuery.Failed(engine, InternalLiveFailure.NotFound, "The join target was not made by this provider.");
            if (providerTarget.NetworkTarget.NeedsService && live.Services == null)
            {
                return ProviderServerQuery.Failed(engine, InternalLiveFailure.ServiceNotConfigured, NoService);
            }

            return new ProviderServerQuery(engine, live.StartQuery(providerTarget.NetworkTarget, allowDirect));
        }

        public void StartBrowsing()
        {
            engine.RequireOwnerThread();
            if (live == null || disposed) return;
            if (!live.StartBrowsing()) Warn("live.browse-failed", "The local network could not be browsed; another program may hold the discovery port.");
        }

        public void StopBrowsing()
        {
            engine.RequireOwnerThread();
            live?.StopBrowsing();
        }

        public bool IsParticipantReady(ParticipantId participant) => engine.IsParticipantReady(participant);

        public OperationResult<bool> ReplaceSession()
        {
            engine.RequireOwnerThread();
            if (disposed || engine.IsEnded) return Fail(ModErrorCode.InvalidState, "The multiplayer session has ended.");
            if (engine.Role != EngineRole.Host) return Fail(ModErrorCode.NotAuthoritative, "Only the host replaces the session.");
            var random = new byte[16];
            Transport.Crypto.CryptoRandom.Fill(random);
            try
            {
                engine.ReplaceSession(
                    new MultiplayerSessionId("session:" + WireText.ToHex(random).Substring(0, 16)),
                    new SessionSeed(BitConverter.ToUInt64(random, 8)));
            }
            catch (InvalidOperationException exception)
            {
                return Fail(ModErrorCode.InvalidState, exception.Message);
            }

            return OperationResult<bool>.Success(true);
        }

        public OperationResult<IInternalSystemChannel> OpenSystemChannel(InternalSystemChannelKind kind)
        {
            engine.RequireOwnerThread();
            if (disposed) return OperationResult<IInternalSystemChannel>.Failure(ModErrorCode.InvalidState, "The multiplayer provider has stopped.");
            var result = engine.OpenSystemChannel(kind);

            // Server mute drops at ingress: the host never takes in a muted player's chat, so nothing relays it.
            if (kind == InternalSystemChannelKind.Chat && result.TryGetValue(out var channel) && channel is SystemChannelEndpoint endpoint)
            {
                endpoint.Admit = AdmitChat;
            }

            return result;
        }

        public IInternalReadinessHold HoldReadiness(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A readiness hold names its reason.", nameof(reason));
            return engine.HoldReadiness();
        }

        public IDisposable SetEntityIdentitySource(IInternalEntityIdentitySource source) => engine.SetEntityIdentitySource(source);

        public IDisposable SetAvatarSource(IInternalAvatarSource source) => engine.SetAvatarSource(source);

        public InternalMultiplayerStatistics GetStatistics()
        {
            engine.RequireOwnerThread();
            var snapshot = engine.Snapshot;
            var channels = new List<InternalSystemChannelStatistics>();
            foreach (InternalSystemChannelKind kind in Enum.GetValues(typeof(InternalSystemChannelKind)))
            {
                var endpoint = engine.FindSystemChannel(kind);
                if (endpoint != null) channels.Add(endpoint.GetStatistics());
            }

            return new InternalMultiplayerStatistics
            {
                Role = Role,
                Participants = snapshot.Participants.Count,
                ConnectedPlayers = CountConnected(snapshot),
                ReadinessHolds = engine.ReadinessHolds,
                ServerMutes = engine.IsVoiceEnabled ? engine.ServerMuteCount : 0,
                SystemChannels = channels.AsReadOnly(),
                Voice = voice?.GetStatistics()
            };
        }

        /// <summary>
        /// Drops a muted player's chat as it reaches the host, by the engine's voice moderation, the same set the voice relay
        /// drops by: a mute by hand, or one the relay made for abusing voice chat.
        /// </summary>
        private bool AdmitChat(ParticipantId sender) =>
            engine.Role != EngineRole.Host || !engine.IsVoiceEnabled || !engine.IsServerMuted(sender);

        private static int CountConnected(MultiplayerSessionSnapshot snapshot)
        {
            var connected = 0;
            foreach (var participant in snapshot.Participants)
            {
                if (participant.IsConnected) connected++;
            }

            return connected;
        }

        private static InternalLanListing ToListing(LanServer server)
        {
            var listing = new InternalLanListing(ProviderJoinTarget.ForLan(server.ServerId), server.ServerId)
            {
                Incompatible = server.Incompatible
            };

            var info = server.Info;
            if (info == null) return listing;
            listing.HasInfo = true;
            listing.Name = info.Name;
            listing.GameBuild = info.GameBuild;
            listing.RuntimeVersion = info.RuntimeVersion;
            listing.ProtocolVersion = info.ProtocolVersion;
            listing.Gamemode = info.Gamemode;
            listing.World = info.World;
            listing.Players = info.Players;
            listing.MaxPlayers = info.MaxPlayers;
            listing.PasswordRequired = info.PasswordRequired;
            listing.Voice = info.Voice;
            listing.PingMilliseconds = (int)Math.Min(MultiplayerParticipant.MaximumPingMilliseconds, ((server.RoundTripMicros / 1000) + 5) / 10 * 10);
            return listing;
        }

        internal static InternalServerInfo ToServerInfo(ServerDescriptor descriptor)
        {
            var packages = new InternalHostPackage[descriptor.Packages.Count];
            for (var index = 0; index < packages.Length; index++)
            {
                var package = descriptor.Packages[index];
                var role = package.Role == DescriptorPackageRole.SessionRequired ? InternalHostPackageRole.SessionRequired
                    : package.Role == DescriptorPackageRole.SessionOptional ? InternalHostPackageRole.SessionOptional
                    : InternalHostPackageRole.Implicit;
                packages[index] = new InternalHostPackage(package.Id, package.Version, package.Sha256, role);
            }

            return new InternalServerInfo(descriptor.ServerId, descriptor.Name, packages)
            {
                GameBuild = descriptor.GameBuild,
                RuntimeVersion = descriptor.RuntimeVersion,
                ProtocolVersion = descriptor.ProtocolVersion,
                MaxPlayers = descriptor.MaxPlayers,
                PasswordRequired = descriptor.PasswordRequired,
                Voice = descriptor.Voice,
                Visibility = descriptor.Visibility == ServerVisibility.Public ? InternalSessionVisibility.Public
                    : descriptor.Visibility == ServerVisibility.Invite ? InternalSessionVisibility.Invite
                    : InternalSessionVisibility.Lan,
                Gamemode = descriptor.Gamemode,
                World = descriptor.World,
                PackageDigest = descriptor.PackageDigest
            };
        }

        private static InternalHostPhase ToPhase(HostPhase phase) => (InternalHostPhase)(int)phase;

        private static InternalJoinPhase ToPhase(JoinPhase phase) => (InternalJoinPhase)((int)phase + 1);

        internal static InternalLiveFailure ToFailure(LiveFailure failure) => (InternalLiveFailure)(int)failure;
    }

    /// <summary>A server lookup as the SPI sees it, over the network layer's query. Main thread only, like the rest of the SPI.</summary>
    internal sealed class ProviderServerQuery : IInternalServerQuery
    {
        private readonly SessionEngine engine;
        private readonly LiveQuery? query;
        private InternalServerInfo? server;
        private InternalLiveFailure failure;
        private string detail = string.Empty;
        private IReadOnlyList<string> contentProblems = Array.Empty<string>();
        private string? changedServerId;
        private bool completed;

        internal ProviderServerQuery(SessionEngine engine, LiveQuery query)
        {
            this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
            this.query = query ?? throw new ArgumentNullException(nameof(query));
        }

        private ProviderServerQuery(SessionEngine engine, InternalLiveFailure failure, string detail)
        {
            this.engine = engine;
            completed = true;
            this.failure = failure;
            this.detail = detail;
        }

        public bool IsCompleted
        {
            get
            {
                engine.RequireOwnerThread();
                if (!completed && query != null && query.IsCompleted) Complete(query);
                return completed;
            }
        }

        public InternalLiveFailure Failure
        {
            get
            {
                engine.RequireOwnerThread();
                return failure;
            }
        }

        public string Detail
        {
            get
            {
                engine.RequireOwnerThread();
                return detail;
            }
        }

        public InternalServerInfo? Server
        {
            get
            {
                _ = IsCompleted;
                return server;
            }
        }

        public IReadOnlyList<string> ContentProblems
        {
            get
            {
                engine.RequireOwnerThread();
                return contentProblems;
            }
        }

        public string? ChangedServerId
        {
            get
            {
                engine.RequireOwnerThread();
                return changedServerId;
            }
        }

        internal static ProviderServerQuery Failed(SessionEngine engine, InternalLiveFailure failure, string detail) => new ProviderServerQuery(engine, failure, detail);

        public void Dispose()
        {
            engine.RequireOwnerThread();
            if (!completed)
            {
                completed = true;
                failure = InternalLiveFailure.Cancelled;
            }

            query?.Dispose();
        }

        private void Complete(LiveQuery finished)
        {
            completed = true;
            failure = InternalControl.ToFailure(finished.Failure);
            detail = finished.Detail;
            changedServerId = finished.ChangedServerId;
            contentProblems = finished.Content?.Problems ?? Array.Empty<string>();
            if (finished.Descriptor != null) server = InternalControl.ToServerInfo(finished.Descriptor);
        }
    }
}
