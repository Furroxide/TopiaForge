using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.RobotKit
{
    internal sealed partial class RobotReplication
    {
        private static bool IsGuestOrigin(IRuntimeReplication replication) =>
            replication.Realm == InternalNetRealm.Client ||
            replication is IInternalEntityCommands commands && commands.IsGuestOrigin;

        private static RobotAgentSpawnRequest WithoutNativeBrain(RobotAgentSpawnRequest request) =>
            Copy(request, brain: RobotBrainMode.Dormant, interaction: RobotInteractionOptions.DisableNativeTalk());

        internal static OperationResult<bool>? BrainRefusal(IRuntimeReplication replication, RobotBrainMode brain, bool replica = false) =>
            brain == RobotBrainMode.Autonomous && (replica || IsGuestOrigin(replication)) ? NativeBrainRefusal() : null;

        internal static OperationResult<bool>? TalkRefusal(IRuntimeReplication replication, RobotInteractionOptions interaction, bool replica = false) =>
            interaction.NativeTalkMode == RobotNativeTalkMode.Enabled && (replica || IsGuestOrigin(replication)) ? NativeBrainRefusal() : null;

        private static OperationResult<bool> NativeBrainRefusal() => OperationResult<bool>.Failure(
            ModErrorCode.NotAuthoritative, "Autonomous robots and native conversation must be started by the host.");
    }
}
