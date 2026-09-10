using System;
using System.Linq;
using TopiaForge.Mods;

namespace TopiaForge.CreatorTools.Shared
{
    internal sealed partial class CreatorWorkbench
    {
        private OperationResult<bool> RemoveOwnedEntry(CreatorRosterEntry entry, bool fireRemoved = true)
        {
            var projectId = ProjectIdForRoster(entry.Id);
            if (!roster.Remove(entry)) return OperationResult<bool>.Success(false);
            var owningRunner = runner;
            if (!string.IsNullOrEmpty(projectId)) projectEntities.Remove(projectId);
            if (selectedRosterId == entry.Id) selectedRosterId = string.Empty;
            var result = OperationResult<bool>.Success(true);
            void Clean(Action action) { var attempted = TryCleanup(action); result = MergeCleanup(result, attempted); }
            if (!string.IsNullOrEmpty(projectId)) Clean(() => DisposeProjectInteractions(projectId));
            // A catalog robot may expose both handles; the creator handle owns source cleanup.
            Clean(() => result = MergeCleanup(result,
                entry.Spawn?.Despawn() ?? entry.Robot?.Despawn() ?? OperationResult<bool>.Success(false)));
            Clean(entry.Dispose);
            if (result.Succeeded && fireRemoved && !string.IsNullOrEmpty(projectId) && ReferenceEquals(runner, owningRunner))
                owningRunner?.Fire(CreatorGraphNodeKind.EntityRemoved, projectId);
            return result;
        }

        private static OperationResult<bool> TryCleanup(Action action)
        {
            try { action(); return OperationResult<bool>.Success(true); }
            catch (Exception exception) { return OperationResult<bool>.Failure(ModErrorCode.External, exception.Message); }
        }

        private static OperationResult<bool> MergeCleanup(OperationResult<bool> first, OperationResult<bool> next)
        {
            if (next.Succeeded) return first;
            if (first.Succeeded) return next;
            return OperationResult<bool>.Failure(first.ErrorCode, first.ErrorMessage + " " + next.ErrorMessage);
        }

        private static OperationResult<string> WithCleanupFailure(ModErrorCode error, string message, OperationResult<bool> cleanup) =>
            OperationResult<string>.Failure(error, message + (cleanup.Succeeded ? string.Empty : " Cleanup: " + cleanup.ErrorMessage));

        public OperationResult<bool> EndSession()
        {
            if (creatorSession == null && roster.Count == 0 && runner == null
                && mutationLease == null && controlLease == null && graphAudio.Count == 0)
                return OperationResult<bool>.Success(false);
            var result = OperationResult<bool>.Success(true);
            void Clean(Action action) { var attempted = TryCleanup(action); result = MergeCleanup(result, attempted); }
            Clean(() => EndConversation());
            Clean(DisposeGraphAudio);
            var stoppedRunner = runner;
            runner = null;
            Clean(() => stoppedRunner?.Dispose());
            Clean(() => DisposeProjectInteractions());
            activeProject = null;
            projectEntities.Clear();
            projectBindings.Clear();
            confirmedNativeProjectId = string.Empty;
            Clean(() => confirmation?.Dispose());
            confirmation = null;
            Clean(() => window?.Hide());
            Clean(ReleaseControl);
            foreach (var entry in roster.AsEnumerable().Reverse().ToArray())
            {
                if (entry.Owned) result = MergeCleanup(result, RemoveOwnedEntry(entry, fireRemoved: false));
                else { roster.Remove(entry); Clean(entry.Dispose); }
            }
            ClearHistory();
            Clean(() => creatorSession?.Dispose());
            creatorSession = null;
            Clean(() => mutationLease?.Dispose());
            mutationLease = null;
            Clean(() => playerTargetRegistration?.Dispose());
            playerTargetRegistration = null;
            selectedRosterId = string.Empty;
            status = result.Succeeded ? "Session ended; temporary edits restored."
                : "Session ended with cleanup problems: " + result.ErrorMessage;
            if (!result.Succeeded) context.Ui.ShowToast(status, UiTone.Danger);
            RefreshUi();
            RefreshHud(force: true);
            return result;
        }
    }
}
