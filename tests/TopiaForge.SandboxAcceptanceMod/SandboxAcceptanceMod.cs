using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.SandboxAcceptance
{
    public sealed partial class SandboxAcceptanceMod : TopiaForgeMod, ISandboxAcceptanceFixture
    {
        public const string SandboxTargetId = "io.github.furroxide.topiaforge.sandbox.creator.menu";
        private ICreatorContentService? content;
        private ICreatorProjectLibrary? library;
        private IRobotAgentService? robots;
        private IWorldSessionService? worlds;
        private ICreatorToolHostService? router;
        private ICreatorMutationSafetyService? safety;
        private readonly List<ICreatorContentRegistration> registrations = new List<ICreatorContentRegistration>();
        private readonly List<TrackedSource> sources = new List<TrackedSource>();
        private readonly List<string> errors = new List<string>();
        private Task<OperationResult<CreatorProjectSummary>>? save;
        private Task<OperationResult<bool>>? stop;
        private Task<OperationResult<bool>>? deletion;
        private IWorldSession? ownedSession;
        private bool preparing;
        public string Challenge { get; private set; } = string.Empty;
        public string WorldSessionId => worlds?.Current.Session?.SessionId ?? string.Empty;
        public long Frame { get; private set; }
        public bool Prepared { get; private set; }
        public string ProjectId => "sandbox-acceptance-" + Challenge.Substring(0, Math.Min(24, Challenge.Length));

        protected override void OnLoad()
        {
            var loaded = Context.Config.Load(new ConfigDefinition<SandboxAcceptanceConfig>(1,
                () => new SandboxAcceptanceConfig(), config => !config.Enabled || IsChallenge(config.Challenge)
                    ? OperationResult<bool>.Success(true)
                    : OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "An enabled fixture requires a 256-bit challenge.")));
            if (!loaded.TryGetValue(out var config) || !config.Enabled) return;
            Challenge = config.Challenge;
            Context.Extensions.TryGet(out content);
            Context.Extensions.TryGet(out library);
            Context.Extensions.TryGet(out robots);
            Context.Extensions.TryGet(out worlds);
            Context.Extensions.TryGet(out router);
            Context.Extensions.TryGet(out safety);
            Context.Extensions.Register<ISandboxAcceptanceFixture>(this);
            Context.Events.SubscribeUpdate(_ => Tick());
        }

        private void Tick()
        {
            Frame++;
            if (save?.IsCompleted == true)
            {
                try
                {
                    var result = save.GetAwaiter().GetResult();
                    Prepared = result.Succeeded && ownedSession?.SessionId == WorldSessionId;
                    if (!result.Succeeded) errors.Add("Fixture project save: " + result.ErrorMessage);
                }
                catch (Exception exception) { errors.Add("Fixture project save: " + exception.Message); }
                save = null;
                preparing = false;
            }
            ObserveTask(ref stop, "Fixture session stop");
            ObserveTask(ref deletion, "Fixture project deletion");
        }

        private void ObserveTask(ref Task<OperationResult<bool>>? task, string label)
        {
            if (task?.IsCompleted != true) return;
            try { var result = task.GetAwaiter().GetResult(); if (!result.Succeeded) errors.Add(label + ": " + result.ErrorMessage); }
            catch (Exception exception) { errors.Add(label + ": " + exception.Message); }
            task = null;
        }

        public OperationResult<bool> Prepare(ICreatorContentFactory propFactory)
        {
            if (Prepared || preparing) return OperationResult<bool>.Failure(ModErrorCode.Conflict, "A fixture is already prepared or preparing.");
            if (!IsChallenge(Challenge) || Context.Lifetime.IsStopping || content == null || library == null || robots == null || worlds == null)
                return OperationResult<bool>.Failure(ModErrorCode.Unavailable, "Required production fixture services are unavailable.");
            var current = worlds.Current;
            if (current.Phase != WorldSessionPhase.Running || current.Session?.TargetId != SandboxTargetId)
                return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "Prepare requires the actual running Sandbox launch target.");
            if (save != null || deletion != null) return OperationResult<bool>.Failure(ModErrorCode.Conflict, "Prior fixture persistence is still pending.");
            ownedSession = current.Session;
            errors.Clear();
            sources.Clear();
            var registered = content.Register(new CreatorContentRegistrationRequest("prop", "Acceptance prop", "Synthetic native QA prop.",
                CreatorContentKind.Prop, CreatorTransformCapabilities.All, new TrackingFactory(this, "prop", propFactory)));
            if (!registered.TryGetValue(out var registration)) return OperationResult<bool>.Failure(registered.ErrorCode, registered.ErrorMessage);
            registrations.Add(registration);
            var character = content.Register(new CreatorContentRegistrationRequest("character", "Acceptance character", "Real RobotKit fixture character.",
                CreatorContentKind.Character, CreatorTransformCapabilities.All, new TrackingFactory(this, "character", new RobotFactory(Context, robots))));
            if (!character.TryGetValue(out registration)) { Cleanup(); return OperationResult<bool>.Failure(character.ErrorCode, character.ErrorMessage); }
            registrations.Add(registration);
            preparing = true;
            save = library.SaveAsync(BuildProject(current.Session.WorldId));
            return OperationResult<bool>.Success(true);
        }

        public OperationResult<bool> UnregisterSource()
        {
            var failures = new List<string>();
            foreach (var registration in registrations.ToArray())
            {
                registrations.Remove(registration);
                try { registration.Dispose(); } catch (Exception exception) { failures.Add(exception.Message); }
            }
            errors.AddRange(failures);
            return failures.Count == 0 ? OperationResult<bool>.Success(true)
                : OperationResult<bool>.Failure(ModErrorCode.External, string.Join(" | ", failures));
        }

        public OperationResult<bool> RequestSessionStop() => RequestLifecycle(0);
        public OperationResult<bool> RequestSessionRestart() => RequestLifecycle(1);
        public OperationResult<bool> RequestReturnToMainMenu() => RequestLifecycle(2);
        private OperationResult<bool> RequestLifecycle(int route)
        {
            if (ownedSession == null || ownedSession.SessionId != WorldSessionId)
                return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "The prepared Sandbox session is no longer current.");
            if (stop != null) return OperationResult<bool>.Failure(ModErrorCode.Conflict, "A fixture lifecycle request is already pending.");
            stop = route == 0 ? ownedSession.StopAsync() : route == 1 ? ownedSession.RestartAsync() : ownedSession.ReturnToMainMenuAsync();
            return OperationResult<bool>.Success(true);
        }

        public OperationResult<bool> Cleanup()
        {
            Prepared = false;
            preparing = false;
            var result = UnregisterSource();
            foreach (var source in sources.ToArray())
                try { source.Dispose(); } catch (Exception exception) { errors.Add(exception.Message); }
            // Protocol v2 control owners (competing host, control cue, control robot) are reversed here as well.
            ReleaseControls();
            if (save != null) return OperationResult<bool>.Failure(ModErrorCode.Conflict, "Fixture save must finish before cleanup can delete its own project.");
            if (library != null && IsChallenge(Challenge) && deletion == null) deletion = library.DeleteAsync(ProjectId);
            ownedSession = null;
            return result;
        }

        protected override void OnUnload() { Cleanup(); }
        public SandboxFixtureSnapshot Capture()
        {
            var current = worlds?.Current;
            var unavailable = new List<string>();
            if (content == null) unavailable.Add("creator-content-service");
            if (robots?.IsAvailable != true) unavailable.Add("robotkit-native-agents");
            if (preparing) unavailable.Add("fixture-project-save-pending");
            if (!Prepared) unavailable.Add("fixture-not-prepared");
            if (deletion != null) unavailable.Add("fixture-project-deletion-pending");
            var snapshot = new SandboxFixtureSnapshot
            {
                WorldSessionId = WorldSessionId, SessionPhase = current?.Phase.ToString() ?? "Unavailable", SessionSequence = current?.Sequence ?? 0,
                TargetId = current?.Session?.TargetId ?? "", GamemodeId = current?.Session?.GamemodeId ?? "", WorldId = current?.Session?.WorldId ?? "",
                ActiveHostId = router?.ActiveHost?.SourceId ?? "", CatalogIds = content?.Catalog.Entries.Select(e => e.ContentId).OrderBy(id => id, StringComparer.Ordinal).Take(4096).ToArray() ?? Array.Empty<string>(),
                RobotTypes = robots?.RobotTypes.Select(t => t.Id).OrderBy(id => id, StringComparer.Ordinal).Take(256).ToArray() ?? Array.Empty<string>(),
                Objects = sources.Select(s => s.Snapshot()).ToArray(), CreatedObjects = sources.Count,
                DisposedObjects = sources.Count(s => s.Disposed), CleanupErrors = errors.Take(64).ToArray(), UnavailableReasons = unavailable.ToArray(),
                MutationSafetyState = safety?.Status.State.ToString() ?? "Unavailable", PersistenceIsolationAvailable = safety?.Status.PersistenceIsolationAvailable == true,
                CompetingHostRegistered = CompetingHostRegistered, CompetingHostCanOpenCalls = competing.CanOpenCalls,
                CompetingHostOpenCalls = competing.OpenCalls, CompetingHostCloseCalls = competing.CloseCalls,
                ControlCuePlaying = ControlCuePlaying, ControlRobotEntityId = controlRobot?.Id ?? "", ControlRobotAlive = ControlRobot != null
            };
            if (Context.LocalPlayer.TryGetSnapshot(out var player) && player != null)
            {
                snapshot.PlayerPosition = new[] { player.Position.X, player.Position.Y, player.Position.Z };
                snapshot.PlayerAim = new[] { player.AimRay.Direction.X, player.AimRay.Direction.Y, player.AimRay.Direction.Z };
            }
            return snapshot;
        }
        private static bool IsChallenge(string value) => value != null && value.Length == 64 && value.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');
    }
}
