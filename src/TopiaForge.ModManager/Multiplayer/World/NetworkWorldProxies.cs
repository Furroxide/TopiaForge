using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// A listen host's side of authority routing (WS3.7). Each joined player's spawns, transform changes and destroys arrive
    /// as requests, and each is validated by the replication model's <see cref="PermissionPolicy"/> and per-player
    /// <see cref="TokenBucket"/> limits before anything runs. A spawn the host allows is made canonically, as an object of
    /// that player's <see cref="ProxyParticipant"/> scope: recorded and announced like any other spawn, with the player's
    /// slot as its owner, and ended when the player leaves. The player hears the verdict, adopted or refused, under the
    /// provisional id it chose. A request never names an owner or an origin: the host takes the sender from the channel.
    /// Main thread only.
    /// </summary>
    internal sealed partial class NetworkWorldProxies : IWorldMessageHandler
    {
        private readonly NetworkWorldChannel channel;
        private readonly NetworkWorldHost host;
        private readonly MultiplayerSpawnLedger ledger;
        private readonly MultiplayerIntents intents;
        private readonly MultiplayerReplication replication;
        private readonly Func<string, IAssetService?> findAssets;
        private readonly Func<long> clock;
        private readonly NetworkWorldCounters counters;
        private readonly ProxyParticipant?[] slots = new ProxyParticipant?[WorldEntryCodec.MaximumOwnerSlot + 1];
        private readonly Dictionary<ParticipantId, ProxyParticipant> byParticipant = new Dictionary<ParticipantId, ProxyParticipant>();
        private readonly List<ProxyParticipant> proxies = new List<ProxyParticipant>();
        private readonly List<ProxyParticipant> proxyScratch = new List<ProxyParticipant>();
        private readonly List<ProxySpawn> spawnScratch = new List<ProxySpawn>();
        private readonly AuthoritySpawnRequest request = new AuthoritySpawnRequest();
        private readonly WorldWireWriter verdict = new WorldWireWriter(64);
        private PlayerRequestLimiter limiter = new PlayerRequestLimiter(PermissionPolicy.Default);
        private ulong lastGeneration;

        internal NetworkWorldProxies(
            NetworkWorldChannel channel,
            NetworkWorldHost host,
            MultiplayerSpawnLedger ledger,
            MultiplayerIntents intents,
            MultiplayerReplication replication,
            Func<string, IAssetService?> findAssets,
            Func<long> clock,
            NetworkWorldCounters counters)
        {
            this.channel = channel ?? throw new ArgumentNullException(nameof(channel));
            this.host = host ?? throw new ArgumentNullException(nameof(host));
            this.ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            this.intents = intents ?? throw new ArgumentNullException(nameof(intents));
            this.replication = replication ?? throw new ArgumentNullException(nameof(replication));
            this.findAssets = findAssets ?? throw new ArgumentNullException(nameof(findAssets));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            this.counters = counters ?? throw new ArgumentNullException(nameof(counters));
            channel.Register(AuthorityMessages.SpawnRequest, this);
            channel.Register(AuthorityMessages.TransformRequest, this);
            channel.Register(AuthorityMessages.DestroyRequest, this);
        }

        /// <summary>
        /// Gets or sets whether a mod is shared in this session, so its objects may be made for players: a mod every player
        /// runs. The network world reads it from the runtime's loaded packages.
        /// </summary>
        internal Func<string, bool> IsSharedMod { get; set; } = _ => false;

        /// <summary>Gets whether this process routes its players' requests now: while it hosts the world.</summary>
        internal bool IsActive { get; private set; }

        /// <summary>Gets how many players' scopes the host keeps, departed ones within their grace included.</summary>
        internal int ProxyCount => proxies.Count;

        /// <summary>Gets the limiter that holds each player's request budgets, so tests can see what was spent.</summary>
        internal PlayerRequestLimiter Limiter => limiter;

        internal Action<int>? ParticipantReleased { get; set; }

        internal ProxyParticipant? MotionProxy(ParticipantId participant) =>
            IsActive && channel.GetGeneration(participant) != 0 ? ProxyFor(participant) : null;

        /// <summary>Gets a player's scope, if the host keeps one.</summary>
        internal bool TryGetProxy(ParticipantId participant, out ProxyParticipant proxy) => byParticipant.TryGetValue(participant, out proxy!);

        /// <summary>Starts routing requests: this process hosts the world now.</summary>
        internal void Activate()
        {
            if (IsActive) Deactivate();
            IsActive = true;
            lastGeneration = channel.Generation;
        }

        /// <summary>
        /// Stops routing requests: hosting stopped, so every player is gone, and every object the host made for one ends with
        /// that player's scope.
        /// </summary>
        internal void Deactivate()
        {
            if (!IsActive) return;
            IsActive = false;
            proxyScratch.Clear();
            proxyScratch.AddRange(proxies);
            foreach (var proxy in proxyScratch) CleanUp(proxy);
            proxyScratch.Clear();
            ReleaseOwnLoads();
            limiter = new PlayerRequestLimiter(PermissionPolicy.Default);
        }

        /// <summary>
        /// Frame step 2, after the players' messages: departures are noticed and their grace counted, builds move on, and the
        /// verdicts owed are sent, ahead of this frame's announcements. Nothing allocates while nobody asks anything.
        /// </summary>
        internal void Pump()
        {
            if (!IsActive) return;
            var now = clock();
            CheckDepartures(now);
            AdvanceBuilds(now);
            ReleaseIdleLoads(now);
            FlushVerdicts();
        }

        bool IWorldMessageHandler.Handle(byte type, ParticipantId sender, WorldWireReader reader)
        {
            if (!IsActive || channel.GetGeneration(sender) == 0) return false;
            switch (type)
            {
                case AuthorityMessages.SpawnRequest:
                    counters.SpawnRequests++;
                    if (!AuthorityWire.TryReadSpawnRequest(reader, request)) return Malformed();
                    Spawn(sender, request);
                    return true;
                case AuthorityMessages.TransformRequest:
                    counters.TransformRequests++;
                    if (!AuthorityWire.TryReadTransformRequest(reader, out var moved, out var scale, out var pose)) return Malformed();
                    Move(sender, moved, scale, pose);
                    return true;
                case AuthorityMessages.DestroyRequest:
                    counters.DestroyRequests++;
                    if (!AuthorityWire.TryReadDestroyRequest(reader, out var destroyed)) return Malformed();
                    Destroy(sender, destroyed);
                    return true;
                default:
                    return false;
            }
        }

        private bool Malformed()
        {
            counters.MalformedRequests++;
            return false;
        }

        /// <summary>
        /// A player asks for a spawn. Every request spends from the player's spawn budget first, so one that floods is
        /// throttled whatever it asks for; then the rules and the quota decide, and only then the mod and its asset or kind.
        /// A request again for a provisional id the host already tracks, as after a restarted stream, is answered with the
        /// verdict it has, never made twice.
        /// </summary>
        private void Spawn(ParticipantId sender, AuthoritySpawnRequest asked)
        {
            if (asked.FromInputFrame) counters.SpawnsFromInputFrames++;
            var proxy = ProxyFor(sender);
            if (proxy == null)
            {
                RefuseWithoutScope(sender, asked.Provisional);
                return;
            }

            var now = clock();
            if (proxy.Spawns.TryGetValue(asked.Provisional, out var known))
            {
                if (limiter.Evaluate(proxy.Slot, false, NetRequestKind.Spawn, NetTargetRelation.None, 0, now) != NetPermission.Allowed) return;
                if (known.State == ProxySpawnState.Live && known.Counts)
                {
                    Owe(proxy, new ProxyVerdict(known.Provisional, known.Id, SpawnRefusal.None));
                }
                else if (known.State == ProxySpawnState.Live)
                {
                    // Made and gone since: the player hears that it is gone.
                    proxy.Spawns.Remove(known.Provisional);
                    Refuse(proxy, known.Provisional, SpawnRefusal.Unbuildable);
                }

                // One still being made is answered when it is.
                return;
            }

            IInternalSpawnHandler? handler = null;
            string? bundlePath = null;
            string? assetName = null;
            var refusal = SpawnAuthorityRules.RefusalOf(limiter.Evaluate(proxy.Slot, false, NetRequestKind.Spawn, NetTargetRelation.None, proxy.Counted, now));
            if (refusal == SpawnRefusal.None) refusal = CheckSpawn(asked, out handler, out bundlePath, out assetName);

            if (refusal == SpawnRefusal.None && proxy.Spawns.Count >= ProxyParticipant.MaximumSpawns) proxy.ForgetEnded();
            if (refusal == SpawnRefusal.None && proxy.Spawns.Count >= ProxyParticipant.MaximumSpawns) refusal = SpawnRefusal.HostBusy;
            if (refusal != SpawnRefusal.None)
            {
                Refuse(proxy, asked.Provisional, refusal);
                return;
            }

            var spawn = new ProxySpawn(asked.Provisional, asked.ModId, asked.Kind, asked.Payload, ToTransform(asked.Pose, asked.Scale), asked.Source, asked.FromInputFrame, now)
            {
                Handler = handler,
                BundlePath = bundlePath,
                AssetName = assetName
            };
            proxy.Spawns[asked.Provisional] = spawn;
            if (handler != null) MakeWithHandler(proxy, spawn);
            else AdvanceBuild(proxy, spawn, now);
        }

        /// <summary>
        /// What the host may make for players: an asset of a mod it shares, named by a plain path inside the mod's package,
        /// or a provider kind of that mod, which the mod's own spawn handler makes. Anything else is refused before a file is
        /// touched or any mod code runs.
        /// </summary>
        private SpawnRefusal CheckSpawn(AuthoritySpawnRequest asked, out IInternalSpawnHandler? handler, out string? bundlePath, out string? assetName)
        {
            handler = null;
            bundlePath = null;
            assetName = null;
            if (!IsShared(asked.ModId)) return SpawnRefusal.NotShared;
            if (string.Equals(asked.Kind, MultiplayerSpawnKinds.AssetPrefab, StringComparison.Ordinal))
            {
                var asset = WorldEntryCodec.DecodeAsset(asked.Payload);
                if (!asset.IsPortable || !SpawnAuthorityRules.IsSafeBundlePath(asset.BundlePath)) return SpawnRefusal.NotPermitted;
                bundlePath = asset.BundlePath;
                assetName = asset.AssetName;
                return SpawnRefusal.None;
            }

            // The runtime's own kinds are made by the runtime alone; a provider's, by the handler of the mod that owns it.
            if (MultiplayerSpawnKinds.IsReserved(asked.Kind)) return SpawnRefusal.NotPermitted;
            if (!replication.TryGetSpawnHandler(asked.Kind, out var found, out var owner) ||
                !string.Equals(owner, asked.ModId, StringComparison.OrdinalIgnoreCase))
            {
                return SpawnRefusal.NotShared;
            }

            handler = found;
            return SpawnRefusal.None;
        }

        private bool IsShared(string modId)
        {
            try
            {
                return IsSharedMod(modId);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Makes a provider kind through its mod's spawn handler, inside the player's forwarded intent, so the provider reads
        /// whose object it makes from <see cref="IRuntimeReplication.CurrentIntent"/>. A participant context's own code carries
        /// no intent to forward; the object is the player's all the same.
        /// </summary>
        private void MakeWithHandler(ProxyParticipant proxy, ProxySpawn spawn)
        {
            OperationResult<IEntity>? made;
            var participant = proxy.Participant.Value;
            var scope = IntentRules.IsPlayerIntent(spawn.Source) && IntentFlowRules.IsParticipantId(participant)
                ? intents.EnterForwarded(spawn.ModId, spawn.Source, participant, spawn.FromInputFrame)
                : default;
            try
            {
                var handler = spawn.Handler!;
                var descriptor = new InternalSpawnDescriptor(spawn.Kind, spawn.Transform, spawn.Payload);
                made = replication.MakeForPlayer(() => handler.Spawn(descriptor));
            }
            catch (Exception)
            {
                made = null;
            }
            finally
            {
                scope.Dispose();
            }

            if (made == null || !made.TryGetValue(out var entity) || entity == null || !entity.IsAlive)
            {
                Fail(proxy, spawn);
                return;
            }

            Adopt(proxy, spawn, entity, null);
        }

        /// <summary>
        /// Records what the host made for a player as canonical and that player's: announced to everyone under the player's
        /// slot, with no shadow key, and owed to the player as an adoption of its provisional id.
        /// </summary>
        private void Adopt(ProxyParticipant proxy, ProxySpawn spawn, IEntity entity, ISpawnedEntity? spawned)
        {
            spawn.Entity = entity;
            spawn.Spawned = spawned;
            object description = spawn.Handler == null
                ? MultiplayerAssetDescriptor.Create(spawn.BundlePath, spawn.AssetName)
                : new InternalSpawnDescriptor(spawn.Kind, spawn.Transform, spawn.Payload);
            MultiplayerSpawnRecord? record;
            try
            {
                record = ledger.Record(entity, new MultiplayerSpawnDescription(spawn.ModId, new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.Participant),
                    MultiplayerSpawnSource.SdkSpawn, spawn.Kind, null, spawn.Transform, description), proxy, 0, proxy.Slot);
            }
            catch (Exception)
            {
                record = null;
            }

            if (record == null || !host.TryGetId(record, out var id))
            {
                // Made but not announced: what was made ends here, so nothing stands on the host that no player sees.
                if (record != null) ledger.Remove(record);
                spawn.State = ProxySpawnState.Live;
                End(spawn);
                Fail(proxy, spawn);
                return;
            }

            spawn.Record = record;
            spawn.Id = id;
            spawn.State = ProxySpawnState.Live;
            counters.SpawnsAdopted++;
            Owe(proxy, new ProxyVerdict(spawn.Provisional, id, SpawnRefusal.None));
        }

        private void Fail(ProxyParticipant proxy, ProxySpawn spawn)
        {
            spawn.State = ProxySpawnState.Ended;
            proxy.Spawns.Remove(spawn.Provisional);
            Refuse(proxy, spawn.Provisional, SpawnRefusal.Unbuildable);
        }

        private void Refuse(ProxyParticipant proxy, uint provisional, SpawnRefusal refusal)
        {
            counters.SpawnsRefused++;
            Owe(proxy, new ProxyVerdict(provisional, NetworkEntityId.None, refusal));
        }

        /// <summary>A request from a player the host has no slot for: answered once if its window has room, and forgotten.</summary>
        private void RefuseWithoutScope(ParticipantId sender, uint provisional)
        {
            counters.SpawnsRefused++;
            AuthorityWire.WriteRefused(verdict, provisional, SpawnRefusal.HostBusy);
            if (!channel.TrySendTo(sender, verdict, verdict.Length)) counters.VerdictsDropped++;
        }

        private void Owe(ProxyParticipant proxy, ProxyVerdict owed)
        {
            if (!proxy.Owe(owed)) counters.VerdictsDropped++;
        }

        /// <summary>Sends each player the verdicts it is owed, oldest first, as far as its window has room.</summary>
        private void FlushVerdicts()
        {
            for (var index = 0; index < proxies.Count; index++)
            {
                var proxy = proxies[index];
                while (proxy.Verdicts.Count > 0)
                {
                    var owed = proxy.Verdicts.Peek();
                    if (owed.Adopted.IsValid) AuthorityWire.WriteAdopted(verdict, owed.Provisional, owed.Adopted);
                    else AuthorityWire.WriteRefused(verdict, owed.Provisional, owed.Refusal);
                    if (!channel.TrySendTo(proxy.Participant, verdict, verdict.Length)) break;
                    proxy.Verdicts.Dequeue();
                }
            }
        }

        /// <summary>The player's scope, made the first time it asks for a spawn while a slot is free; null when none is.</summary>
        private ProxyParticipant? ProxyFor(ParticipantId sender)
        {
            if (byParticipant.TryGetValue(sender, out var proxy)) return proxy;
            for (var slot = 1; slot < slots.Length; slot++)
            {
                if (slots[slot] != null) continue;
                proxy = new ProxyParticipant(sender, slot);
                slots[slot] = proxy;
                byParticipant.Add(sender, proxy);
                proxies.Add(proxy);
                return proxy;
            }

            return null;
        }

        /// <summary>
        /// A player's stream ended: it left, or its link dropped. Its scope keeps everything for
        /// <see cref="SpawnAuthorityRules.DepartureGraceMicros"/>, the time the session keeps its place, so a player who comes
        /// back finds its objects; past it, the scope is cleaned up.
        /// </summary>
        private void CheckDepartures(long now)
        {
            if (proxies.Count == 0) return;
            var generation = channel.Generation;
            if (generation != lastGeneration)
            {
                lastGeneration = generation;
                foreach (var proxy in proxies)
                {
                    var present = channel.GetGeneration(proxy.Participant) != 0;
                    if (!present && !proxy.IsDeparted) proxy.DepartedMicros = now;
                    else if (present && proxy.IsDeparted) proxy.DepartedMicros = long.MinValue;
                }
            }

            proxyScratch.Clear();
            foreach (var proxy in proxies)
            {
                if (proxy.IsDeparted && now - proxy.DepartedMicros >= SpawnAuthorityRules.DepartureGraceMicros) proxyScratch.Add(proxy);
            }

            foreach (var proxy in proxyScratch) CleanUp(proxy);
            proxyScratch.Clear();
        }

        /// <summary>
        /// Cleans up a player's scope: every object the host made or was making for that player ends, its records with it,
        /// and its slot and budgets are forgotten before anyone else may take the slot. Nothing another scope owns, nor
        /// anything a mod made itself, is touched.
        /// </summary>
        private void CleanUp(ProxyParticipant proxy)
        {
            ParticipantReleased?.Invoke(proxy.Slot);
            spawnScratch.Clear();
            spawnScratch.AddRange(proxy.Spawns.Values);
            foreach (var spawn in spawnScratch)
            {
                if (spawn.Counts) counters.PlayerObjectsEnded++;
                End(spawn);
            }

            spawnScratch.Clear();
            proxy.Spawns.Clear();
            proxy.Verdicts.Clear();
            ledger.ReleaseOwner(proxy);
            limiter.Forget(proxy.Slot);
            slots[proxy.Slot] = null;
            byParticipant.Remove(proxy.Participant);
            proxies.Remove(proxy);
            counters.PlayersCleanedUp++;
        }

        /// <summary>Ends one of a player's objects: an asset spawn is disposed, a provider's goes through its kind's handler.</summary>
        private void End(ProxySpawn spawn)
        {
            var wasLive = spawn.State == ProxySpawnState.Live;
            spawn.State = ProxySpawnState.Ended;
            spawn.Prefab = null;
            spawn.Assets = null;
            if (!wasLive) return;
            try
            {
                if (spawn.Spawned != null) spawn.Spawned.Dispose();
                else if (spawn.Entity != null) spawn.Handler?.Despawn(spawn.Entity);
            }
            catch (Exception)
            {
                // Its record ends regardless, and the entity goes with its mod's lifetime.
            }
        }

        private static TransformState ToTransform(in NetPose pose, NetVector3 scale) =>
            new TransformState(
                new Vec3(pose.Position.X, pose.Position.Y, pose.Position.Z),
                new Quat(pose.Rotation.X, pose.Rotation.Y, pose.Rotation.Z, pose.Rotation.W),
                new Vec3(scale.X, scale.Y, scale.Z));
    }
}
