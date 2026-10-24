using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// Names entities and players by references that mean the same on every peer. The network world (WS3.6) names
    /// entities, and the players directory (WS3.11) players. Main thread only.
    /// </summary>
    internal interface IMultiplayerReferenceSource
    {
        bool TryCreateRef(IEntity entity, out InternalNetRef reference);

        bool TryResolveRef(InternalNetRef reference, out IEntity? entity);
    }

    /// <summary>
    /// Takes the world content a joined player's own world provider builds, so the network world (WS3.6) binds it by its
    /// content key to the host's, which names it. Main thread only.
    /// </summary>
    internal interface IMultiplayerLocalContent
    {
        /// <summary>Keeps <paramref name="entity"/> as this player's world content of <paramref name="contentKey"/>, made through <paramref name="owner"/>.</summary>
        void RegisterLocalContent(object owner, string contentKey, IEntity entity);

        /// <summary>Forgets the content <paramref name="owner"/> registered: its scope has ended.</summary>
        void ReleaseOwner(object owner);
    }

    /// <summary>
    /// Authority routing's side of the network world (WS3.7), through which a joined player's mod contexts route their
    /// spawns, transform changes and destroys. Main thread only.
    /// </summary>
    internal interface IMultiplayerAuthority
    {
        /// <summary>Gets whether this process mirrors a host's world, so a joined player's calls route through it.</summary>
        bool IsJoined { get; }

        /// <summary>Says whether a spawn on the player's intent may be predicted now, before its mod's spawn runs here.</summary>
        SpawnRefusal CheckPrediction();

        /// <summary>
        /// Takes a spawn a mod made here for its player as a prediction: frozen here and asked of the host, and adopted in place
        /// under the same handle or ended with a toast through <paramref name="ui"/>. <paramref name="source"/> is the intent
        /// it acts on, or none for a participant context's own code, which acts for its player regardless.
        /// </summary>
        void Predict(string modId, string kind, byte[] description, IEntity entity, IDisposable? owned, IInternalSpawnHandler? handler, IntentSource source,
            bool fromInputFrame, TransformState transform, IUiService? ui);

        /// <summary>Gets whether a scripted spawn may be shadowed now.</summary>
        bool CanShadow { get; }

        /// <summary>Gets whether <paramref name="entity"/> is already a prediction or a shadow of this player's.</summary>
        bool IsLocal(IEntity entity);

        /// <summary>Takes a scripted spawn a mod made here as a shadow of the host's spawn with its mod's next shadow key.</summary>
        void Shadow(string modId, string kind, byte[] description, IEntity entity, IDisposable? owned, IInternalSpawnHandler? handler);

        /// <summary>Routes a transform change; null when the entity is none of the network world's, so the call goes on as offline.</summary>
        OperationResult<TransformState>? RouteTransform(IEntity entity, TransformState transform, IEntityService inner, bool scripted);

        /// <summary>Routes a destroy; null when the entity is none of the network world's.</summary>
        OperationResult<bool>? RouteDestroy(IEntity entity, bool scripted);

        /// <summary>
        /// A mod starts loading one of its bundles itself: a load of it the network world made, for a joined player's spawn on
        /// a listen host or for a joined player's replicas, gives way first, so the game does not refuse the mod's load.
        /// </summary>
        void YieldBundle(string modId, string bundlePath);
    }

    /// <summary>
    /// The process's replication services behind every mod context's <see cref="IRuntimeReplication"/>: the realm and the
    /// spawn ledger of the composition root, the replica factories and command handlers providers register, the reference
    /// seam and the microphone arbiter. Each mod context binds a <see cref="RuntimeReplication"/> to it with its own
    /// profile and lifetime. Main thread only. See docs/internal/MultiplayerRuntimeIntegration.md, section 11.
    /// </summary>
    internal sealed class MultiplayerReplication
    {
        private readonly Dictionary<string, Registration<IInternalReplicaFactory>> factories =
            new Dictionary<string, Registration<IInternalReplicaFactory>>(StringComparer.Ordinal);
        private readonly Dictionary<string, Registration<Func<InternalCommand, OperationResult<bool>>>> commands =
            new Dictionary<string, Registration<Func<InternalCommand, OperationResult<bool>>>>(StringComparer.Ordinal);
        private readonly Dictionary<string, Registration<IInternalSpawnHandler>> spawnHandlers =
            new Dictionary<string, Registration<IInternalSpawnHandler>>(StringComparer.Ordinal);
        private bool recordFailureLogged;
        private bool referenceFailureLogged;
        private int makingForPlayer;

        internal MultiplayerReplication(MultiplayerRuntime runtime)
        {
            Runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        }

        /// <summary>Gets the composition root: the realm, and the ledger spawns are recorded in.</summary>
        internal MultiplayerRuntime Runtime { get; }

        internal IMultiplayerMotion? Motion { get; set; }

        /// <summary>Gets the microphone's arbiter, which every context's leases share.</summary>
        internal MultiplayerMicrophoneArbiter Microphone { get; } = new MultiplayerMicrophoneArbiter();

        /// <summary>
        /// Gets or sets the reference seam the network world (WS3.6) and the players directory (WS3.11) fill. Until one is
        /// set, no reference is made or resolved. A source that throws answers no and is reported.
        /// </summary>
        internal IMultiplayerReferenceSource? References { get; set; }

        /// <summary>
        /// Gets or sets where a joined player's world content waits to bind by key to the host's: the network world. Until
        /// one is set, a joined player's world content binds to nothing.
        /// </summary>
        internal IMultiplayerLocalContent? LocalContent { get; set; }

        /// <summary>
        /// Gets or sets authority routing (WS3.7): the network world. Until one is set, a joined player's spawns, transform
        /// changes and destroys run here and are recorded as nothing, as they did before authority routing.
        /// </summary>
        internal IMultiplayerAuthority? Authority { get; set; }

        /// <summary>Gets the bundles and prefabs each mod's own code loaded, which a listen host makes a player's asset spawns from first.</summary>
        internal ModAssetLoads AssetLoads { get; } = new ModAssetLoads();

        /// <summary>
        /// Gets whether a listen host is making an object for a joined player now, through its kind's spawn handler: what the
        /// handler records is the player's, so it takes no shadow key, whatever intent the handler's code sees (WS3.7).
        /// </summary>
        internal bool MakingForPlayer => makingForPlayer > 0;

        /// <summary>Runs <paramref name="make"/> as the host making an object for a joined player (<see cref="MakingForPlayer"/>).</summary>
        internal T MakeForPlayer<T>(Func<T> make)
        {
            if (make == null) throw new ArgumentNullException(nameof(make));
            makingForPlayer++;
            try
            {
                return make();
            }
            finally
            {
                makingForPlayer--;
            }
        }

        /// <summary>Creates the services of a process with no multiplayer provider: single player, as a mod context made outside ModRuntime has it.</summary>
        internal static MultiplayerReplication Offline()
        {
            var log = new SilentLog();
            return new MultiplayerReplication(new MultiplayerRuntime(() => null, new MultiplayerRuntimeHost("unknown", TopiaForgeVersions.LoaderVersion, log), log));
        }

        /// <summary>Gets the network profile of a mod's package context: the runtime's network class for its manifest, running process-wide.</summary>
        internal ModNetProfile ProfileFor(ModManifest manifest) =>
            new ModNetProfile(MultiplayerRuntime.NetworkClassOf(manifest), ModExecutionContext.Process);

        /// <summary>Binds the seam to one mod context.</summary>
        internal RuntimeReplication Bind(string ownerModId, ModNetProfile profile, IModLifetime lifetime) =>
            new RuntimeReplication(this, ownerModId, profile, lifetime);

        /// <summary>Gets the factory registered for <paramref name="kind"/>; the network world (WS3.6) builds replicas through it.</summary>
        internal bool TryGetReplicaFactory(string kind, out IInternalReplicaFactory? factory)
        {
            if (kind == null) throw new ArgumentNullException(nameof(kind));
            var found = factories.TryGetValue(kind, out var registration);
            factory = found ? registration!.Value : null;
            return found;
        }

        /// <summary>Gets the handler <paramref name="ownerModId"/> registered for its commands of <paramref name="kind"/>.</summary>
        internal bool TryGetCommand(string ownerModId, string kind, out Func<InternalCommand, OperationResult<bool>>? handler)
        {
            if (ownerModId == null) throw new ArgumentNullException(nameof(ownerModId));
            if (kind == null) throw new ArgumentNullException(nameof(kind));
            var found = commands.TryGetValue(CommandKey(ownerModId, kind), out var registration);
            handler = found ? registration!.Value : null;
            return found;
        }

        /// <summary>Gets the spawn handler registered for <paramref name="kind"/>, and the mod that registered it.</summary>
        internal bool TryGetSpawnHandler(string kind, out IInternalSpawnHandler? handler, out string? ownerModId)
        {
            if (kind == null) throw new ArgumentNullException(nameof(kind));
            var found = spawnHandlers.TryGetValue(kind, out var registration);
            handler = found ? registration!.Value : null;
            ownerModId = found ? registration!.OwnerModId : null;
            return found;
        }

        internal Registration<IInternalSpawnHandler>? AddSpawnHandler(string ownerModId, string kind, IInternalSpawnHandler handler)
        {
            if (spawnHandlers.ContainsKey(kind)) return null;
            var registration = new Registration<IInternalSpawnHandler>(spawnHandlers, kind, ownerModId, handler);
            spawnHandlers.Add(kind, registration);
            return registration;
        }

        internal Registration<IInternalReplicaFactory>? AddFactory(string ownerModId, string kind, IInternalReplicaFactory factory)
        {
            if (factories.ContainsKey(kind)) return null;
            var registration = new Registration<IInternalReplicaFactory>(factories, kind, ownerModId, factory);
            factories.Add(kind, registration);
            return registration;
        }

        internal Registration<Func<InternalCommand, OperationResult<bool>>>? AddCommand(
            string ownerModId,
            string kind,
            Func<InternalCommand, OperationResult<bool>> handler)
        {
            var key = CommandKey(ownerModId, kind);
            if (commands.ContainsKey(key)) return null;
            var registration = new Registration<Func<InternalCommand, OperationResult<bool>>>(commands, key, ownerModId, handler);
            commands.Add(key, registration);
            return registration;
        }

        /// <summary>
        /// Records a spawn <paramref name="owner"/>'s context made, without letting a failure reach the mod whose spawn
        /// already succeeded; a failure is reported once until a spawn records again. The record ends with the context.
        /// </summary>
        internal void RecordQuietly(IEntity entity, MultiplayerSpawnDescription description, RuntimeReplication owner, long shadowKey = 0)
        {
            try
            {
                if (!owner.TrackRelease()) return;
                Runtime.Spawns.Record(entity, description, owner, shadowKey, 0);
                recordFailureLogged = false;
            }
            catch (Exception exception)
            {
                if (!recordFailureLogged)
                {
                    Runtime.Log.Error("Multiplayer: a spawn could not be recorded for a later promotion: " + exception.GetType().Name + ".");
                }

                recordFailureLogged = true;
            }
        }

        internal bool TryCreateRef(IEntity entity, out InternalNetRef reference)
        {
            reference = default;
            var source = References;
            if (source == null) return false;
            try
            {
                var made = source.TryCreateRef(entity, out reference);
                referenceFailureLogged = false;
                if (!made) reference = default;
                return made;
            }
            catch (Exception exception)
            {
                ReportReferenceFailure(exception);
                reference = default;
                return false;
            }
        }

        internal bool TryResolveRef(InternalNetRef reference, out IEntity? entity)
        {
            entity = null;
            var source = References;
            if (source == null || !reference.IsSpecified) return false;
            try
            {
                var found = source.TryResolveRef(reference, out entity);
                referenceFailureLogged = false;
                if (!found) entity = null;
                return found && entity != null;
            }
            catch (Exception exception)
            {
                ReportReferenceFailure(exception);
                entity = null;
                return false;
            }
        }

        private void ReportReferenceFailure(Exception exception)
        {
            if (!referenceFailureLogged) Runtime.Log.Error("Multiplayer: the reference source failed: " + exception.GetType().Name + ".");
            referenceFailureLogged = true;
        }

        // A mod's command kinds are its own: two mods may each have a "spawn", and neither runs the other's.
        private static string CommandKey(string ownerModId, string kind) => ownerModId.ToLowerInvariant() + "\n" + kind;

        /// <summary>One registered factory or handler. Disposing it removes it, once, if it is still the registered one.</summary>
        internal sealed class Registration<T> : IDisposable
        {
            private readonly Dictionary<string, Registration<T>> owner;
            private readonly string key;
            private bool disposed;

            internal Registration(Dictionary<string, Registration<T>> owner, string key, string ownerModId, T value)
            {
                this.owner = owner;
                this.key = key;
                OwnerModId = ownerModId;
                Value = value;
            }

            internal string OwnerModId { get; }

            internal T Value { get; }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                if (owner.TryGetValue(key, out var current) && ReferenceEquals(current, this)) owner.Remove(key);
            }
        }

        private sealed class SilentLog : IMultiplayerLog
        {
            public void Info(string message)
            {
            }

            public void Warn(string message)
            {
            }

            public void Error(string message)
            {
            }
        }
    }
}
