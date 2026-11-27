using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.CreatorTools.Shared
{
    internal sealed partial class CreatorWorkbench
    {
        private const int MaximumHistory = 256;

        private sealed class HistoryEntry
        {
            public HistoryEntry(string description, Func<OperationResult<string>> undo)
            {
                Description = description;
                Undo = undo;
            }

            public string Description { get; }
            public Func<OperationResult<string>> Undo { get; }
        }

        private readonly List<HistoryEntry> history = new List<HistoryEntry>();
        private bool replayingHistory;

        private void PushHistory(string description, Func<OperationResult<string>> undo)
        {
            if (replayingHistory) return;
            if (history.Count == MaximumHistory) history.RemoveAt(0);
            history.Add(new HistoryEntry(description, undo));
        }

        private void RecordSpawn(CreatorRosterEntry entry)
        {
            PushHistory("spawn " + entry.DisplayName, () =>
            {
                var live = FindRoster(entry.Id);
                if (live == null) return OperationResult<string>.Success(entry.DisplayName + " was already removed.");
                Despawn(live);
                live.Dispose();
                roster.Remove(live);
                if (selectedRosterId == live.Id) selectedRosterId = string.Empty;
                return OperationResult<string>.Success("Undid spawn of " + entry.DisplayName + ".");
            });
        }

        private void RecordTransform(CreatorRosterEntry entry, TransformState previous)
        {
            PushHistory("transform " + entry.DisplayName, () =>
            {
                var live = FindRoster(entry.Id);
                if (live == null) return OperationResult<string>.Failure(ModErrorCode.NotFound, "The transformed target no longer exists.");
                var result = SetTransform(live, previous, recordHistory: false);
                return result.Succeeded
                    ? OperationResult<string>.Success("Restored " + entry.DisplayName + " transform.")
                    : OperationResult<string>.Failure(result.ErrorCode, result.ErrorMessage);
            });
        }

        private void RecordNativeHidden(CreatorRosterEntry entry)
        {
            PushHistory("show " + entry.DisplayName, () =>
            {
                var live = FindRoster(entry.Id);
                if (live?.NativeEdit == null)
                {
                    return OperationResult<string>.Failure(ModErrorCode.NotFound, "The borrowed native target is no longer available.");
                }
                var shown = live.NativeEdit.SetTemporarilyHidden(false);
                if (shown.Succeeded) live.NativeHidden = false;
                return shown.Succeeded
                    ? OperationResult<string>.Success("Restored visibility for " + live.DisplayName + ".")
                    : OperationResult<string>.Failure(shown.ErrorCode, shown.ErrorMessage);
            });
        }

        private void RecordDespawn(CreatorRosterEntry entry, TransformState transform, string projectId = "")
        {
            var source = entry.SourceId;
            var originalId = entry.Id;
            var name = entry.DisplayName;
            var kind = entry.Kind;
            var wasRobot = entry.Robot != null;
            PushHistory("despawn " + name, () =>
            {
                var catalogEntry = new CreatorCatalogEntry(
                    (wasRobot ? "robotkit:" : "content:") + source,
                    name,
                    string.Empty,
                    kind);
                var result = Spawn(catalogEntry, transform);
                if (result.Succeeded && SelectedRoster() is { } restored)
                {
                    restored.Id = originalId;
                    selectedRosterId = originalId;
                    if (!string.IsNullOrEmpty(projectId))
                    {
                        projectEntities[projectId] = originalId;
                        var interactions = RegisterProjectInteractionsFor(projectId);
                        if (!interactions.Succeeded)
                        {
                            return OperationResult<string>.Failure(interactions.ErrorCode, interactions.ErrorMessage);
                        }
                    }
                }
                return result.Succeeded
                    ? OperationResult<string>.Success("Restored " + name + ".")
                    : result;
            });
        }

        private OperationResult<string> UndoHistory()
        {
            if (history.Count == 0)
            {
                return OperationResult<string>.Failure(ModErrorCode.NotFound, "There is no creator operation to undo.");
            }
            var index = history.Count - 1;
            var entry = history[index];
            history.RemoveAt(index);
            replayingHistory = true;
            try
            {
                return entry.Undo();
            }
            finally
            {
                replayingHistory = false;
            }
        }

        private void ClearHistory()
        {
            history.Clear();
            replayingHistory = false;
        }
    }
}
