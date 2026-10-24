using System;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// Something a joined player's mod made here on its own (WS3.7): a prediction of a spawn on the player's intent, or a
    /// shadow of a scripted one. It stays frozen, kinematic and unmarked, until it stands for one of the host's entities,
    /// when the network world binds it in place of a replica, so the mod keeps the same handle and <see cref="IEntity.Id"/>.
    /// It is never recorded as canonical. Main thread only.
    /// </summary>
    internal abstract class LocalSpawn
    {
        private readonly IDisposable? owned;
        private readonly IInternalSpawnHandler? handler;

        protected LocalSpawn(string modId, string kind, byte[] description, IEntity entity, IDisposable? owned, IInternalSpawnHandler? handler)
        {
            if (string.IsNullOrEmpty(modId)) throw new ArgumentException("A local spawn needs its mod.", nameof(modId));
            if (!NetworkEntityTable.IsValidKind(kind)) throw new ArgumentException("A local spawn needs its kind.", nameof(kind));
            ModId = modId;
            Kind = kind;
            Description = description ?? throw new ArgumentNullException(nameof(description));
            Entity = entity ?? throw new ArgumentNullException(nameof(entity));
            EntityKey = entity.Id;
            this.owned = owned;
            this.handler = handler;
        }

        /// <summary>Gets the mod whose code made it.</summary>
        internal string ModId { get; }

        /// <summary>Gets its kind: <c>asset.prefab</c>, or a provider's.</summary>
        internal string Kind { get; }

        /// <summary>
        /// Gets what the host's entity must be described as for this to stand for it: an asset spawn's bundle path and asset
        /// name, encoded as world entries carry them, or a provider's description.
        /// </summary>
        internal byte[] Description { get; }

        /// <summary>Gets the handle its mod holds.</summary>
        internal IEntity Entity { get; }

        /// <summary>Gets the entity's process-local id when it was made, the key the network decorators find it by.</summary>
        internal string EntityKey { get; }

        /// <summary>Gets or sets its root in the engine, once frozen; null when the engine side cannot adopt it.</summary>
        internal INetworkWorldObject? Object { get; set; }

        /// <summary>Gets or sets the host entity it stands for, or none while it waits.</summary>
        internal NetworkEntityId Bound { get; set; }

        /// <summary>Gets whether it was ended here: its handle is dead, or about to be.</summary>
        internal bool IsEnded { get; private set; }

        /// <summary>Gets whether its instance still exists.</summary>
        internal bool IsAlive
        {
            get
            {
                try
                {
                    return Entity.IsAlive;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Ends the instance here, once: a provider's goes through its kind's spawn handler, and an asset spawn is disposed, as
        /// its mod's own <c>Dispose</c> would. A failure to end it is the engine's; the instance is let go of either way.
        /// </summary>
        internal void End()
        {
            if (IsEnded) return;
            IsEnded = true;
            try
            {
                if (handler != null) handler.Despawn(Entity);
                else owned?.Dispose();
            }
            catch (Exception)
            {
                // The instance goes with its mod's lifetime instead.
            }
        }

        /// <summary>Whether a host entry of <paramref name="kind"/> described by <paramref name="description"/> is what this stands for.</summary>
        internal bool Matches(string kind, byte[] description)
        {
            if (!string.Equals(Kind, kind, StringComparison.Ordinal)) return false;

            // A provider's description may carry what changes from one run to the next; its kind is what it is.
            if (!string.Equals(kind, MultiplayerSpawnKinds.AssetPrefab, StringComparison.Ordinal)) return true;
            if (description.Length != Description.Length) return false;
            for (var index = 0; index < description.Length; index++)
            {
                if (description[index] != Description[index]) return false;
            }

            return true;
        }
    }

    /// <summary>
    /// A spawn for this player, made here and asked of the host (WS3.7): on the player's intent, or by a participant context's
    /// own code, which acts for its player regardless. It holds its provisional id, its request, where it stands, and the
    /// context's UI service its refusal is shown through.
    /// </summary>
    internal sealed class ClientPrediction : LocalSpawn
    {
        internal ClientPrediction(
            uint provisional,
            string modId,
            string kind,
            byte[] description,
            IEntity entity,
            IDisposable? owned,
            IInternalSpawnHandler? handler,
            IntentSource source,
            bool fromInputFrame,
            NetVector3 scale,
            NetPose pose,
            IUiService? ui)
            : base(modId, kind, description, entity, owned, handler)
        {
            if (provisional == 0) throw new ArgumentOutOfRangeException(nameof(provisional));
            if (!IntentRules.IsDefined(source)) throw new ArgumentOutOfRangeException(nameof(source));
            Provisional = provisional;
            Source = source;
            FromInputFrame = fromInputFrame;
            Scale = scale;
            Pose = pose;
            Ui = ui;
        }

        /// <summary>Gets the id this player gave the spawn, which its request and the host's verdict name.</summary>
        internal uint Provisional { get; }

        /// <summary>Gets the intent it acts on, or <see cref="IntentSource.None"/> for a participant context's own code.</summary>
        internal IntentSource Source { get; }

        internal bool FromInputFrame { get; }

        internal NetVector3 Scale { get; }

        /// <summary>Gets or sets where its request places it; a move before the request leaves changes it.</summary>
        internal NetPose Pose { get; set; }

        internal IUiService? Ui { get; }

        internal PredictionState State { get; private set; } = PredictionState.Queued;

        /// <summary>Gets when its request last left, on the world's clock.</summary>
        internal long SentMicros { get; private set; }

        /// <summary>Gets when the host's verdict adopted it, on the world's clock.</summary>
        internal long AdoptedMicros { get; private set; }

        /// <summary>
        /// Gets or sets how many baselines had begun here when the host adopted it: a baseline that began after the adoption
        /// lists its entity, unless the entity ended first.
        /// </summary>
        internal long AdoptedInBaseline { get; set; }

        /// <summary>Applies one event to the prediction's state machine and returns the state it reached.</summary>
        internal PredictionState Apply(PredictionEvent happened, long nowMicros)
        {
            var before = State;
            State = SpawnAuthorityRules.Next(State, happened);
            if (happened == PredictionEvent.Sent && before == PredictionState.Queued && State == PredictionState.Sent) SentMicros = nowMicros;
            if (happened == PredictionEvent.Adopted && before != PredictionState.Adopted && State == PredictionState.Adopted) AdoptedMicros = nowMicros;
            return State;
        }
    }

    /// <summary>
    /// A scripted spawn made here (WS3.7): the host's own run of the same script makes it canonically, and this stands for
    /// that spawn once it arrives with the same mod and shadow key.
    /// </summary>
    internal sealed class ClientShadow : LocalSpawn
    {
        internal ClientShadow(long key, string modId, string kind, byte[] description, IEntity entity, IDisposable? owned, IInternalSpawnHandler? handler, long madeMicros)
            : base(modId, kind, description, entity, owned, handler)
        {
            if (key < 1) throw new ArgumentOutOfRangeException(nameof(key));
            Key = key;
            MadeMicros = madeMicros;
        }

        /// <summary>Gets the shadow's number in its mod's shadowable spawns, which the host's spawn names as its origin.</summary>
        internal long Key { get; }

        /// <summary>Gets when it was made, on the world's clock.</summary>
        internal long MadeMicros { get; }

        internal ShadowState State { get; private set; }

        internal NetSpawnOrigin Origin => new NetSpawnOrigin(ModId, Key);

        /// <summary>Applies one event to the shadow's state machine and returns the state it reached.</summary>
        internal ShadowState Apply(ShadowEvent happened)
        {
            State = SpawnAuthorityRules.Next(State, happened);
            return State;
        }
    }
}
