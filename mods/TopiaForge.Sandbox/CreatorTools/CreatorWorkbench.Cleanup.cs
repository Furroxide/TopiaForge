using System;
using System.Linq;
using TopiaForge.Mods;

namespace TopiaForge.CreatorTools.Shared
{
    internal sealed partial class CreatorWorkbench
    {
        public OperationResult<bool> EndSession()
        {
            if (endingSession) return OperationResult<bool>.Success(false);
            if (creatorSession == null && roster.Count == 0 && runner == null
                && mutationLease == null && controlLease == null && graphAudio.Count == 0 && confirmation == null)
                return OperationResult<bool>.Success(false);
            endingSession = true;
            sessionGeneration++;
            var result = OperationResult<bool>.Success(true);
            try
            {
                Clean(() => EndConversation());
                Clean(DisposeGraphAudio);
                var oldRunner = runner;
                runner = null;
                Clean(() => oldRunner?.Dispose());
                Clean(() => DisposeProjectInteractions());
                activeProject = null;
                projectEntities.Clear();
                projectBindings.Clear();
                confirmedNativeProjectId = string.Empty;
                Clean(DisposeConfirmation);
                Clean(() => window?.Hide());
                var controls = controlLease;
                controlLease = null;
                Clean(() => controls?.Dispose());
                var entries = roster.ToArray();
                roster.Clear();
                for (var index = entries.Length - 1; index >= 0; index--)
                    Record(entries[index].RestoreAndDispose());
                ClearHistory();
                var session = creatorSession;
                creatorSession = null;
                Clean(() => session?.Dispose());
                var isolation = mutationLease;
                mutationLease = null;
                Clean(() => isolation?.Dispose());
                var playerTarget = playerTargetRegistration;
                playerTargetRegistration = null;
                Clean(() => playerTarget?.Dispose());
                selectedRosterId = string.Empty;
                status = result.Succeeded ? "Session ended; temporary edits restored."
                    : "Session ended with restoration warnings: " + result.ErrorMessage;
                if (!result.Succeeded)
                {
                    context.Logger.Warn(status);
                    context.Ui.ShowToast(status, result.ErrorCode == ModErrorCode.Conflict ? UiTone.Warning : UiTone.Danger);
                }
                RefreshUi();
                RefreshHud(force: true);
                return result;
            }
            finally { endingSession = false; }

            void Clean(Action cleanup)
            {
                try { cleanup(); }
                catch (Exception exception) { Record(OperationResult<bool>.Failure(ModErrorCode.External, exception.Message)); }
            }
            void Record(OperationResult<bool> cleanup)
            {
                if (cleanup.Succeeded) return;
                result = result.Succeeded ? cleanup : OperationResult<bool>.Failure(
                    result.ErrorCode == ModErrorCode.Conflict ? cleanup.ErrorCode : result.ErrorCode,
                    result.ErrorMessage + " " + cleanup.ErrorMessage);
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            EndSession();
            disposed = true;
            updateSubscription.Dispose();
            DisposeConfirmation();
            window?.Dispose();
            hud?.Dispose();
            confirmation = null;
            window = null;
            hud = null;
        }

        private OperationResult<bool> RetireRosterEntry(CreatorRosterEntry entry, bool despawn, bool fireRemoved = true)
        {
            var projectId = ProjectTargetIdForRoster(entry.Id);
            if (!roster.Remove(entry)) return OperationResult<bool>.Success(false);
            var owningRunner = runner;
            if (!string.IsNullOrEmpty(projectId))
            {
                projectEntities.Remove(projectId);
                if (projectBindings.Remove(projectId)) confirmedNativeProjectId = string.Empty;
            }
            if (selectedRosterId == entry.Id) selectedRosterId = string.Empty;
            var result = OperationResult<bool>.Success(true);
            void Attempt(Action action)
            {
                try { action(); }
                catch (Exception exception) { result = MergeCleanup(result, OperationResult<bool>.Failure(ModErrorCode.External, exception.Message)); }
            }
            if (!string.IsNullOrEmpty(projectId)) Attempt(() => DisposeProjectInteractions(projectId));
            if (despawn) Attempt(() => result = MergeCleanup(result,
                entry.Robot?.Despawn() ?? entry.Spawn?.Despawn() ?? OperationResult<bool>.Success(false)));
            result = MergeCleanup(result, entry.RestoreAndDispose());
            if (fireRemoved && !string.IsNullOrEmpty(projectId) && ReferenceEquals(runner, owningRunner))
                Attempt(() => owningRunner?.Fire(CreatorGraphNodeKind.EntityRemoved, projectId));
            return result;
        }

        private static OperationResult<bool> MergeCleanup(OperationResult<bool> current, OperationResult<bool> next)
        {
            if (next.Succeeded) return current;
            if (current.Succeeded) return next;
            return OperationResult<bool>.Failure(current.ErrorCode == ModErrorCode.Conflict ? next.ErrorCode : current.ErrorCode,
                current.ErrorMessage + " " + next.ErrorMessage);
        }

        private void ReportCleanupFailure(string prefix, OperationResult<bool> result)
        {
            if (result.Succeeded) return;
            status = prefix + ": " + result.ErrorMessage;
            context.Logger.Warn(status);
            context.Ui.ShowToast(status, result.ErrorCode == ModErrorCode.Conflict ? UiTone.Warning : UiTone.Danger);
        }
    }
}
