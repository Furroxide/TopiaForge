using System;
using System.Collections.Generic;
using System.Text;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal sealed partial class MultiplayerPlayers
    {
        private readonly List<PlayerScoreColumn> scoreColumns = new List<PlayerScoreColumn>();
        private readonly List<ScoreboardData> scoreboard = new List<ScoreboardData>();
        private readonly Dictionary<ParticipantId, ScoreDelivery> scoreDeliveries = new Dictionary<ParticipantId, ScoreDelivery>();
        private ulong scoreVersion;
        private int scoreBytes = 4;
        private const int MaximumScoreBytes = 28 * 1024;
        internal IReadOnlyList<ScoreboardData> Scoreboard => scoreboard;

        internal OperationResult<IScoreboardColumn> RegisterColumn(PlayerContext context, ScoreboardColumnDefinition definition)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (!IsHost) return Authority<IScoreboardColumn>();
            if (scoreColumns.Count >= 64) return Limit<IScoreboardColumn>();
            for (var i = 0; i < scoreColumns.Count; i++)
                if (scoreColumns[i].Context.Mod == context.Mod && scoreColumns[i].Id == definition.Id)
                    return OperationResult<IScoreboardColumn>.Failure(ModErrorCode.Conflict, "This mod already registered that scoreboard column.");
            var column = new PlayerScoreColumn(this, context, definition);
            if (scoreBytes + column.WireBytes > MaximumScoreBytes) return Limit<IScoreboardColumn>();
            scoreBytes += column.WireBytes;
            scoreColumns.Add(column); scoreboard.Add(column.Data); scoreVersion++;
            context.Lifetime.Track(column);
            return OperationResult<IScoreboardColumn>.Success(column);
        }

        private void SendScores()
        {
            var snapshot = Snapshot;
            if (!IsHost || Channel == null || snapshot == null) return;
            for (var c = scoreColumns.Count - 1; c >= 0; c--) scoreColumns[c].Prune();
            for (var p = 0; p < snapshot.Participants.Count; p++)
            {
                var participant = snapshot.Participants[p];
                if (participant.IsLocal) continue;
                var generation = Channel.GetGeneration(participant.Id);
                if (generation == 0) continue;
                if (!scoreDeliveries.TryGetValue(participant.Id, out var delivery)) scoreDeliveries.Add(participant.Id, delivery = new ScoreDelivery());
                if (delivery.Generation == generation && delivery.Version == scoreVersion) continue;
                // One atomic bounded baseline, including an empty list, removes retired columns on every client.
                Begin(20, ""); Writer.WriteVarUInt((ulong)scoreboard.Count);
                for (var c = 0; c < scoreboard.Count; c++)
                {
                    var column = scoreboard[c];
                    Writer.WriteString(column.Mod); Writer.WriteString(column.Definition.Id); Writer.WriteString(column.Definition.Title);
                    Writer.WriteUInt64(unchecked((ulong)(long)column.Definition.Order)); Writer.WriteVarUInt((ulong)column.Values.Count);
                    foreach (var value in column.Values)
                    { Writer.WriteString(value.Key.Value); Writer.WriteString(value.Value.Display); Writer.WriteUInt64(unchecked((ulong)value.Value.SortKey)); }
                }
                if (Send(participant.Id).Succeeded) { delivery.Generation = generation; delivery.Version = scoreVersion; }
            }
        }

        private bool ReceiveColumn(PlayerContext? context, ParticipantId sender, WorldWireReader r)
        {
            if (!FromHost(sender) || !r.TryReadInt32(64, out var count)) return false;
            var columns = new List<ScoreboardData>(count);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < count; i++)
            {
                var mod = UiNodeWireCodec.Text(r, 128); var id = UiNodeWireCodec.Text(r, 64); var title = UiNodeWireCodec.Text(r, 64);
                if (!keys.Add(mod + "/" + id) || !r.TryReadUInt64(out var order) || unchecked((long)order) < int.MinValue || unchecked((long)order) > int.MaxValue || !r.TryReadInt32(32, out var values)) return false;
                var data = new ScoreboardData(mod, new ScoreboardColumnDefinition(id, title, unchecked((int)order)));
                for (var v = 0; v < values; v++)
                {
                    var player = new ParticipantId(UiNodeWireCodec.Text(r, 128)); var display = UiNodeWireCodec.Text(r, 32);
                    if (!r.TryReadUInt64(out var sort) || data.Values.ContainsKey(player)) return false;
                    if (Present(player)) data.Values.Add(player, new ScoreboardValue(display, unchecked((long)sort)));
                }
                columns.Add(data);
            }
            if (!r.AtEnd) return false;
            scoreboard.Clear(); scoreboard.AddRange(columns);
            return true;
        }

        private sealed class ScoreDelivery { internal ulong Generation, Version; }
        private static int TextBytes(string value)
        {
            var bytes = Encoding.UTF8.GetByteCount(value);
            return bytes + (bytes < 128 ? 1 : bytes < 16384 ? 2 : 3);
        }

        private sealed class PlayerScoreColumn : IScoreboardColumn
        {
            private readonly MultiplayerPlayers runtime;
            internal readonly PlayerContext Context;
            internal readonly ScoreboardData Data;
            internal int WireBytes;
            private readonly Dictionary<ParticipantId, IPlayer> targets = new Dictionary<ParticipantId, IPlayer>();
            private readonly List<ParticipantId> removed = new List<ParticipantId>();
            internal PlayerScoreColumn(MultiplayerPlayers runtime, PlayerContext context, ScoreboardColumnDefinition definition)
            {
                this.runtime = runtime; Context = context; Data = new ScoreboardData(context.Mod, definition);
                WireBytes = TextBytes(context.Mod) + TextBytes(definition.Id) + TextBytes(definition.Title) + 9;
            }
            public string Id => Data.Definition.Id;
            public bool IsActive { get; private set; } = true;
            public OperationResult<bool> SetValue(ParticipantId player, string displayValue, long sortKey = 0)
            {
                UnityMainThreadGuard.AssertCurrent();
                if (!runtime.IsHost) return Authority<bool>();
                if (!IsActive || Context.Lifetime.IsStopping || !runtime.Present(player)) return Gone<bool>();
                if (displayValue == null || displayValue.Length > 32) return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "A scoreboard value is at most 32 characters.");
                var existed = Data.Values.TryGetValue(player, out var old);
                if (existed && old.Display == displayValue && old.SortKey == sortKey) return Success;
                var delta = TextBytes(displayValue) - (existed ? TextBytes(old.Display) : 0) + (existed ? 0 : TextBytes(player.Value) + 8);
                if (runtime.scoreBytes + delta > MaximumScoreBytes) return Limit<bool>();
                targets[player] = runtime.Target(Context, player);
                runtime.scoreBytes += delta; WireBytes += delta;
                Data.Values[player] = new ScoreboardValue(displayValue, sortKey); runtime.scoreVersion++;
                return Success;
            }
            public OperationResult<bool> Clear(ParticipantId player)
            {
                UnityMainThreadGuard.AssertCurrent();
                if (!runtime.IsHost) return Authority<bool>();
                if (!IsActive) return Gone<bool>();
                targets.Remove(player);
                if (Data.Values.TryGetValue(player, out var old))
                {
                    var bytes = TextBytes(player.Value) + TextBytes(old.Display) + 8;
                    Data.Values.Remove(player); WireBytes -= bytes; runtime.scoreBytes -= bytes; runtime.scoreVersion++;
                }
                return Success;
            }
            internal void Prune()
            {
                removed.Clear();
                foreach (var pair in targets) if (!pair.Value.IsPresent) removed.Add(pair.Key);
                for (var i = 0; i < removed.Count; i++) Clear(removed[i]);
            }
            public void Dispose()
            {
                UnityMainThreadGuard.AssertCurrent();
                if (!IsActive) return;
                IsActive = false; runtime.scoreColumns.Remove(this); runtime.scoreboard.Remove(Data); runtime.scoreBytes -= WireBytes; runtime.scoreVersion++;
            }
            private static readonly OperationResult<bool> Success = OperationResult<bool>.Success(true);
        }
    }

    internal sealed class ScoreboardData
    {
        internal readonly string Mod;
        internal readonly ScoreboardColumnDefinition Definition;
        internal readonly Dictionary<ParticipantId, ScoreboardValue> Values = new Dictionary<ParticipantId, ScoreboardValue>();
        internal ScoreboardData(string mod, ScoreboardColumnDefinition definition) { Mod = mod; Definition = definition; }
    }
    internal readonly struct ScoreboardValue
    {
        internal readonly string Display;
        internal readonly long SortKey;
        internal ScoreboardValue(string display, long sortKey) { Display = display; SortKey = sortKey; }
    }
}
