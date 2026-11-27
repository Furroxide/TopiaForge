using System;
using System.Linq;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.CreatorTools.Shared
{
    internal sealed partial class CreatorWorkbench
    {
        private Task<OperationResult<bool>>? projectDeleteTask;
        private string deletingProjectId = string.Empty;

        private void ConfirmDeleteSelectedProject()
        {
            if (projects == null || string.IsNullOrEmpty(selectedProjectId) || confirmation?.IsOpen == true) return;
            var summary = projectSummaries.FirstOrDefault(item => item.Id == selectedProjectId);
            if (summary == null) return;
            var shown = context.Ui.ShowModal(
                new UiModalRequest(
                    "DELETE EVENT PROJECT?",
                    "Delete '" + summary.DisplayName + "' from the local creator project library? This cannot be undone.",
                    "DELETE PROJECT",
                    destructive: true),
                confirmed =>
                {
                    confirmation = null;
                    if (!confirmed || projectDeleteTask != null) return;
                    deletingProjectId = summary.Id;
                    if (activeProject?.Id == deletingProjectId) StopProject(removeProjectEntities: true, removeProjectBindings: true);
                    projectDeleteTask = projects.DeleteAsync(deletingProjectId);
                    status = "Deleting project…";
                    RefreshUi();
                });
            shown.TryGetValue(out confirmation);
        }

        private void PollProjectDeletion()
        {
            if (projectDeleteTask?.IsCompleted != true) return;
            var result = projectDeleteTask.GetAwaiter().GetResult();
            projectDeleteTask = null;
            if (result.Succeeded)
            {
                projectSummaries = projectSummaries.Where(item => item.Id != deletingProjectId).ToArray();
                if (activeProject?.Id == deletingProjectId) activeProject = null;
                selectedProjectId = projectSummaries.FirstOrDefault()?.Id ?? string.Empty;
                selectedGraphNodeId = string.Empty;
                status = result.Value ? "Project deleted." : "Project was already absent.";
                BeginProjectList();
            }
            else
            {
                status = result.ErrorMessage;
                context.Ui.ShowToast(status, UiTone.Danger);
            }
            deletingProjectId = string.Empty;
            RefreshUi();
        }
    }
}
