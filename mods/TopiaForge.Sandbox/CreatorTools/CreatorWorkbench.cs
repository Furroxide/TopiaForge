using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.CreatorTools.Shared
{
    internal sealed partial class CreatorWorkbench : IDisposable, ICreatorEventRuntime
    {
        private readonly IModContext context;
        private readonly CreatorWorkbenchOptions options;
        private readonly ICreatorContentService content;
        private readonly IRobotAgentService robots;
        private readonly ICreatorProjectLibrary? projects;
        private readonly IRobotSceneEditorService? robotEditor;
        private readonly IRobotObjectiveService? objectives;
        private readonly IRobotConversationService? conversations;
        private readonly ICreatorMutationSafetyService? mutationSafety;
        private readonly Action requestHide;
        private readonly Action requestEnd;
        private readonly IDisposable updateSubscription;
        private readonly List<CreatorCatalogEntry> catalog = new List<CreatorCatalogEntry>();
        private readonly List<CreatorRosterEntry> roster = new List<CreatorRosterEntry>();
        private readonly Dictionary<string, string> projectEntities = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> projectBindings = new Dictionary<string, string>(StringComparer.Ordinal);
        private ICreatorSession? creatorSession;
        private ICreatorMutationLease? mutationLease;
        private IPlayerControlLease? controlLease;
        private IRobotTargetRegistration? playerTargetRegistration;
        private IUiSurface? window;
        private IUiSurface? hud;
        private IUiModal? confirmation;
        private CreatorEventGraphRunner? runner;
        private CreatorEventProject? activeProject;
        private Task<OperationResult<CreatorProjectLibrarySnapshot>>? projectListTask;
        private Task<OperationResult<CreatorEventProject>>? projectLoadTask;
        private IReadOnlyList<CreatorProjectSummary> projectSummaries = Array.Empty<CreatorProjectSummary>();
        private string selectedCatalogId = string.Empty;
        private string selectedRosterId = string.Empty;
        private string selectedProjectId = string.Empty;
        private string selectedGraphNodeId = string.Empty;
        private string confirmedNativeProjectId = string.Empty;
        private string search = string.Empty;
        private string kindFilter = "all";
        private string xText = "0";
        private string yText = "0";
        private string zText = "0";
        private string qxText = "0";
        private string qyText = "0";
        private string qzText = "0";
        private string qwText = "1";
        private string sxText = "1";
        private string syText = "1";
        private string szText = "1";
        private string personaName = "Creator persona";
        private string personaInstructions = "Be friendly, concise, and curious.";
        private string chatText = string.Empty;
        private string chatStatus = string.Empty;
        private string status = "Ready.";
        private string hudText = string.Empty;
        private Vec3 projectRunOrigin;
        private int nextOwnedNumber = 1;
        private bool disposed;

        public CreatorWorkbench(
            IModContext context,
            CreatorWorkbenchOptions options,
            ICreatorContentService content,
            IRobotAgentService robots,
            Action requestHide,
            Action requestEnd)
        {
            this.context = context ?? throw new ArgumentNullException(nameof(context));
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            this.content = content ?? throw new ArgumentNullException(nameof(content));
            this.robots = robots ?? throw new ArgumentNullException(nameof(robots));
            this.requestHide = requestHide ?? throw new ArgumentNullException(nameof(requestHide));
            this.requestEnd = requestEnd ?? throw new ArgumentNullException(nameof(requestEnd));
            context.Extensions.TryGet(out projects);
            context.Extensions.TryGet(out robotEditor);
            context.Extensions.TryGet(out objectives);
            context.Extensions.TryGet(out conversations);
            context.Extensions.TryGet(out mutationSafety);
            updateSubscription = context.Events.SubscribeUpdate(Update);
        }

        public bool IsSessionActive => creatorSession?.IsAlive == true;
        public bool IsVisible => window?.IsVisible == true;

        public OperationResult<bool> Open()
        {
            if (disposed)
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "Creator Tools is disposed.");
            }

            if (controlLease == null)
            {
                var controlled = context.LocalPlayer.AcquireControl(options.Title + " workbench");
                if (!controlled.TryGetValue(out controlLease))
                {
                    return OperationResult<bool>.Failure(controlled.ErrorCode, controlled.ErrorMessage);
                }
            }
            var started = EnsureSession();
            if (!started.Succeeded)
            {
                ReleaseControl();
                return started;
            }
            if (window == null)
            {
                var created = context.Ui.CreateSurface(new UiSurfaceRequest(
                    options.SurfaceId + "-window",
                    options.Title,
                    string.Empty,
                    UiSurfaceKind.FullscreenTool,
                    1180f,
                    820f,
                    BuildContent()));
                if (!created.TryGetValue(out window))
                {
                    ReleaseControl();
                    if (started.Value) EndSession();
                    return OperationResult<bool>.Failure(created.ErrorCode, created.ErrorMessage);
                }

                if (window is IUiSurfaceDismissalSource dismissal)
                {
                    dismissal.Dismissed += OnWindowDismissed;
                }
            }

            window.Show();
            RefreshUi();
            // started.Value is true only when this Open began a new session, so
            // a false value is an observed reopen of the surviving session.
            return OperationResult<bool>.Success(true);
        }

        public OperationResult<bool> Hide()
        {
            var changed = window?.IsVisible == true || controlLease != null;
            if (window?.IsVisible == true) window.Hide();
            ReleaseControl();
            RefreshHud(force: true);
            if (changed && creatorSession?.IsAlive == true)
            {
            }
            return OperationResult<bool>.Success(changed);
        }

        public OperationResult<bool> EndSession()
        {
            if (creatorSession == null && roster.Count == 0 && runner == null
                && mutationLease == null && controlLease == null && graphAudio.Count == 0)
            {
                return OperationResult<bool>.Success(false);
            }
            EndConversation();
            DisposeGraphAudio();
            runner?.Dispose();
            runner = null;
            DisposeProjectInteractions();
            activeProject = null;
            projectEntities.Clear();
            projectBindings.Clear();
            confirmedNativeProjectId = string.Empty;
            confirmation?.Dispose();
            confirmation = null;
            window?.Hide();
            ReleaseControl();
            for (var index = roster.Count - 1; index >= 0; index--) roster[index].Dispose();
            roster.Clear();
            ClearHistory();
            creatorSession?.Dispose();
            creatorSession = null;
            mutationLease?.Dispose();
            mutationLease = null;
            playerTargetRegistration?.Dispose();
            playerTargetRegistration = null;
            selectedRosterId = string.Empty;
            status = "Session ended; temporary edits restored.";
            RefreshUi();
            RefreshHud(force: true);
            // A cycle only counts when teardown actually left nothing retained.
            var clean = roster.Count == 0
                && creatorSession == null
                && mutationLease == null
                && controlLease == null
                && runner == null
                && activeProject == null
                && projectEntities.Count == 0
                && projectBindings.Count == 0
                && graphAudio.Count == 0;
            return OperationResult<bool>.Success(true);
        }

        public void Dispose()
        {
            if (disposed) return;
            EndSession();
            disposed = true;
            updateSubscription.Dispose();
            confirmation?.Dispose();
            window?.Dispose();
            hud?.Dispose();
            confirmation = null;
            window = null;
            hud = null;
        }

        private OperationResult<bool> EnsureSession()
        {
            if (creatorSession?.IsAlive == true) return OperationResult<bool>.Success(false);
            var started = content.BeginSession(new CreatorSessionOptions(options.Title, options.MaximumInstances));
            if (!started.TryGetValue(out creatorSession))
            {
                return OperationResult<bool>.Failure(started.ErrorCode, started.ErrorMessage);
            }

            RefreshCatalog();
            RefreshNativeRoster();
            if (objectives != null)
            {
                var player = objectives.RegisterTarget(
                    "PLAYER",
                    RobotTargetKind.Player,
                    ResolvePlayerTarget);
                player.TryGetValue(out playerTargetRegistration);
            }
            BeginProjectList();
            EnsureHud();
            status = "Creator session active. Closing the window keeps it running.";
            return OperationResult<bool>.Success(true);
        }

        private void Update(float deltaTime)
        {
            if (disposed) return;
            if (options.ProjectScope == CreatorProjectScope.Global && mutationLease != null
                && (!mutationLease.IsAlive || !mutationLease.IsPersistenceIsolated))
            {
                var problem = "Global persistence isolation was lost; temporary content and edits were restored.";
                requestEnd();
                status = problem;
                context.Ui.ShowToast(problem, UiTone.Danger);
                return;
            }
            if (creatorSession != null && !creatorSession.IsAlive)
            {
                var problem = "Creator session became unavailable; temporary content and edits were restored.";
                requestEnd();
                status = problem;
                context.Ui.ShowToast(problem, UiTone.Danger);
                return;
            }
            RefreshCatalogIfChanged();
            RemoveDeadRosterEntries();
            runner?.Update(deltaTime);
            if (runner != null && !runner.IsRunning && !string.IsNullOrEmpty(runner.LastProblem))
            {
                var problem = runner.LastProblem;
                StopProject(removeProjectEntities: true);
                status = problem + " Project-owned content and edits were rolled back.";
                context.Ui.ShowToast(status, UiTone.Danger);
                return;
            }
            PollProjectTriggers();
            PollProjectTasks();
            PollConversation();
            RefreshHud(force: false);
        }


        private void RemoveDeadRosterEntries()
        {
            var changed = false;
            // Snapshot first, in the reverse/LIFO order the source-unload case requires. runner.Fire below is a
            // synchronous re-entrant callback into consumer graph nodes, and those can both add to and remove
            // from `roster` (CreatorWorkbench.ProjectBindings.cs:135 and :150), so any index held across it is
            // stale afterwards: it either walks off the end or names a different, still-live entry that then gets
            // disposed and dropped. Everything past the snapshot works by reference, matching the established
            // pattern in CreatorWorkbench.Operations.cs:389.
            var dead = new List<CreatorRosterEntry>();
            for (var index = roster.Count - 1; index >= 0; index--)
            {
                if (!roster[index].IsAlive) dead.Add(roster[index]);
            }

            foreach (var entry in dead)
            {
                // A re-entrant callback from an earlier iteration may already have pruned this one.
                if (!roster.Contains(entry)) continue;
                var projectId = ProjectTargetIdForRoster(entry.Id);
                if (!string.IsNullOrEmpty(projectId))
                {
                    DisposeProjectInteractions(projectId);
                    projectEntities.Remove(projectId);
                    if (projectBindings.Remove(projectId)) confirmedNativeProjectId = string.Empty;
                    runner?.Fire(CreatorGraphNodeKind.EntityRemoved, projectId);
                }
                if (string.Equals(selectedRosterId, entry.Id, StringComparison.Ordinal)) selectedRosterId = string.Empty;
                roster.Remove(entry);
                entry.Dispose();
                changed = true;
            }
            if (changed && window?.IsVisible == true) RefreshUi();
        }

        private CreatorCatalogEntry? FindCatalog(string id) =>
            catalog.FirstOrDefault(entry => string.Equals(entry.Id, id, StringComparison.Ordinal));

        private CreatorRosterEntry? SelectedRoster() =>
            roster.FirstOrDefault(entry => string.Equals(entry.Id, selectedRosterId, StringComparison.Ordinal));

        private CreatorRosterEntry? FindRoster(string id) =>
            roster.FirstOrDefault(entry => string.Equals(entry.Id, id, StringComparison.Ordinal));

        private bool CanMutate => options.ProjectScope == CreatorProjectScope.Sandbox
            || mutationLease?.IsAlive == true && mutationLease.IsPersistenceIsolated;

        private OperationResult<bool> EnsureMutationAllowed()
        {
            if (CanMutate) return OperationResult<bool>.Success(true);
            var message = mutationSafety?.Status.Message;
            return OperationResult<bool>.Failure(
                ModErrorCode.Unavailable,
                string.IsNullOrWhiteSpace(message)
                    ? "Global creator mutations require an acknowledged persistence-isolation lease."
                    : message);
        }

        private string MutationStatusText()
        {
            if (options.ProjectScope == CreatorProjectScope.Sandbox) return "Sandbox isolation active.";
            if (CanMutate) return "GLOBAL MUTATIONS ISOLATED  •  temporary changes acknowledged";
            return mutationSafety?.Status.Message
                ?? "Global mutation safety service unavailable; browsing and project editing remain available.";
        }

        private void RequestMutationAccess()
        {
            if (options.ProjectScope == CreatorProjectScope.Sandbox || CanMutate) return;
            if (mutationSafety?.Status.PersistenceIsolationAvailable != true)
            {
                context.Ui.ShowToast(MutationStatusText(), UiTone.Warning);
                return;
            }
            if (confirmation?.IsOpen == true) return;
            var shown = context.Ui.ShowModal(
                new UiModalRequest(
                    "ENABLE TEMPORARY GLOBAL CHANGES?",
                    "Creator Tools will isolate persistent save state. Owned content is removed and borrowed edits are restored when you explicitly end the session or leave the scene.",
                    "ENABLE ISOLATION",
                    destructive: false),
                confirmed =>
                {
                    confirmation = null;
                    if (!confirmed) return;
                    var acquired = mutationSafety.Acquire(new CreatorMutationLeaseRequest(
                        "Global Creator Tools session",
                        userAcknowledgedTemporaryChanges: true));
                    if (acquired.TryGetValue(out var lease))
                    {
                        mutationLease?.Dispose();
                        mutationLease = lease;
                        status = "Persistence isolation enabled for this session.";
                        if (lease.IsAlive && lease.IsPersistenceIsolated)
                        {
                        }
                    }
                    else
                    {
                        status = acquired.ErrorMessage;
                        context.Ui.ShowToast(status, UiTone.Danger);
                    }
                    RefreshUi();
                });
            shown.TryGetValue(out confirmation);
        }

        private RobotTargetSnapshot? ResolvePlayerTarget() =>
            context.LocalPlayer.TryGetSnapshot(out var player) && player != null
                ? new RobotTargetSnapshot(player.Position)
                : (RobotTargetSnapshot?)null;
    }
}
