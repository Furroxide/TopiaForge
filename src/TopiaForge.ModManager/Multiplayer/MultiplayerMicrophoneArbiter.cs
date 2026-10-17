using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// Decides who uses the one microphone. The game's own robot recording comes first, RobotKit's robot dialogue next and
    /// voice chat last, which yields to both. A use is refused while one that comes before it holds the microphone, and a
    /// hold is preempted while one that comes before it takes the microphone later; several holds of one use may coexist.
    /// The game's recording is observed rather than acquired: the voice slice (WS5) sets
    /// <see cref="NativeRecording"/> from the game's state. Main thread only.
    /// </summary>
    internal sealed class MultiplayerMicrophoneArbiter
    {
        private const int Uses = 3;

        private readonly int[] holds = new int[Uses];
        private bool nativeRecording;

        /// <summary>Gets or sets whether the game's own robot recording has the microphone.</summary>
        internal bool NativeRecording
        {
            get => nativeRecording;
            set => nativeRecording = value;
        }

        /// <summary>Gets the use that has the microphone now, or null when none does.</summary>
        internal InternalMicrophoneUse? Holder
        {
            get
            {
                for (var index = 0; index < Uses; index++)
                {
                    if (IsInUse((InternalMicrophoneUse)index)) return (InternalMicrophoneUse)index;
                }

                return null;
            }
        }

        /// <summary>Gets how many holds of <paramref name="use"/> are taken.</summary>
        internal int HoldsOf(InternalMicrophoneUse use)
        {
            RequireDefined(use);
            return use == InternalMicrophoneUse.NativeRecording ? (nativeRecording ? 1 : 0) : holds[(int)use];
        }

        /// <summary>Gets whether a use that comes before <paramref name="use"/> has the microphone.</summary>
        internal bool IsHeldAbove(InternalMicrophoneUse use)
        {
            RequireDefined(use);
            for (var index = 0; index < (int)use; index++)
            {
                if (IsInUse((InternalMicrophoneUse)index)) return true;
            }

            return false;
        }

        /// <summary>
        /// Takes the microphone for robot dialogue or voice chat. The game's own recording cannot be taken, and a use is
        /// refused with <see cref="ModErrorCode.Conflict"/> while one that comes before it has the microphone.
        /// </summary>
        internal OperationResult<Hold> TryAcquire(InternalMicrophoneUse use, string reason)
        {
            if (reason == null) throw new ArgumentNullException(nameof(reason));
            if (use == InternalMicrophoneUse.NativeRecording)
            {
                return OperationResult<Hold>.Failure(ModErrorCode.InvalidArgument, "The game's own recording is observed, never taken.");
            }

            if (use != InternalMicrophoneUse.RobotDialogue && use != InternalMicrophoneUse.VoiceChat)
            {
                return OperationResult<Hold>.Failure(ModErrorCode.InvalidArgument, "That is not a use of the microphone.");
            }

            if (!InternalReplicationLimits.IsText(reason, InternalReplicationLimits.MaximumReasonLength))
            {
                return OperationResult<Hold>.Failure(ModErrorCode.InvalidArgument, "A reason is 1 to 128 printable characters.");
            }

            if (IsHeldAbove(use))
            {
                return OperationResult<Hold>.Failure(ModErrorCode.Conflict, "The microphone is in use for " + Describe(Holder!.Value) + ".");
            }

            holds[(int)use]++;
            return OperationResult<Hold>.Success(new Hold(this, use, reason));
        }

        private bool IsInUse(InternalMicrophoneUse use) =>
            use == InternalMicrophoneUse.NativeRecording ? nativeRecording : holds[(int)use] > 0;

        private void Release(InternalMicrophoneUse use) => holds[(int)use]--;

        private static void RequireDefined(InternalMicrophoneUse use)
        {
            if (use < InternalMicrophoneUse.NativeRecording || use > InternalMicrophoneUse.VoiceChat) throw new ArgumentOutOfRangeException(nameof(use));
        }

        private static string Describe(InternalMicrophoneUse use) =>
            use == InternalMicrophoneUse.NativeRecording ? "the game's robot recording"
            : use == InternalMicrophoneUse.RobotDialogue ? "robot dialogue"
            : "voice chat";

        /// <summary>One hold on the microphone. Releasing it gives the microphone back once.</summary>
        internal sealed class Hold
        {
            private readonly MultiplayerMicrophoneArbiter arbiter;

            internal Hold(MultiplayerMicrophoneArbiter arbiter, InternalMicrophoneUse use, string reason)
            {
                this.arbiter = arbiter;
                Use = use;
                Reason = reason;
            }

            internal InternalMicrophoneUse Use { get; }

            /// <summary>Gets why the microphone was taken, for diagnostics.</summary>
            internal string Reason { get; }

            internal bool IsReleased { get; private set; }

            /// <summary>Gets whether a use that comes first has the microphone now.</summary>
            internal bool IsPreempted => !IsReleased && arbiter.IsHeldAbove(Use);

            internal void Release()
            {
                if (IsReleased) return;
                IsReleased = true;
                arbiter.Release(Use);
            }
        }
    }
}
