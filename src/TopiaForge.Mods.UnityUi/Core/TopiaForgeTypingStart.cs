namespace TopiaForge.Mods.UnityUi
{
    /// <summary>What a kit text field does in one frame about starting to take the keyboard.</summary>
    internal enum TopiaForgeTypingStep : byte
    {
        /// <summary>Nothing waits: the field's own frame runs as it always does.</summary>
        Pass,

        /// <summary>A start waits for held keys: the field neither starts nor lets a start of its own run.</summary>
        Wait,

        /// <summary>The keys a start waited for are released and the field still has the selection: start it now.</summary>
        Start,
    }

    /// <summary>
    /// When a kit text field starts taking the keyboard, Unity-free. A field asked to start while any key is held, as it is
    /// reached by navigation, selected as its surface opens, clicked or submitted, waits until every key is released: a key
    /// already down as the field is reached types nothing, its auto-repeat included, until it is pressed again. A field asked
    /// with no key held starts at once, as it always did. A start asked by a selection or a click is dropped if the field
    /// lost the selection meanwhile, so a move that only passed over the field, or one TopiaForge undid, never leaves it
    /// typing; one the field marked itself, as a submit does, runs once the keys are released, as it would have at once.
    /// </summary>
    internal sealed class TopiaForgeTypingStart
    {
        private bool waiting;
        private bool fieldPending;

        /// <summary>Gets whether a start asked while keys were held still waits for them.</summary>
        public bool Waiting => waiting;

        /// <summary>
        /// Asks the field to start, as it is selected or clicked. Returns whether it may start at once: only with no key held
        /// and no earlier start still waiting.
        /// </summary>
        public bool Ask(bool keysHeld)
        {
            if (keysHeld)
            {
                waiting = true;
            }

            return !waiting;
        }

        /// <summary>
        /// Asks the field to start as it is submitted while it does not have the keyboard: the field marks a start of its own
        /// that cannot be withdrawn, which waits with this one while keys are held.
        /// </summary>
        public void Submitted(bool keysHeld)
        {
            if (keysHeld)
            {
                waiting = true;
                fieldPending = true;
            }
        }

        /// <summary>
        /// One frame, before the field's own late update. <paramref name="focused"/> is whether the field has the keyboard,
        /// <paramref name="selected"/> whether it has the EventSystem selection.
        /// </summary>
        public TopiaForgeTypingStep Tick(bool keysHeld, bool selected, bool focused)
        {
            if (!waiting)
            {
                return TopiaForgeTypingStep.Pass;
            }

            if (focused)
            {
                // Started some other way meanwhile: nothing waits any more.
                waiting = false;
                fieldPending = false;
                return TopiaForgeTypingStep.Pass;
            }

            if (keysHeld)
            {
                return fieldPending ? TopiaForgeTypingStep.Wait : TopiaForgeTypingStep.Pass;
            }

            waiting = false;
            fieldPending = false;
            return selected ? TopiaForgeTypingStep.Start : TopiaForgeTypingStep.Pass;
        }

        /// <summary>Forgets a start that waits, as when the field is disabled.</summary>
        public void Reset()
        {
            waiting = false;
            fieldPending = false;
        }
    }
}
