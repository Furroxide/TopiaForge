using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed partial class MultiplayerPresence : IInternalCommunicationPositionSource
    {
        public bool TryGetCommunicationPosition(ParticipantId participant, out Vec3 position)
        {
            position = default;
            if (RunningWorld?.Invoke() == null || !Host.IsCurrentWorld) return false;
            var validated = ValidatedPosition(participant);
            if (!validated.HasValue) return false;
            var value = validated.Value;
            position = new Vec3(value.X, value.Y, value.Z);
            return true;
        }
    }
}
