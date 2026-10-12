using System;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>The status of one step in a phase stepper.</summary>
    public enum TopiaForgeStepStatus
    {
        /// <summary>Not reached yet.</summary>
        Pending,

        /// <summary>In progress.</summary>
        Active,

        /// <summary>Finished.</summary>
        Done,

        /// <summary>Stopped the flow.</summary>
        Failed,

        /// <summary>Passed over without running.</summary>
        Skipped,
    }

    /// <summary>
    /// The state machine behind a phase stepper: an ordered list of steps where at most one is
    /// active and at most one has failed. Transitions return whether anything changed, and
    /// <see cref="Version"/> moves on every change so a view repaints only when needed.
    /// </summary>
    public sealed class TopiaForgePhaseStepperState
    {
        /// <summary>The most steps a stepper may have.</summary>
        public const int MaxSteps = 16;

        private readonly TopiaForgeStepStatus[] statuses;
        private readonly int[] progressCurrent;
        private readonly int[] progressTotal;

        /// <summary>Creates a stepper whose steps are all pending.</summary>
        /// <exception cref="ArgumentOutOfRangeException">The count is not between 1 and <see cref="MaxSteps"/>.</exception>
        public TopiaForgePhaseStepperState(int stepCount)
        {
            if (stepCount < 1 || stepCount > MaxSteps)
            {
                throw new ArgumentOutOfRangeException(nameof(stepCount), "A phase stepper needs between 1 and " + MaxSteps + " steps.");
            }

            statuses = new TopiaForgeStepStatus[stepCount];
            progressCurrent = new int[stepCount];
            progressTotal = new int[stepCount];
        }

        /// <summary>Gets the number of steps.</summary>
        public int Count => statuses.Length;

        /// <summary>Gets a counter that changes whenever any status or progress changes.</summary>
        public int Version { get; private set; }

        /// <summary>Gets the active step, or -1.</summary>
        public int ActiveIndex => IndexOf(TopiaForgeStepStatus.Active);

        /// <summary>Gets the failed step, or -1.</summary>
        public int FailedIndex => IndexOf(TopiaForgeStepStatus.Failed);

        /// <summary>Gets whether a step has failed.</summary>
        public bool HasFailed => FailedIndex >= 0;

        /// <summary>Gets whether every step is done or skipped.</summary>
        public bool IsComplete
        {
            get
            {
                for (var index = 0; index < statuses.Length; index++)
                {
                    if (statuses[index] != TopiaForgeStepStatus.Done && statuses[index] != TopiaForgeStepStatus.Skipped)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        /// <summary>Gets the number of finished steps.</summary>
        public int DoneCount
        {
            get
            {
                var count = 0;
                for (var index = 0; index < statuses.Length; index++)
                {
                    if (statuses[index] == TopiaForgeStepStatus.Done)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>Gets the status of a step.</summary>
        public TopiaForgeStepStatus StatusOf(int index)
        {
            return statuses[CheckIndex(index)];
        }

        /// <summary>Reads a step's progress; false when the step has none.</summary>
        public bool TryGetProgress(int index, out int current, out int total)
        {
            CheckIndex(index);
            current = progressCurrent[index];
            total = progressTotal[index];
            return total > 0;
        }

        /// <summary>Activates the first pending step when nothing is active, failed or complete.</summary>
        public bool Start()
        {
            if (ActiveIndex >= 0 || HasFailed)
            {
                return false;
            }

            var next = NextPending(0);
            if (next < 0)
            {
                return false;
            }

            Set(next, TopiaForgeStepStatus.Active);
            Version++;
            return true;
        }

        /// <summary>
        /// Finishes the active step and activates the next pending one, skipping skipped steps.
        /// With nothing active it behaves like <see cref="Start"/>. Refused after a failure.
        /// </summary>
        public bool Advance()
        {
            if (HasFailed)
            {
                return false;
            }

            var active = ActiveIndex;
            if (active < 0)
            {
                return Start();
            }

            Set(active, TopiaForgeStepStatus.Done);
            var next = NextPending(active + 1);
            if (next >= 0)
            {
                Set(next, TopiaForgeStepStatus.Active);
            }

            Version++;
            return true;
        }

        /// <summary>
        /// Jumps straight to a step, as when a driver reports its phase directly. Earlier steps that
        /// were not skipped become done, later ones pending (their progress clears), and any failure
        /// clears. Activating a skipped step un-skips it.
        /// </summary>
        public bool MoveTo(int index)
        {
            CheckIndex(index);
            var changed = false;
            for (var step = 0; step < statuses.Length; step++)
            {
                var current = statuses[step];
                TopiaForgeStepStatus next;
                if (step < index)
                {
                    next = current == TopiaForgeStepStatus.Skipped ? TopiaForgeStepStatus.Skipped : TopiaForgeStepStatus.Done;
                }
                else if (step == index)
                {
                    next = TopiaForgeStepStatus.Active;
                }
                else
                {
                    next = current == TopiaForgeStepStatus.Skipped ? TopiaForgeStepStatus.Skipped : TopiaForgeStepStatus.Pending;
                    if (progressTotal[step] != 0 || progressCurrent[step] != 0)
                    {
                        progressCurrent[step] = 0;
                        progressTotal[step] = 0;
                        changed = true;
                    }
                }

                if (next != current)
                {
                    statuses[step] = next;
                    changed = true;
                }
            }

            if (changed)
            {
                Version++;
            }

            return changed;
        }

        /// <summary>Marks the active step failed, or the first pending step when nothing is active.</summary>
        public bool Fail()
        {
            if (HasFailed)
            {
                return false;
            }

            var target = ActiveIndex;
            if (target < 0)
            {
                target = NextPending(0);
            }

            if (target < 0)
            {
                return false;
            }

            Set(target, TopiaForgeStepStatus.Failed);
            Version++;
            return true;
        }

        /// <summary>Marks a pending step skipped; any other status refuses.</summary>
        public bool Skip(int index)
        {
            if (statuses[CheckIndex(index)] != TopiaForgeStepStatus.Pending)
            {
                return false;
            }

            Set(index, TopiaForgeStepStatus.Skipped);
            Version++;
            return true;
        }

        /// <summary>Finishes every pending or active step. Refused after a failure or when already complete.</summary>
        public bool Complete()
        {
            if (HasFailed || IsComplete)
            {
                return false;
            }

            for (var index = 0; index < statuses.Length; index++)
            {
                if (statuses[index] == TopiaForgeStepStatus.Pending || statuses[index] == TopiaForgeStepStatus.Active)
                {
                    statuses[index] = TopiaForgeStepStatus.Done;
                }
            }

            Version++;
            return true;
        }

        /// <summary>Returns every step to pending and clears all progress.</summary>
        public bool Reset()
        {
            var changed = false;
            for (var index = 0; index < statuses.Length; index++)
            {
                if (statuses[index] != TopiaForgeStepStatus.Pending || progressTotal[index] != 0 || progressCurrent[index] != 0)
                {
                    statuses[index] = TopiaForgeStepStatus.Pending;
                    progressCurrent[index] = 0;
                    progressTotal[index] = 0;
                    changed = true;
                }
            }

            if (changed)
            {
                Version++;
            }

            return changed;
        }

        /// <summary>
        /// Sets a step's progress ("3/7"). A non-positive total clears it; the current value clamps
        /// to [0, total].
        /// </summary>
        public bool SetProgress(int index, int current, int total)
        {
            CheckIndex(index);
            if (total <= 0)
            {
                total = 0;
                current = 0;
            }
            else
            {
                current = current < 0 ? 0 : current > total ? total : current;
            }

            if (progressCurrent[index] == current && progressTotal[index] == total)
            {
                return false;
            }

            progressCurrent[index] = current;
            progressTotal[index] = total;
            Version++;
            return true;
        }

        private void Set(int index, TopiaForgeStepStatus status)
        {
            statuses[index] = status;
        }

        private int NextPending(int from)
        {
            for (var index = from; index < statuses.Length; index++)
            {
                if (statuses[index] == TopiaForgeStepStatus.Pending)
                {
                    return index;
                }
            }

            return -1;
        }

        private int IndexOf(TopiaForgeStepStatus status)
        {
            for (var index = 0; index < statuses.Length; index++)
            {
                if (statuses[index] == status)
                {
                    return index;
                }
            }

            return -1;
        }

        private int CheckIndex(int index)
        {
            if (index < 0 || index >= statuses.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index), "Step index " + index + " is outside the stepper.");
            }

            return index;
        }
    }
}
