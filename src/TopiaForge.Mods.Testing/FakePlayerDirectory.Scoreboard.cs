using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Testing
{
    public sealed partial class FakePlayerDirectory
    {
        private readonly Dictionary<string, Column> columns = new Dictionary<string, Column>(StringComparer.Ordinal);
        private OperationResult<IScoreboardColumn> BackendColumn(ScoreboardColumnDefinition definition)
        {
            if (!IsAuthoritative) return Refused<IScoreboardColumn>();
            if (columns.ContainsKey(definition.Id)) return OperationResult<IScoreboardColumn>.Failure(ModErrorCode.Conflict, "The column is already registered.");
            var column = new Column(this, definition.Id); columns.Add(definition.Id, column);
            return OperationResult<IScoreboardColumn>.Success(column);
        }
        /// <summary>Reads a value an author wrote, including its numeric sort key.</summary>
        public bool TryGetScore(string columnId, ParticipantId player, out string displayValue, out long sortKey)
        {
            displayValue = string.Empty; sortKey = 0;
            if (!columns.TryGetValue(columnId, out var column) || !column.Values.TryGetValue(player, out var value) || !value.Player.IsPresent) return false;
            displayValue = value.Display; sortKey = value.Sort; return true;
        }
        private sealed class Column : IScoreboardColumn
        {
            private readonly FakePlayerDirectory directory;
            internal readonly Dictionary<ParticipantId, Value> Values = new Dictionary<ParticipantId, Value>();
            internal Column(FakePlayerDirectory directory, string id) { this.directory = directory; Id = id; }
            public string Id { get; }
            public bool IsActive { get; private set; } = true;
            public OperationResult<bool> SetValue(ParticipantId player, string displayValue, long sortKey = 0)
            {
                if (!directory.IsAuthoritative) return Refused<bool>();
                if (!IsActive || !directory.TryGet(player, out var target)) return OperationResult<bool>.Failure(ModErrorCode.NotFound, "The player or column is absent.");
                if (displayValue == null || displayValue.Length > 32) return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "A scoreboard value is at most 32 characters.");
                Values[player] = new Value(target!, displayValue, sortKey); return OperationResult<bool>.Success(true);
            }
            public OperationResult<bool> Clear(ParticipantId player)
            {
                if (!directory.IsAuthoritative) return Refused<bool>();
                if (!IsActive) return OperationResult<bool>.Failure(ModErrorCode.NotFound, "The column is absent.");
                Values.Remove(player); return OperationResult<bool>.Success(true);
            }
            public void Dispose() { if (!IsActive) return; IsActive = false; directory.columns.Remove(Id); Values.Clear(); }
        }
        private sealed class Value
        {
            internal readonly IPlayer Player;
            internal readonly string Display;
            internal readonly long Sort;
            internal Value(IPlayer player, string display, long sort) { Player = player; Display = display; Sort = sort; }
        }
    }
}
