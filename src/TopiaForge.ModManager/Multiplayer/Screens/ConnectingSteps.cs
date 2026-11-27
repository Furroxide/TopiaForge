using System;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>The connecting overlay's phases, in the order the stepper shows them.</summary>
    internal enum ConnectingStep
    {
        Resolving = 0,
        Connecting = 1,
        Authenticating = 2,
        VerifyingMods = 3,
        LoadingWorld = 4,
        Synchronizing = 5,
        Spawning = 6
    }

    /// <summary>
    /// The state the connecting overlay's stepper should show: the active or failed step, whether it is complete, and the
    /// steps passed over. <see cref="Version"/> moves on every change, so the view rebuilds the stepper only then.
    /// </summary>
    internal readonly struct ConnectingSteps
    {
        internal const int Count = 7;

        internal ConnectingSteps(int version, int active, int failed, bool complete, int skippedMask)
        {
            Version = version;
            Active = active;
            Failed = failed;
            Complete = complete;
            SkippedMask = skippedMask;
        }

        internal int Version { get; }

        /// <summary>Gets the step in progress, or -1.</summary>
        internal int Active { get; }

        /// <summary>Gets the step that failed, or -1.</summary>
        internal int Failed { get; }

        internal bool Complete { get; }

        internal int SkippedMask { get; }

        internal bool IsSkipped(int step) => step >= 0 && step < Count && (SkippedMask & (1 << step)) != 0;
    }

    /// <summary>The operations a phase stepper offers, so one replay drives the kit's widget and its state alike.</summary>
    internal interface IStepperOperations
    {
        bool Reset();

        bool Skip(int index);

        bool MoveTo(int index);

        bool Fail();

        bool Complete();
    }

    /// <summary>Brings a stepper to a <see cref="ConnectingSteps"/> target from any state it was in.</summary>
    internal static class ConnectingStepReplay
    {
        internal static void Apply(ConnectingSteps target, IStepperOperations stepper)
        {
            if (stepper == null) throw new ArgumentNullException(nameof(stepper));
            stepper.Reset();
            for (var step = 0; step < ConnectingSteps.Count; step++)
            {
                if (target.IsSkipped(step)) stepper.Skip(step);
            }

            if (target.Complete)
            {
                stepper.Complete();
                return;
            }

            if (target.Failed >= 0)
            {
                stepper.MoveTo(target.Failed);
                stepper.Fail();
                return;
            }

            if (target.Active >= 0) stepper.MoveTo(target.Active);
        }
    }
}
