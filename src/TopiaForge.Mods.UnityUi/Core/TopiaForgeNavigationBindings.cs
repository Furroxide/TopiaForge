using System;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// Which keys and sticks navigate, Unity-free, by Input System key number. The kit's own keys always do: Escape is Back,
    /// the arrows move, Enter, the keypad's Enter and Space submit, and Tab is Tab. The game's navigate action, which Unity's
    /// UI module and Robotopia's menus both read, binds more: W, A, S and D as well as the arrows, and it may bind the right
    /// stick as well as the left. While that action listens, each key it binds moves the way its binding points, so a
    /// surface that claims navigation takes it from the game as it takes the arrows, and so does the right stick. While it
    /// does not, as in a level, those keys stay gameplay's unless a keyboard claim takes them.
    /// </summary>
    internal sealed class TopiaForgeNavigationBindings
    {
        /// <summary>Space's key number.</summary>
        public const int Space = 1;

        /// <summary>Enter's key number.</summary>
        public const int Enter = 2;

        /// <summary>Tab's key number.</summary>
        public const int Tab = 3;

        /// <summary>Escape's key number.</summary>
        public const int Escape = 60;

        /// <summary>The left arrow's key number.</summary>
        public const int LeftArrow = 61;

        /// <summary>The right arrow's key number.</summary>
        public const int RightArrow = 62;

        /// <summary>The up arrow's key number.</summary>
        public const int UpArrow = 63;

        /// <summary>The down arrow's key number.</summary>
        public const int DownArrow = 64;

        /// <summary>The keypad's Enter key number.</summary>
        public const int NumpadEnter = 77;

        /// <summary>The most keys of the game's navigate action followed; a key past them stays the game's.</summary>
        public const int MaxKeys = 16;

        private readonly int[] slots = new int[TopiaForgeInputGate.ControlsPerDevice];
        private readonly int[] keys = new int[MaxKeys];
        private readonly float[] directionX = new float[MaxKeys];
        private readonly float[] directionY = new float[MaxKeys];
        private int count;
        private bool rightStick;

        /// <summary>Gets or sets whether the game's navigate action is enabled, so its bindings move its menus.</summary>
        public bool Listening { get; set; }

        /// <summary>Gets how many keys of the game's navigate action move its menus now: none while it does not listen.</summary>
        public int KeyCount => Listening ? count : 0;

        /// <summary>Gets whether the right stick moves the game's menus now.</summary>
        public bool RightStickMoves => Listening && rightStick;

        /// <summary>Gets the part one of the kit's own keys plays; every other key is <see cref="TopiaForgeInputRole.Key"/>.</summary>
        public static TopiaForgeInputRole KitRole(int key)
        {
            switch (key)
            {
                case Escape:
                    return TopiaForgeInputRole.Back;
                case UpArrow:
                case DownArrow:
                case LeftArrow:
                case RightArrow:
                    return TopiaForgeInputRole.Direction;
                case Enter:
                case NumpadEnter:
                case Space:
                    return TopiaForgeInputRole.Submit;
                case Tab:
                    return TopiaForgeInputRole.Tab;
                default:
                    return TopiaForgeInputRole.Key;
            }
        }

        /// <summary>Gets the key at <paramref name="index"/>, below <see cref="KeyCount"/>.</summary>
        public int KeyAt(int index) => keys[index];

        /// <summary>Gets whether <paramref name="key"/> moves the game's menus now.</summary>
        public bool Moves(int key) => Listening && key >= 0 && key < slots.Length && slots[key] != 0;

        /// <summary>Gets the part <paramref name="key"/> plays now: its own as one of the kit's keys, else a direction while it moves the game's menus.</summary>
        public TopiaForgeInputRole RoleOf(int key)
        {
            var role = KitRole(key);
            return role == TopiaForgeInputRole.Key && Moves(key) ? TopiaForgeInputRole.Direction : role;
        }

        /// <summary>Gets the way <paramref name="key"/> moves, each axis -1, 0 or 1: an arrow's own, or its binding's while it moves the game's menus.</summary>
        public void Direction(int key, out float x, out float y)
        {
            var slot = Moves(key) ? slots[key] - 1 : -1;
            x = key == LeftArrow ? -1f : key == RightArrow ? 1f : slot >= 0 ? directionX[slot] : 0f;
            y = key == DownArrow ? -1f : key == UpArrow ? 1f : slot >= 0 ? directionY[slot] : 0f;
        }

        /// <summary>
        /// Follows a key the game's navigate action binds. <paramref name="part"/> is its part of a two-dimensional composite
        /// (up, down, left or right); a key bound without one is still the action's, so it is taken, but moves nowhere. One of
        /// the kit's own keys, a key already followed, an unknown key, or one past <see cref="MaxKeys"/> changes nothing.
        /// </summary>
        public void BindKey(int key, string? part)
        {
            if (key <= 0 || key >= slots.Length || slots[key] != 0 || count == MaxKeys || KitRole(key) != TopiaForgeInputRole.Key)
            {
                return;
            }

            keys[count] = key;
            directionX[count] = Is(part, "left") ? -1f : Is(part, "right") ? 1f : 0f;
            directionY[count] = Is(part, "down") ? -1f : Is(part, "up") ? 1f : 0f;
            slots[key] = ++count;
        }

        /// <summary>Follows the right stick, which the game's navigate action binds.</summary>
        public void BindRightStick()
        {
            rightStick = true;
        }

        /// <summary>Forgets every key and the right stick, before the action's controls are read again.</summary>
        public void Clear()
        {
            for (var index = 0; index < count; index++)
            {
                slots[keys[index]] = 0;
            }

            count = 0;
            rightStick = false;
        }

        private static bool Is(string? part, string name)
        {
            return string.Equals(part, name, StringComparison.OrdinalIgnoreCase);
        }
    }
}
