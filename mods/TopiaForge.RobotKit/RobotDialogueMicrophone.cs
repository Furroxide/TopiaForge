using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.RobotKit
{
    // The microphone behind RobotKit's dialogue capture, taken through the runtime's microphone arbiter: the game's own
    // robot recording comes first, a robot conversation next, and voice chat last, which yields to both. A capture claims
    // the microphone before it starts recording and gives the claim back once it has ended its recording, so voice chat
    // stays off the microphone meanwhile. While the game records, a capture cannot start. When the game starts recording
    // during a capture, the capture stops reading and leaves the device alone, because Unity keys a recording by its device
    // and ending it would end the game's recording too; it ends its own only once the game is done. A context without the
    // runtime's seam, as a test double, claims nothing. Unity-free, so the arbitration is tested without the game.
    internal sealed class RobotDialogueMicrophone
    {
        internal const string Reason = "Robot conversation";

        private readonly Func<InternalMicrophoneUse, string, OperationResult<IInternalMicrophoneLease>>? acquire;

        internal RobotDialogueMicrophone(Func<InternalMicrophoneUse, string, OperationResult<IInternalMicrophoneLease>>? acquire)
        {
            this.acquire = acquire;
        }

        // The claims a mod's context hands out: through its replication seam, or none without one.
        internal static RobotDialogueMicrophone For(IModContext context)
        {
            var replication = (context as IInternalReplicationContext)?.Replication;
            return new RobotDialogueMicrophone(replication == null
                ? null
                : new Func<InternalMicrophoneUse, string, OperationResult<IInternalMicrophoneLease>>(replication.AcquireMicrophone));
        }

        // Claims the microphone for one capture; refused with Conflict while the game's own recording holds it.
        internal OperationResult<Claim> TryClaim()
        {
            if (acquire == null) return OperationResult<Claim>.Success(new Claim(null));
            OperationResult<IInternalMicrophoneLease> taken;
            try
            {
                taken = acquire(InternalMicrophoneUse.RobotDialogue, Reason);
            }
            catch (Exception)
            {
                return OperationResult<Claim>.Failure(ModErrorCode.Unavailable, "The microphone could not be claimed for the robot conversation.");
            }

            if (!taken.TryGetValue(out var lease))
            {
                return OperationResult<Claim>.Failure(
                    taken.ErrorCode,
                    taken.ErrorCode == ModErrorCode.Conflict
                        ? "The game is recording with the microphone; try again once it is done."
                        : "The microphone could not be claimed for the robot conversation.");
            }

            return OperationResult<Claim>.Success(new Claim(lease));
        }

        // One capture's claim on the microphone.
        internal sealed class Claim
        {
            private IInternalMicrophoneLease? lease;

            internal Claim(IInternalMicrophoneLease? lease)
            {
                this.lease = lease;
            }

            // Whether the game's own recording has the microphone now: the capture must not read it, nor end it.
            internal bool IsPreempted
            {
                get
                {
                    var held = lease;
                    return held != null && !held.IsReleased && held.IsPreempted;
                }
            }

            // Whether the capture let go of its recording without ending it, because the game took the device.
            internal bool IsAbandoned { get; private set; }

            // Whether the claim was given back: the capture's recording is ended, or no longer its to end.
            internal bool IsReleased { get; private set; }

            // Whether an abandoned capture may end its recording now: the game has let go of the microphone.
            internal bool MayEndAbandoned => IsAbandoned && !IsReleased && !IsPreempted;

            internal void Abandon() => IsAbandoned = true;

            internal void Release()
            {
                if (IsReleased) return;
                IsReleased = true;
                var held = lease;
                lease = null;
                held?.Dispose();
            }
        }
    }
}
