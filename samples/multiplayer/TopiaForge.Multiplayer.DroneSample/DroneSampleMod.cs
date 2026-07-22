using System;
using TopiaForge.Mods;

namespace TopiaForge.Multiplayer.DroneSample
{
    /// <summary>Dogfoods a server-created replicated object with owner prediction.</summary>
    [MultiplayerContract(Id = "io.github.furroxide.topiaforge.samples.drone")]
    public sealed partial class DroneSampleMod : TopiaForgeMod
    {
        private IReplicatedObject<DroneState, DroneInput>? drone;

        /// <inheritdoc/>
        protected override void OnLoad()
        {
            var session = Context.RequireMultiplayer();
            var binding = BindMultiplayer(session);
            if (!binding.TryGetValue(out var lease))
            {
                throw new InvalidOperationException(binding.ErrorMessage);
            }

            Context.Lifetime.Track(lease);
            if (!session.Snapshot.HasWorldAuthority)
            {
                return;
            }

            ParticipantId? owner = session.Snapshot.LocalParticipantId;
            if (!owner.HasValue)
            {
                foreach (var participant in session.Snapshot.Participants)
                {
                    if (!participant.IsConnected)
                    {
                        continue;
                    }

                    owner = participant.Id;
                    break;
                }
            }

            var spawned = SpawnDroneObject(
                new DroneState { Callsign = "TF-1" },
                owner);
            if (!spawned.TryGetValue(out drone))
            {
                throw new InvalidOperationException(spawned.ErrorMessage);
            }

            Context.Logger.Info("Multiplayer drone sample spawned a canonical predicted object.");
        }

        [ReplicatedObject("drone", Prediction = PredictionMode.Owner, MaximumPerSecond = 30, MaximumPayloadBytes = 512)]
        private static OperationResult<DroneState> ApplyDroneInput(
            ReplicatedObjectCommandContext command,
            DroneState state,
            DroneInput input)
        {
            if (!command.SenderOwnsTarget)
            {
                return OperationResult<DroneState>.Failure(
                    ModErrorCode.NotAuthoritative,
                    "Only the current owner may steer this drone.");
            }

            if (input.Horizontal < -4 || input.Horizontal > 4 ||
                input.Vertical < -4 || input.Vertical > 4)
            {
                return OperationResult<DroneState>.Failure(
                    ModErrorCode.InvalidArgument,
                    "Drone input must remain within the server-validated movement bound.");
            }

            return OperationResult<DroneState>.Success(
                new DroneState
                {
                    X = checked(state.X + input.Horizontal),
                    Y = checked(state.Y + input.Vertical),
                    Callsign = state.Callsign
                });
        }
    }

    public sealed class DroneState
    {
        public int X { get; set; }
        public int Y { get; set; }

        [NetworkBound(24)]
        public string Callsign { get; set; } = string.Empty;
    }

    public sealed class DroneInput
    {
        public int Horizontal { get; set; }
        public int Vertical { get; set; }
    }
}
