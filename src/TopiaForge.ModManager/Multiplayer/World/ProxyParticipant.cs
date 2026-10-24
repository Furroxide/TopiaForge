using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>Where one object a listen host makes for a joined player stands.</summary>
    internal enum ProxySpawnState : byte
    {
        /// <summary>The host is making it: its asset loads, or its request waits for its kind's handler.</summary>
        Building = 0,

        /// <summary>Made: a canonical entity, announced to everyone, and the player's own.</summary>
        Live = 1,

        /// <summary>Ended: refused, destroyed, cancelled, or left behind by its player.</summary>
        Ended = 2
    }

    /// <summary>
    /// One object a listen host makes for a joined player (WS3.7): the player's request, its build, and the canonical entity
    /// it became, which the player's <see cref="ProxyParticipant"/> scope owns and ends.
    /// </summary>
    internal sealed class ProxySpawn
    {
        internal ProxySpawn(uint provisional, string modId, string kind, byte[] payload, TransformState transform, IntentSource source, bool fromInputFrame, long startedMicros)
        {
            if (provisional == 0) throw new ArgumentOutOfRangeException(nameof(provisional));
            Provisional = provisional;
            ModId = modId ?? throw new ArgumentNullException(nameof(modId));
            Kind = kind ?? throw new ArgumentNullException(nameof(kind));
            Payload = payload ?? throw new ArgumentNullException(nameof(payload));
            Transform = transform;
            Source = source;
            FromInputFrame = fromInputFrame;
            StartedMicros = startedMicros;
        }

        /// <summary>Gets the id the player gave it, which its verdict and the player's later requests name.</summary>
        internal uint Provisional { get; }

        internal string ModId { get; }

        internal string Kind { get; }

        /// <summary>Gets its description: an asset's bundle path and name, encoded, or a provider's payload.</summary>
        internal byte[] Payload { get; }

        /// <summary>Gets or sets where it is made; a move the player asks for while it builds changes it.</summary>
        internal TransformState Transform { get; set; }

        internal IntentSource Source { get; }

        internal bool FromInputFrame { get; }

        internal long StartedMicros { get; }

        internal ProxySpawnState State { get; set; }

        /// <summary>Gets or sets the asset's bundle path, for an asset spawn.</summary>
        internal string? BundlePath { get; set; }

        /// <summary>Gets or sets the asset's name in its bundle, for an asset spawn.</summary>
        internal string? AssetName { get; set; }

        /// <summary>Gets or sets the service its asset spawns through: the context that loaded the prefab, the mod's or the host's.</summary>
        internal IAssetService? Assets { get; set; }

        /// <summary>Gets or sets the prefab it spawns from, once loaded.</summary>
        internal IPrefabAsset? Prefab { get; set; }

        /// <summary>Gets or sets the handler its provider kind is made and ended through.</summary>
        internal IInternalSpawnHandler? Handler { get; set; }

        /// <summary>Gets or sets the canonical entity, once made.</summary>
        internal IEntity? Entity { get; set; }

        /// <summary>Gets or sets the asset spawn's own handle, which ends it.</summary>
        internal ISpawnedEntity? Spawned { get; set; }

        internal MultiplayerSpawnRecord? Record { get; set; }

        internal NetworkEntityId Id { get; set; }

        /// <summary>Gets whether it counts among its player's objects: being made, or made and alive.</summary>
        internal bool Counts => State == ProxySpawnState.Building || (State == ProxySpawnState.Live && Record != null && Record.IsLive);
    }

    /// <summary>A verdict a listen host owes one player, sent as that player's window allows.</summary>
    internal readonly struct ProxyVerdict
    {
        internal ProxyVerdict(uint provisional, NetworkEntityId adopted, SpawnRefusal refusal)
        {
            Provisional = provisional;
            Adopted = adopted;
            Refusal = refusal;
        }

        internal uint Provisional { get; }

        /// <summary>Gets the entity the spawn became, or none for a refusal.</summary>
        internal NetworkEntityId Adopted { get; }

        internal SpawnRefusal Refusal { get; }
    }

    /// <summary>
    /// A joined player's scope on the listen host (WS3.7): the objects the host made for that player, owned by the player
    /// and counted against their quota, and the verdicts the host owes them. The player's participant slot names it in the
    /// world's entries. When the player leaves, and does not come back within
    /// <see cref="SpawnAuthorityRules.DepartureGraceMicros"/>, the scope is cleaned up: everything it owns ends, and nothing
    /// else does. It is the runtime's own scope: no mod code runs in it, so it opens no session of its own. Main thread only.
    /// </summary>
    internal sealed class ProxyParticipant
    {
        /// <summary>The most verdicts one player may be owed; only a player that floods requests reaches it.</summary>
        internal const int MaximumVerdicts = 128;

        /// <summary>The most requests one player's scope tracks, live and building, ended ones forgotten: twice the quota.</summary>
        internal const int MaximumSpawns = 2 * PermissionPolicy.DefaultMaximumLiveSpawnsPerPlayer;

        private readonly Queue<ProxyVerdict> verdicts = new Queue<ProxyVerdict>();

        internal ProxyParticipant(ParticipantId participant, int slot)
        {
            if (!participant.IsValid) throw new ArgumentException("A proxy needs its participant.", nameof(participant));
            if (slot < 1 || slot > WorldEntryCodec.MaximumOwnerSlot) throw new ArgumentOutOfRangeException(nameof(slot));
            Participant = participant;
            Slot = slot;
        }

        internal ParticipantId Participant { get; }

        /// <summary>Gets the player's slot: the owner the world's entries name for this player's objects.</summary>
        internal int Slot { get; }

        /// <summary>Gets what the player asked for that the host still tracks, by provisional id.</summary>
        internal Dictionary<uint, ProxySpawn> Spawns { get; } = new Dictionary<uint, ProxySpawn>();

        /// <summary>Gets the verdicts owed to the player, oldest first.</summary>
        internal Queue<ProxyVerdict> Verdicts => verdicts;

        /// <summary>Gets or sets when the player's stream ended, or <see cref="long.MinValue"/> while it is in the session.</summary>
        internal long DepartedMicros { get; set; } = long.MinValue;

        internal bool IsDeparted => DepartedMicros != long.MinValue;

        /// <summary>Gets how many of the player's objects count against their quota: being made, or made and alive.</summary>
        internal int Counted
        {
            get
            {
                var count = 0;
                foreach (var spawn in Spawns.Values)
                {
                    if (spawn.Counts) count++;
                }

                return count;
            }
        }

        /// <summary>
        /// Forgets the spawns that no longer count: made, and ended since by the host's own code. Only a full scope pays for
        /// it; a provisional id forgotten this way is answered as a new request if the player asks for it again.
        /// </summary>
        internal void ForgetEnded()
        {
            List<uint>? ended = null;
            foreach (var pair in Spawns)
            {
                if (!pair.Value.Counts) (ended ??= new List<uint>()).Add(pair.Key);
            }

            if (ended == null) return;
            foreach (var provisional in ended) Spawns.Remove(provisional);
        }

        /// <summary>Queues a verdict for the player; false when the queue is full and the verdict dropped.</summary>
        internal bool Owe(ProxyVerdict verdict)
        {
            if (verdicts.Count >= MaximumVerdicts) return false;
            verdicts.Enqueue(verdict);
            return true;
        }
    }
}
