namespace TopiaForge.Mods.UnityUi
{
    /// <summary>What the kit does to one of the game UI module's pointer actions in a frame.</summary>
    internal enum TopiaForgePointerStep : byte
    {
        /// <summary>Nothing.</summary>
        None,

        /// <summary>Turn the action on: a TopiaForge surface shows the cursor and the game left the action off.</summary>
        Enable,

        /// <summary>Turn the action off again: the kit turned it on, nothing changed it since, and no surface needs it now.</summary>
        Disable,
    }

    /// <summary>
    /// One of the game UI module's pointer actions (the pointer's position, a button, the wheel) while TopiaForge surfaces
    /// show the cursor, Unity-free. Robotopia turns its whole menu map off as its pause menu closes, pointer actions and
    /// all, so no click reached a TopiaForge surface after any pause. While a surface shows the cursor, the kit turns the
    /// action back on if the game left it off, and turns it off again once the last such surface closes: the game's state
    /// returns exactly. Only what the kit turned on is turned off, and only while nothing else changed it: once the game
    /// turns the action or its map on or off itself, as its pause menu does, the game's state stands, and the kit takes the
    /// action afresh only if it is off while a surface still needs it. Where the game leaves the action on, as at its menus,
    /// the kit changes nothing.
    /// </summary>
    internal sealed class TopiaForgePointerHold
    {
        private bool held;

        /// <summary>Gets whether the kit turned the action on and nothing changed it since.</summary>
        public bool Held => held;

        /// <summary>
        /// One frame. <paramref name="wanted"/> is whether a TopiaForge surface shows the cursor, <paramref name="present"/>
        /// whether the module has the action, and <paramref name="enabled"/> whether the action is on now.
        /// </summary>
        public TopiaForgePointerStep Tick(bool wanted, bool present, bool enabled)
        {
            if (!present)
            {
                held = false;
                return TopiaForgePointerStep.None;
            }

            if (wanted)
            {
                if (enabled)
                {
                    return TopiaForgePointerStep.None;
                }

                held = true;
                return TopiaForgePointerStep.Enable;
            }

            if (!held)
            {
                return TopiaForgePointerStep.None;
            }

            held = false;
            return enabled ? TopiaForgePointerStep.Disable : TopiaForgePointerStep.None;
        }

        /// <summary>The game turned the action or its map on or off itself: its state stands, and nothing is turned off later.</summary>
        public void Superseded()
        {
            held = false;
        }

        /// <summary>
        /// Lets go of the action, as when the module replaces it or the kit stops: returns whether to turn it off, because the
        /// kit turned it on, nothing changed it since, and it is still on.
        /// </summary>
        public bool Release(bool enabled)
        {
            var restore = held && enabled;
            held = false;
            return restore;
        }
    }
}
