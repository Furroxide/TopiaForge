namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// One press of Back, however many ways it reaches the kit. Unity's legacy input and the Input System report the same
    /// Escape, sometimes a frame apart, so Back counts once, when it is first held or reported, and again only after every
    /// way of reading it has let go. Unity-free.
    /// </summary>
    internal sealed class TopiaForgeBackPress
    {
        private bool held;

        /// <summary>Takes this frame's readings; returns whether a new Back press starts in it.</summary>
        public bool Tick(bool heldNow, bool pressedThisFrame)
        {
            var pressed = !held && (heldNow || pressedThisFrame);
            held = heldNow;
            return pressed;
        }

        /// <summary>Forgets a held Back.</summary>
        public void Reset()
        {
            held = false;
        }
    }

    /// <summary>A navigation move, as Unity's UI events name them.</summary>
    internal enum TopiaForgeMoveDirection : byte
    {
        /// <summary>No move.</summary>
        None,

        /// <summary>Left.</summary>
        Left,

        /// <summary>Up.</summary>
        Up,

        /// <summary>Right.</summary>
        Right,

        /// <summary>Down.</summary>
        Down,
    }

    /// <summary>What the kit's navigation does in one frame: select the surface's first control, or move and submit.</summary>
    internal readonly struct TopiaForgeNavigationStep
    {
        /// <summary>Creates a step.</summary>
        public TopiaForgeNavigationStep(bool selectFirst, TopiaForgeMoveDirection move, bool submit)
        {
            SelectFirst = selectFirst;
            Move = move;
            Submit = submit;
        }

        /// <summary>Gets whether to select the surface's first control, since none of its controls is selected.</summary>
        public bool SelectFirst { get; }

        /// <summary>Gets the move to send the selected control.</summary>
        public TopiaForgeMoveDirection Move { get; }

        /// <summary>Gets whether to send the selected control a submit.</summary>
        public bool Submit { get; }

        /// <summary>Gets whether the step does anything.</summary>
        public bool IsEmpty => !SelectFirst && Move == TopiaForgeMoveDirection.None && !Submit;
    }

    /// <summary>
    /// The kit's own navigation while a surface takes it from the game, Unity-free. Unity's UI module reads the Input System
    /// actions the game reads, so it sees none of the presses TopiaForge takes: this decides what the module would have sent
    /// the selection. A direction moves at once, again after the module's repeat delay and then at its repeat rate while it
    /// is held; Enter, Space or a controller's A submits. The first press that finds none of the surface's controls selected
    /// selects its first control instead. A text field with the keyboard keeps its own keys, as the module leaves them to it.
    /// Nothing is sent once the claim ends, and a direction already held when a claim begins moves nothing until it is
    /// released, so a key held across surfaces never moves the next one.
    /// </summary>
    internal sealed class TopiaForgeNavigationDriver
    {
        /// <summary>A direction's vector must reach this length to move, as a taken stick press does.</summary>
        public const float MoveThreshold = TopiaForgeInputGate.PressThreshold;

        private TopiaForgeMoveDirection repeating;
        private TopiaForgeMoveDirection stale;
        private float wait;
        private bool claimed;
        private object? surface;

        /// <summary>
        /// One frame. <paramref name="claimedNow"/> is whether the topmost surface takes navigation;
        /// <paramref name="selectionInSurface"/> whether the EventSystem selection is one of its controls, and
        /// <paramref name="typing"/> whether that control is a text field with the keyboard. <paramref name="x"/>,
        /// <paramref name="y"/> and <paramref name="submitPressed"/> are the presses TopiaForge took; the delay and rate
        /// are the UI module's own. <paramref name="surface"/> identifies the current surface, so switching directly
        /// between two surfaces does not carry a held direction or the old surface's submit into the new one.
        /// </summary>
        public TopiaForgeNavigationStep Tick(
            bool claimedNow,
            bool selectionInSurface,
            bool typing,
            float x,
            float y,
            bool submitPressed,
            float deltaSeconds,
            float repeatDelay,
            float repeatRate,
            object? surface = null)
        {
            var direction = DirectionOf(x, y);
            if (!claimedNow)
            {
                claimed = false;
                this.surface = null;
                repeating = TopiaForgeMoveDirection.None;
                return default;
            }

            if (claimed && !ReferenceEquals(this.surface, surface))
            {
                // Back can uncover another surface before this frame's navigation, with no unclaimed frame between.
                claimed = false;
                submitPressed = false;
            }

            this.surface = surface;
            if (!claimed)
            {
                // A claim begins: whatever direction is held now was pressed before it, for something else.
                claimed = true;
                stale = direction;
                repeating = TopiaForgeMoveDirection.None;
            }

            if (stale != TopiaForgeMoveDirection.None)
            {
                if (direction != TopiaForgeMoveDirection.None)
                {
                    direction = TopiaForgeMoveDirection.None;
                }
                else
                {
                    stale = TopiaForgeMoveDirection.None;
                }
            }

            var move = Repeat(direction, deltaSeconds, repeatDelay, repeatRate);
            if (move == TopiaForgeMoveDirection.None && !submitPressed)
            {
                return default;
            }

            if (!selectionInSurface)
            {
                return new TopiaForgeNavigationStep(true, TopiaForgeMoveDirection.None, false);
            }

            return typing ? default : new TopiaForgeNavigationStep(false, move, submitPressed);
        }

        /// <summary>Gets the move a direction vector makes: none inside the threshold, else along its longer axis.</summary>
        public static TopiaForgeMoveDirection DirectionOf(float x, float y)
        {
            if ((x * x) + (y * y) < MoveThreshold * MoveThreshold)
            {
                return TopiaForgeMoveDirection.None;
            }

            if (System.Math.Abs(x) > System.Math.Abs(y))
            {
                return x > 0f ? TopiaForgeMoveDirection.Right : TopiaForgeMoveDirection.Left;
            }

            return y > 0f ? TopiaForgeMoveDirection.Up : TopiaForgeMoveDirection.Down;
        }

        private TopiaForgeMoveDirection Repeat(TopiaForgeMoveDirection direction, float deltaSeconds, float repeatDelay, float repeatRate)
        {
            if (direction == TopiaForgeMoveDirection.None)
            {
                repeating = TopiaForgeMoveDirection.None;
                return TopiaForgeMoveDirection.None;
            }

            if (direction != repeating)
            {
                repeating = direction;
                wait = repeatDelay;
                return direction;
            }

            wait -= deltaSeconds;
            if (wait > 0f)
            {
                return TopiaForgeMoveDirection.None;
            }

            wait = repeatRate;
            return direction;
        }
    }
}
