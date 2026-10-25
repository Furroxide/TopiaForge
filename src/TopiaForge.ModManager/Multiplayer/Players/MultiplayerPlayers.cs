using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>Process-owned player traffic. Sender attribution is supplied by the authenticated channel.</summary>
    internal sealed partial class MultiplayerPlayers : IMultiplayerRuntimeObserver, IMultiplayerFrameStage
    {
        internal const int MaximumMessageBytes = 32 * 1024;
        private readonly MultiplayerRuntime runtime;
        internal readonly MultiplayerPresence Presence;
        private readonly List<PlayerContext> contexts = new List<PlayerContext>();
        private readonly WorldWireReader reader = new WorldWireReader();
        internal readonly WorldWireWriter Writer = new WorldWireWriter(MaximumMessageBytes);
        internal IInternalSystemChannel? Channel;
        private ulong streamGeneration;
        internal long Rejected { get; private set; }
        internal MultiplayerSessionSnapshot? Snapshot => runtime.Control?.SessionSnapshot;
        internal bool IsHost => runtime.Role != InternalSessionRole.Client;
        internal long Now => Presence.Now();

        private MultiplayerPlayers(MultiplayerRuntime runtime, MultiplayerPresence presence)
        { this.runtime = runtime; Presence = presence; }

        internal static MultiplayerPlayers Install(MultiplayerRuntime runtime, MultiplayerPresence presence)
        {
            var players = new MultiplayerPlayers(runtime, presence);
            runtime.PlayersReceive = players;
            runtime.PlayersSend = new SendStage(players);
            runtime.AddObserver(players);
            return players;
        }

        internal PlayerContext Bind(string mod, bool networked, IModLogger logger, IModLifetime lifetime, ILocalPlayerService local, IUiService ui, ModInputActivity input)
        {
            var context = new PlayerContext(this, mod, networked, logger, lifetime, local, ui, input);
            contexts.Add(context);
            lifetime.Track(context);
            return context;
        }

        internal void Remove(PlayerContext context)
        {
            if (context.TickSource != null) { context.TickSource.Dispose(); context.TickSource = null; canonicalTickSources--; }
            contexts.Remove(context);
        }
        private PlayerContext? Find(string mod)
        {
            for (var i = 0; i < contexts.Count; i++)
                if (!contexts[i].Lifetime.IsStopping && contexts[i].Mod == mod) return contexts[i];
            return null;
        }

        public void Attached(IInternalMultiplayerControl control)
        {
            var opened = control.OpenSystemChannel(InternalSystemChannelKind.Players);
            if (opened.TryGetValue(out var channel)) Channel = channel;
            else runtime.Log.Warn("Multiplayer: player actions are unavailable; restart before hosting or joining.");
        }
        public void Detaching(IInternalMultiplayerControl control)
        {
            Channel?.Dispose();
            Channel = null;
            Reset();
        }
        public void RealmChanged(NetRealm previous, NetRealm next) => Reset();

        private void Reset()
        {
            for (var i = 0; i < contexts.Count; i++) contexts[i].Reset();
            ResetUi();
            inputBudgets.Clear(); outgoingInputBudget = null;
            playerStreams.Clear();
            scoreDeliveries.Clear();
            scoreboard.Clear();
            if (IsHost) for (var i = 0; i < scoreColumns.Count; i++) scoreboard.Add(scoreColumns[i].Data);
            scoreVersion++;
        }

        public void Run(float deltaTime)
        {
            var channel = Channel;
            if (channel == null || !channel.IsOpen) return;
            if (streamGeneration != channel.Generation)
            {
                streamGeneration = channel.Generation;
                // A host's aggregate generation also moves when an unrelated player joins.
                // Existing targets keep their UI; per-target delivery checks refresh only restarted streams.
                if (!IsHost) Reset();
            }
            AdvanceInputs();
            ReconcilePlayers();
            ReconcileUi();
            while (channel.TryReceive(out var message))
            {
                if (message.Payload.Length > MaximumMessageBytes) { Rejected++; continue; }
                reader.Reset(message.Payload);
                try
                {
                    if (!reader.TryReadHeader(out var kind) || !reader.TryReadString(128, out var mod)) { Rejected++; continue; }
                    if (kind == 20) { if (!ReceiveColumn(null, message.Sender, reader)) Rejected++; continue; }
                    var context = Find(mod);
                    if (context == null) { Rejected++; continue; }
                    var accepted = kind == 1 ? ReceiveInput(context, message.Sender, reader)
                        : kind == 2 ? ReceiveAction(context, message.Sender, reader)
                        : ReceiveUi(context, message.Sender, kind, reader);
                    if (!accepted) Rejected++;
                }
                catch (ArgumentException) { Rejected++; }
                catch (InvalidOperationException) { Rejected++; }
            }
        }

        internal bool IsLocal(ParticipantId id) => Snapshot?.LocalParticipantId?.Equals(id) == true;
        internal bool Present(ParticipantId id) => Snapshot?.TryGetParticipant(id, out _) == true;
        internal bool FromHost(ParticipantId id) => !IsHost && (Snapshot?.HostParticipantId ?? InternalSystemMessage.HeadlessHostSender).Equals(id);

        internal void Begin(byte kind, string mod) { Writer.Begin(kind); Writer.WriteString(mod); }
        internal OperationResult<bool> Send(ParticipantId target)
        {
            if (Channel == null || !Channel.IsOpen) return PlayerDirectory.Unavailable<bool>();
            var destination = IsHost ? InternalSystemTarget.Only(target) : InternalSystemTarget.Host;
            var result = Channel.Send(Writer.Segment, destination);
            return result.TryGetValue(out var delivered) && delivered > 0 ? OperationResult<bool>.Success(true)
                : OperationResult<bool>.Failure(result.Succeeded ? ModErrorCode.Unavailable : result.ErrorCode, "The player could not receive this yet; try again after reconnecting.");
        }

        private sealed class SendStage : IMultiplayerFrameStage
        {
            private readonly MultiplayerPlayers players;
            internal SendStage(MultiplayerPlayers players) { this.players = players; }
            public void Run(float deltaTime) { players.SendInputs(); players.SendScores(); }
        }
    }
}
