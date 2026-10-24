namespace TopiaForge.Mods.UnityUi
{
    /// <summary>What the kit does about its own Unity event system after a check.</summary>
    internal enum TopiaForgeEventSystemAction
    {
        /// <summary>Leave everything as it is.</summary>
        None,

        /// <summary>Make the kit's own event system: a kit canvas takes input and no event system is active.</summary>
        CreateOwn,

        /// <summary>Remove the kit's own event system: the game's, or another mod's, is active.</summary>
        RetireOwn,
    }

    /// <summary>
    /// When the kit makes or removes its own event system. Unity runs only the first event system enabled, so a passive
    /// HUD must not install one before the game's first scene. The game's event system always comes first:
    /// the kit makes its own only while one of its canvases takes input and no event
    /// system is active, and removes it as soon as another is active. It never touches an event system it did not make.
    /// A canvas that takes no input, such as a HUD or a toast, needs none. Unity-free and unit-tested.
    /// </summary>
    internal static class TopiaForgeEventSystemPolicy
    {
        /// <summary>Decides what to do now.</summary>
        /// <param name="kitTakesInput">Whether a kit canvas with an enabled raycaster is active.</param>
        /// <param name="kitOwnsOne">Whether the kit's own event system exists.</param>
        /// <param name="otherIsActive">Whether an event system the kit did not make is active.</param>
        public static TopiaForgeEventSystemAction Decide(bool kitTakesInput, bool kitOwnsOne, bool otherIsActive)
        {
            if (kitOwnsOne)
            {
                return otherIsActive ? TopiaForgeEventSystemAction.RetireOwn : TopiaForgeEventSystemAction.None;
            }

            return kitTakesInput && !otherIsActive ? TopiaForgeEventSystemAction.CreateOwn : TopiaForgeEventSystemAction.None;
        }
    }
}
