using System;
using TopiaForge.Mods;

namespace TopiaForge.SandboxAcceptance
{
    // Unrelated control owners for protocol v2: a competing workbench host, a looping control cue and a dormant
    // control robot. Each is owned by this fixture and never by a content source, so source teardown must leave
    // it alive; cleanup releases every one of them independently.
    public sealed partial class SandboxAcceptanceMod
    {
        public const string ControlCueId = "sandbox-acceptance-control";
        private CompetingHost competing = new CompetingHost();
        private ICreatorToolHostRegistration? competingRegistration;
        private IAudioPlayback? controlCue;
        private IRobotAgent? controlRobot;

        public IEntity? ControlRobot => controlRobot != null && controlRobot.IsAlive ? controlRobot : null;
        private bool CompetingHostRegistered => competingRegistration?.IsAlive == true;
        private bool ControlCuePlaying => controlCue?.IsPlaying == true;

        public OperationResult<bool> RegisterCompetingHost()
        {
            if (router == null) return OperationResult<bool>.Failure(ModErrorCode.Unavailable, "Creator tool host routing is unavailable.");
            if (CompetingHostRegistered) return OperationResult<bool>.Failure(ModErrorCode.Conflict, "The competing host is already registered.");
            // Counters restart with every registration so a cycle observes only its own routing decisions.
            competing = new CompetingHost();
            var registered = router.RegisterHost(new CreatorToolHostRegistrationRequest("competing", "Acceptance competing host", 100, competing));
            if (!registered.TryGetValue(out var registration)) return OperationResult<bool>.Failure(registered.ErrorCode, registered.ErrorMessage);
            competingRegistration = registration;
            return OperationResult<bool>.Success(true);
        }

        public OperationResult<bool> UnregisterCompetingHost()
        {
            var registration = competingRegistration;
            if (registration == null) return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "No competing host is registered.");
            competingRegistration = null;
            try { registration.Dispose(); }
            catch (Exception exception) { return OperationResult<bool>.Failure(ModErrorCode.External, "Competing host release: " + exception.Message); }
            return OperationResult<bool>.Success(true);
        }

        public OperationResult<bool> PlayControlCue()
        {
            if (ControlCuePlaying) return OperationResult<bool>.Failure(ModErrorCode.Conflict, "The control cue is already playing.");
            controlCue?.Dispose();
            controlCue = null;
            var played = Context.Audio.Play(new AudioPlayRequest(ControlCueId, 1f, loop: true));
            if (!played.TryGetValue(out var playback)) return OperationResult<bool>.Failure(played.ErrorCode, played.ErrorMessage);
            controlCue = playback;
            return OperationResult<bool>.Success(true);
        }

        public OperationResult<bool> StopControlCue()
        {
            var cue = controlCue;
            if (cue == null) return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "No control cue is playing.");
            controlCue = null;
            try { cue.Stop(); }
            catch (Exception exception) { return OperationResult<bool>.Failure(ModErrorCode.External, "Control cue stop: " + exception.Message); }
            finally { cue.Dispose(); }
            return OperationResult<bool>.Success(true);
        }

        public OperationResult<bool> SpawnControlRobot()
        {
            if (robots == null || !robots.IsAvailable) return OperationResult<bool>.Failure(ModErrorCode.Unavailable, "RobotKit native agents are unavailable.");
            if (ControlRobot != null) return OperationResult<bool>.Failure(ModErrorCode.Conflict, "The control robot already exists.");
            if (!Context.LocalPlayer.TryGetSnapshot(out var player) || player == null)
                return OperationResult<bool>.Failure(ModErrorCode.Unavailable, "The local player position is unavailable.");
            ReleaseControlRobot();
            var spawned = robots.Spawn(new RobotAgentSpawnRequest(player.Position + new Vec3(-3f, 0f, 3f), brainMode: RobotBrainMode.Dormant,
                name: "Sandbox acceptance control robot"));
            if (!spawned.TryGetValue(out var agent)) return OperationResult<bool>.Failure(spawned.ErrorCode, spawned.ErrorMessage);
            controlRobot = agent;
            return OperationResult<bool>.Success(true);
        }

        public OperationResult<bool> DespawnControlRobot()
        {
            var robot = controlRobot;
            if (robot == null) return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "No control robot exists.");
            controlRobot = null;
            OperationResult<bool> despawned;
            try { despawned = robot.IsAlive ? robot.Despawn() : OperationResult<bool>.Success(false); }
            catch (Exception exception) { return OperationResult<bool>.Failure(ModErrorCode.External, "Control robot despawn: " + exception.Message); }
            finally { robot.Dispose(); }
            return despawned.Succeeded ? OperationResult<bool>.Success(true) : OperationResult<bool>.Failure(despawned.ErrorCode, despawned.ErrorMessage);
        }

        // Every control owner is released independently; a failure is recorded as a cleanup error and never
        // skips the remaining releases.
        private void ReleaseControls()
        {
            var registration = competingRegistration;
            competingRegistration = null;
            try { registration?.Dispose(); }
            catch (Exception exception) { errors.Add("Competing host release: " + exception.Message); }
            var cue = controlCue;
            controlCue = null;
            try { cue?.Dispose(); }
            catch (Exception exception) { errors.Add("Control cue release: " + exception.Message); }
            ReleaseControlRobot();
        }

        private void ReleaseControlRobot()
        {
            var robot = controlRobot;
            controlRobot = null;
            if (robot == null) return;
            try
            {
                if (robot.IsAlive)
                {
                    var despawned = robot.Despawn();
                    if (!despawned.Succeeded && robot.IsAlive) errors.Add("Control robot release: " + despawned.ErrorMessage);
                }
            }
            catch (Exception exception) { errors.Add("Control robot release: " + exception.Message); }
            finally
            {
                try { robot.Dispose(); }
                catch (Exception exception) { errors.Add("Control robot dispose: " + exception.Message); }
            }
        }

        // Competes for the shared F5 route below the Sandbox host (priority 100 versus 200). It reports itself as
        // available so the router's priority decision is measured, yet an Open call never succeeds.
        private sealed class CompetingHost : ICreatorToolHost
        {
            public int CanOpenCalls { get; private set; }
            public int OpenCalls { get; private set; }
            public int CloseCalls { get; private set; }
            public bool IsOpen => false;
            public bool CanOpen(CreatorToolOpenContext context) { CanOpenCalls++; return true; }
            public OperationResult<bool> Open(CreatorToolOpenContext context)
            {
                OpenCalls++;
                return OperationResult<bool>.Failure(ModErrorCode.Unavailable, "The acceptance competing host never opens.");
            }
            public OperationResult<bool> Close(CreatorToolCloseReason reason) { CloseCalls++; return OperationResult<bool>.Success(false); }
        }
    }
}
