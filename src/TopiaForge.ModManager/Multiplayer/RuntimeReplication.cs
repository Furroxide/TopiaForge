using System;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// One mod context's replication seam: the <see cref="IRuntimeReplication"/> first-party providers reach through
    /// <see cref="IInternalReplicationContext"/>, and what the context's network decorators record through. It is bound
    /// to the context's mod, network profile and lifetime: what it registers ends with the lifetime, and a stopping context
    /// is refused. Main thread only. The stubs are documented on <see cref="IRuntimeReplication"/>.
    /// </summary>
    internal sealed partial class RuntimeReplication : IRuntimeReplication
    {
        private const string Stopping = "The context is stopping.";
        private const string NotRunHere = "This context does not run in this realm, so it changes nothing here.";

        private readonly MultiplayerReplication services;
        private readonly IModLifetime lifetime;
        private bool releaseTracked;

        internal RuntimeReplication(MultiplayerReplication services, string ownerModId, ModNetProfile profile, IModLifetime lifetime)
        {
            this.services = services ?? throw new ArgumentNullException(nameof(services));
            if (string.IsNullOrEmpty(ownerModId)) throw new ArgumentException("The seam needs the mod it belongs to.", nameof(ownerModId));
            this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
            OwnerModId = ownerModId;
            Profile = profile;
        }

        /// <summary>Gets the mod the context belongs to.</summary>
        internal string OwnerModId { get; }

        /// <summary>Gets the context's network profile: its mod's network class and its execution context.</summary>
        internal ModNetProfile Profile { get; }

        /// <summary>Gets the process's replication services the seam is bound to.</summary>
        internal MultiplayerReplication Services => services;
        internal IModLifetime Lifetime => lifetime;

        public InternalNetRealm Realm => (InternalNetRealm)(int)services.Runtime.Realm;

        public InternalNetworkClass NetworkClass => (InternalNetworkClass)(int)Profile.NetworkClass;

        public InternalExecutionContext ExecutionContext => (InternalExecutionContext)(int)Profile.Context;

        public InternalTimeAuthority TimeAuthority =>
            (InternalTimeAuthority)(int)ExecutionRouting.TimeControl(services.Runtime.Realm, Profile);

        public bool NativePauseFreezesWorld => ExecutionRouting.NativePause(services.Runtime.Realm) == NativePauseRoute.FreezesWorld;

        public InternalRoute Route(InternalIntent intent)
        {
            var source = (IntentSource)(int)intent;
            if (!IntentRules.IsDefined(source)) throw new ArgumentOutOfRangeException(nameof(intent));
            return (InternalRoute)(int)ExecutionRouting.Route(services.Runtime.Realm, Profile, source);
        }

        public OperationResult<IDisposable> RegisterReplicaFactory(string kind, IInternalReplicaFactory factory)
        {
            if (kind == null) throw new ArgumentNullException(nameof(kind));
            if (factory == null) throw new ArgumentNullException(nameof(factory));
            UnityMainThreadGuard.AssertCurrent();
            var refused = Admit<IDisposable>() ?? CheckKind<IDisposable>(kind, "replica");
            if (refused != null) return refused;
            var registration = services.AddFactory(OwnerModId, kind, factory);
            if (registration == null)
            {
                return OperationResult<IDisposable>.Failure(ModErrorCode.Conflict, "Another provider already builds replicas of that kind.");
            }

            return TrackRegistration(registration);
        }

        public OperationResult<bool> RegisterCanonical(IEntity entity, InternalSpawnDescriptor descriptor)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            UnityMainThreadGuard.AssertCurrent();
            var refused = Admit<bool>() ?? CheckReserved<bool>(descriptor.Kind);
            if (refused != null) return refused;
            if (!entity.IsAlive) return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "The entity no longer exists.");
            if (services.Runtime.Realm == NetRealm.Client)
            {
                // A joined player records nothing as canonical: its world content binds by key to the host's, and its SDK
                // spawns are shadows or predictions that authority routing (WS3.7) binds in place of the host's replicas.
                if (descriptor.Source == InternalSpawnSource.WorldContent)
                {
                    if (TrackRelease()) services.LocalContent?.RegisterLocalContent(this, descriptor.ContentKey!, entity);
                    return OperationResult<bool>.Success(true);
                }

                return RouteRegistration(entity, descriptor);
            }

            if (!TrackRelease()) return OperationResult<bool>.Failure(ModErrorCode.Cancelled, Stopping);
            // Describing a recorded entity again keeps its key, so it takes none of its mod's numbering.
            var shadowKey = services.Runtime.Spawns.Find(entity) == null ? CanonicalShadowKey() : 0;
            services.Runtime.Spawns.Record(entity, Describe(descriptor), this, shadowKey, 0);
            return OperationResult<bool>.Success(true);
        }

        public OperationResult<IEntity> RequestSpawn(InternalSpawnDescriptor descriptor, Func<OperationResult<IEntity>> spawn)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            if (spawn == null) throw new ArgumentNullException(nameof(spawn));
            UnityMainThreadGuard.AssertCurrent();
            var refused = Admit<IEntity>() ?? CheckReserved<IEntity>(descriptor.Kind);
            if (refused != null) return refused;

            // A joined player's spawn is a prediction or a shadow, never recorded as canonical (WS3.7).
            if (services.Runtime.Realm == NetRealm.Client) return RouteSpawnRequest(descriptor, spawn);
            var spawned = spawn() ?? throw new InvalidOperationException("The spawn returned no result.");
            if (spawned.TryGetValue(out var entity) && entity.IsAlive)
            {
                services.RecordQuietly(entity, Describe(descriptor), this, CanonicalShadowKey());
            }

            return spawned;
        }

        public OperationResult<IDisposable> RegisterSpawnHandler(string kind, IInternalSpawnHandler handler)
        {
            if (kind == null) throw new ArgumentNullException(nameof(kind));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            UnityMainThreadGuard.AssertCurrent();
            var refused = Admit<IDisposable>() ?? CheckKind<IDisposable>(kind, "spawn");
            if (refused != null) return refused;
            var registration = services.AddSpawnHandler(OwnerModId, kind, handler);
            if (registration == null)
            {
                return OperationResult<IDisposable>.Failure(ModErrorCode.Conflict, "Another provider already makes objects of that kind for players.");
            }

            return TrackRegistration(registration);
        }

        public OperationResult<IDisposable> RegisterCommand(string kind, Func<InternalCommand, OperationResult<bool>> handler)
        {
            if (kind == null) throw new ArgumentNullException(nameof(kind));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            UnityMainThreadGuard.AssertCurrent();
            var refused = Admit<IDisposable>() ?? CheckKind<IDisposable>(kind, "command");
            if (refused != null) return refused;
            var registration = services.AddCommand(OwnerModId, kind, handler);
            if (registration == null)
            {
                return OperationResult<IDisposable>.Failure(ModErrorCode.Conflict, "This mod already handles commands of that kind.");
            }

            return TrackRegistration(registration);
        }

        public OperationResult<bool> SubmitCommand(string kind, byte[] payload)
        {
            if (kind == null) throw new ArgumentNullException(nameof(kind));
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            UnityMainThreadGuard.AssertCurrent();
            var refused = Admit<bool>();
            if (refused != null) return refused;
            if (!InternalReplicationLimits.IsKind(kind))
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "A command kind is a lowercase token of at most 64 characters.");
            }

            if (payload.Length > InternalReplicationLimits.MaximumPayloadBytes)
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "A command is at most 16 KiB.");
            }

            if (!services.TryGetCommand(OwnerModId, kind, out var handler))
            {
                return OperationResult<bool>.Failure(ModErrorCode.NotFound, "This mod handles no command of that kind.");
            }

            // Stub until authority routing (WS3.7): the handler runs here at once in every realm, as in single player.
            return handler!(new InternalCommand(kind, payload, null)) ?? throw new InvalidOperationException("The command handler returned no result.");
        }

        public bool TryCreateRef(IEntity entity, out InternalNetRef reference)
        {
            if (entity == null) throw new ArgumentNullException(nameof(entity));
            UnityMainThreadGuard.AssertCurrent();
            return services.TryCreateRef(entity, out reference);
        }

        public bool TryResolveRef(InternalNetRef reference, out IEntity? entity)
        {
            UnityMainThreadGuard.AssertCurrent();
            return services.TryResolveRef(reference, out entity);
        }

        public OperationResult<IInternalMicrophoneLease> AcquireMicrophone(InternalMicrophoneUse use, string reason)
        {
            if (reason == null) throw new ArgumentNullException(nameof(reason));
            UnityMainThreadGuard.AssertCurrent();
            var refused = Admit<IInternalMicrophoneLease>();
            if (refused != null) return refused;
            var acquired = services.Microphone.TryAcquire(use, reason);
            if (!acquired.TryGetValue(out var hold))
            {
                return OperationResult<IInternalMicrophoneLease>.Failure(acquired.ErrorCode, acquired.ErrorMessage);
            }

            IDisposable tracking;
            try
            {
                tracking = lifetime.Track(new Release(hold));
            }
            catch (ObjectDisposedException)
            {
                // The lifetime began to stop in between, and took the hold with it.
                return OperationResult<IInternalMicrophoneLease>.Failure(ModErrorCode.Cancelled, Stopping);
            }

            return OperationResult<IInternalMicrophoneLease>.Success(new MicrophoneLease(hold, tracking));
        }

        /// <summary>
        /// Records a runtime box with the scope that owns it, including boxes placed before hosting starts.
        /// </summary>
        internal void RecordBlocker(IBlocker blocker, InternalSpawnDescriptor descriptor)
        {
            if (!TrackRelease()) throw new ObjectDisposedException(nameof(RuntimeReplication));
            services.Runtime.Spawns.Record(blocker, Describe(descriptor), this, CanonicalShadowKey(), 0);
        }

        /// <summary>
        /// Records an asset spawn for a later promotion, described by its prefab's bundle and asset name rather than by the
        /// request, whose prefab handle belongs to the spawning scope. A failure is reported, never thrown at the mod whose
        /// spawn succeeded.
        /// </summary>
        internal void RecordAssetSpawn(ISpawnedEntity spawned, AssetSpawnRequest request)
        {
            // Client-local and standalone mods are never baselined, so their spawns cost nothing more than before.
            if (!MultiplayerSpawnLedger.Records(Profile)) return;

            // A joined player's spawn is never canonical: authority routing (WS3.7) forwards it to the host, or shadows the host's.
            if (services.Runtime.Realm == NetRealm.Client) return;
            services.RecordQuietly(spawned, new MultiplayerSpawnDescription(OwnerModId, Profile, MultiplayerSpawnSource.SdkSpawn,
                MultiplayerSpawnKinds.AssetPrefab, null, request.Transform, MultiplayerAssetDescriptor.Of(request.Prefab)), this, CanonicalShadowKey());
        }

        /// <summary>
        /// Ends this seam's records, and its world content waiting to bind, when its context's lifetime ends: tracked once,
        /// at the first record. False when the lifetime is already stopping.
        /// </summary>
        internal bool TrackRelease()
        {
            if (releaseTracked) return true;
            try
            {
                lifetime.Track(new SeamRelease(services, this));
            }
            catch (ObjectDisposedException)
            {
                return false;
            }

            releaseTracked = true;
            return true;
        }

        private MultiplayerSpawnDescription Describe(InternalSpawnDescriptor descriptor) =>
            new MultiplayerSpawnDescription(OwnerModId, Profile, (MultiplayerSpawnSource)(int)descriptor.Source, descriptor.Kind,
                descriptor.ContentKey, descriptor.Transform, descriptor);

        private OperationResult<T>? Admit<T>() where T : notnull
        {
            if (lifetime.IsStopping) return OperationResult<T>.Failure(ModErrorCode.Cancelled, Stopping);
            return Profile.RunsOn(services.Runtime.Realm) ? null : OperationResult<T>.Failure(ModErrorCode.NotAuthoritative, NotRunHere);
        }

        private static OperationResult<T>? CheckKind<T>(string kind, string what) where T : notnull
        {
            if (!InternalReplicationLimits.IsKind(kind))
            {
                return OperationResult<T>.Failure(ModErrorCode.InvalidArgument, "A " + what + " kind is a lowercase token of at most 64 characters.");
            }

            return CheckReserved<T>(kind);
        }

        private static OperationResult<T>? CheckReserved<T>(string kind) where T : notnull =>
            MultiplayerSpawnKinds.IsReserved(kind)
                ? OperationResult<T>.Failure(ModErrorCode.InvalidArgument, "Kinds that start with \"" + MultiplayerSpawnKinds.RuntimePrefix + "\" are the runtime's own.")
                : null;

        private OperationResult<IDisposable> TrackRegistration(IDisposable registration)
        {
            try
            {
                return OperationResult<IDisposable>.Success(lifetime.Track(registration));
            }
            catch (ObjectDisposedException)
            {
                // The lifetime began to stop in between, and removed the registration with it.
                return OperationResult<IDisposable>.Failure(ModErrorCode.Cancelled, Stopping);
            }
        }

        /// <summary>Ends a seam's records and its world content waiting to bind, once, when its context's lifetime ends.</summary>
        private sealed class SeamRelease : IDisposable
        {
            private readonly MultiplayerReplication services;
            private RuntimeReplication? seam;

            internal SeamRelease(MultiplayerReplication services, RuntimeReplication seam)
            {
                this.services = services;
                this.seam = seam;
            }

            public void Dispose()
            {
                var ending = seam;
                if (ending == null) return;
                seam = null;
                services.Runtime.Spawns.ReleaseOwner(ending);
                services.LocalContent?.ReleaseOwner(ending);
                services.AssetLoads.ReleaseOwner(ending);
            }
        }

        /// <summary>Gives a hold back when the context's lifetime ends, or when its lease is disposed first.</summary>
        private sealed class Release : IDisposable
        {
            private readonly MultiplayerMicrophoneArbiter.Hold hold;

            internal Release(MultiplayerMicrophoneArbiter.Hold hold)
            {
                this.hold = hold;
            }

            public void Dispose() => hold.Release();
        }

        private sealed class MicrophoneLease : IInternalMicrophoneLease
        {
            private readonly MultiplayerMicrophoneArbiter.Hold hold;
            private readonly IDisposable tracking;

            internal MicrophoneLease(MultiplayerMicrophoneArbiter.Hold hold, IDisposable tracking)
            {
                this.hold = hold;
                this.tracking = tracking;
            }

            public InternalMicrophoneUse Use => hold.Use;

            public bool IsPreempted => hold.IsPreempted;

            public bool IsReleased => hold.IsReleased;

            // Disposing the lifetime's lease releases the hold once and stops the lifetime holding it.
            public void Dispose() => tracking.Dispose();
        }
    }
}
