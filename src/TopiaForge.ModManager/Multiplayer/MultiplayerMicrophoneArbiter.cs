using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// Decides who uses the one microphone. The game's own robot recording comes first, RobotKit's robot dialogue next and
    /// voice chat last, which yields to both. A use is refused while one that comes before it holds the microphone, and a
    /// hold is preempted while one that comes before it takes the microphone later; several holds of one use may coexist.
    /// The game's recording is observed rather than acquired, and read from the game at every question
    /// (<see cref="ObserveNativeRecording"/>): a claim, a check for preemption or a release made at any moment of a frame,
    /// before or after voice chat's frame, sees the game as it is then. Main thread only.
    /// </summary>
    internal sealed class MultiplayerMicrophoneArbiter
    {
        private const int Uses = 3;

        private readonly int[] holds = new int[Uses];
        private Func<bool>? nativeRecording;

        /// <summary>
        /// Reads the game's own robot recording from <paramref name="isRecording"/> at every question from now on: from the
        /// push-to-record key going down until the game has decoded what it heard. The plugin sets it before any mod loads.
        /// A read that throws, as after a game update moved that state, counts as recording, so nothing records over the
        /// game. Until a source is set, as offline and in tests that set none, the game never records.
        /// </summary>
        internal void ObserveNativeRecording(Func<bool> isRecording) =>
            nativeRecording = isRecording ?? throw new ArgumentNullException(nameof(isRecording));

        /// <summary>Gets whether the game's own robot recording has the microphone now, read from the game as it is asked.</summary>
        internal bool NativeRecording => ReadNative();

        /// <summary>Gets whether the last read of the game's recording failed, which counted as recording.</summary>
        internal bool NativeUnreadable { get; private set; }

        /// <summary>Gets the use that has the microphone now, or null when none does.</summary>
        internal InternalMicrophoneUse? Holder => FirstInUse(Uses);

        /// <summary>Gets how many holds of <paramref name="use"/> are taken.</summary>
        internal int HoldsOf(InternalMicrophoneUse use)
        {
            RequireDefined(use);
            return use == InternalMicrophoneUse.NativeRecording ? (ReadNative() ? 1 : 0) : holds[(int)use];
        }

        /// <summary>Gets whether a use that comes before <paramref name="use"/> has the microphone.</summary>
        internal bool IsHeldAbove(InternalMicrophoneUse use)
        {
            RequireDefined(use);
            return FirstInUse((int)use).HasValue;
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

            // Read once, so the use named is the one that refused, whatever the game's state does next.
            var above = FirstInUse((int)use);
            if (above.HasValue)
            {
                return OperationResult<Hold>.Failure(ModErrorCode.Conflict, "The microphone is in use for " + Describe(above.Value) + ".");
            }

            holds[(int)use]++;
            return OperationResult<Hold>.Success(new Hold(this, use, reason));
        }

        /// <summary>The first use before <paramref name="end"/> that has the microphone; the game's recording is read first, now.</summary>
        private InternalMicrophoneUse? FirstInUse(int end)
        {
            for (var index = 0; index < end; index++)
            {
                if (IsInUse((InternalMicrophoneUse)index)) return (InternalMicrophoneUse)index;
            }

            return null;
        }

        private bool IsInUse(InternalMicrophoneUse use) =>
            use == InternalMicrophoneUse.NativeRecording ? ReadNative() : holds[(int)use] > 0;

        private bool ReadNative()
        {
            var source = nativeRecording;
            if (source == null)
            {
                NativeUnreadable = false;
                return false;
            }

            try
            {
                var recording = source();
                NativeUnreadable = false;
                return recording;
            }
            catch (Exception)
            {
                NativeUnreadable = true;
                return true;
            }
        }

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

            /// <summary>Gets whether a use that comes first has the microphone now, the game's recording read as it is asked.</summary>
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
