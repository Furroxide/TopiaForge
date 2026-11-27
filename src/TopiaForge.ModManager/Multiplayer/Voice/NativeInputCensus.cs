using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Voice
{
    /// <summary>
    /// The keys and mouse buttons the game's own <c>PlayerInput</c> binds, read from the effective path of every binding of
    /// its actions (<c>&lt;Keyboard&gt;/capsLock</c>, <c>&lt;Mouse&gt;/forwardButton</c>), so push-to-talk never takes a
    /// control the game acts on. Gamepad, touch, usage and composite paths name no key and are skipped. When the game's
    /// bindings cannot be read, as before its player exists or if a game update moves them, <see cref="Fallback"/> stands
    /// in: the controls the game is known to use and the ones a game of its kind binds. Immutable and Unity-free.
    /// </summary>
    internal sealed class NativeInputCensus
    {
        /// <summary>The census used until the game's own bindings can be read.</summary>
        internal static readonly NativeInputCensus Fallback = new NativeInputCensus(false, new[]
        {
            "<Keyboard>/v", "<Keyboard>/b", "<Keyboard>/q", "<Keyboard>/e", "<Keyboard>/z", "<Keyboard>/w", "<Keyboard>/a",
            "<Keyboard>/s", "<Keyboard>/d", "<Keyboard>/space", "<Keyboard>/shift", "<Keyboard>/ctrl", "<Keyboard>/tab",
            "<Keyboard>/escape", "<Keyboard>/enter", "<Mouse>/leftButton", "<Mouse>/rightButton"
        });

        private readonly HashSet<string> used = new HashSet<string>(StringComparer.Ordinal);

        private NativeInputCensus(bool fromGame, IEnumerable<string?> paths)
        {
            IsFromGame = fromGame;
            var parsed = new List<VoiceControl>(2);
            foreach (var path in paths)
            {
                parsed.Clear();
                if (!TryParsePath(path, parsed)) continue;
                foreach (var control in parsed) used.Add(control.Stored);
            }
        }

        /// <summary>Gets whether the census was read from the game's own bindings rather than standing in for them.</summary>
        internal bool IsFromGame { get; }

        /// <summary>Gets how many keys and mouse buttons the game binds.</summary>
        internal int Count => used.Count;

        /// <summary>Takes the census of the game's bindings, from the effective path of each; null and empty paths are skipped.</summary>
        internal static NativeInputCensus FromPaths(IEnumerable<string?> paths) =>
            new NativeInputCensus(true, paths ?? throw new ArgumentNullException(nameof(paths)));

        /// <summary>Gets whether the game binds <paramref name="binding"/>.</summary>
        internal bool Uses(InputBinding binding) =>
            VoiceControls.TryFind(binding, out var control) && used.Contains(control.Stored);

        /// <summary>Gets whether <paramref name="other"/> came from the same source and names the same controls.</summary>
        internal bool SameAs(NativeInputCensus? other) =>
            other != null && (ReferenceEquals(this, other) || (IsFromGame == other.IsFromGame && used.SetEquals(other.used)));

        /// <summary>
        /// Reads the keys or mouse buttons one Input System binding path names into <paramref name="into"/>: one for most,
        /// both sides for the plain modifiers (<c>shift</c>, <c>ctrl</c>, <c>alt</c>), and none for a path that names no key
        /// or button this game's push-to-talk knows. Layouts and control names compare without case, as the Input System's
        /// do; a device usage in braces, as in <c>&lt;Keyboard&gt;{Main}/v</c>, is ignored.
        /// </summary>
        internal static bool TryParsePath(string? path, List<VoiceControl> into)
        {
            if (into == null) throw new ArgumentNullException(nameof(into));
            if (string.IsNullOrEmpty(path) || path[0] != '<') return false;
            var close = path.IndexOf('>');
            if (close < 2) return false;
            var layout = path.Substring(1, close - 1).ToLowerInvariant();
            var slash = path.IndexOf('/', close);
            if (slash < 0 || slash == path.Length - 1) return false;
            var name = path.Substring(slash + 1).ToLowerInvariant();

            // Only a direct child names one control; a deeper path, a character match (#(a)) or a wildcard names none.
            if (name.IndexOf('/') >= 0 || name.IndexOf('#') >= 0 || name.IndexOf('*') >= 0) return false;
            var before = into.Count;
            switch (layout)
            {
                case "keyboard":
                    switch (name)
                    {
                        case "shift":
                            AddKey(into, "leftshift");
                            AddKey(into, "rightshift");
                            break;
                        case "ctrl":
                            AddKey(into, "leftctrl");
                            AddKey(into, "rightctrl");
                            break;
                        case "alt":
                            AddKey(into, "leftalt");
                            AddKey(into, "rightalt");
                            break;
                        default:
                            AddKey(into, name);
                            break;
                    }

                    break;
                case "mouse":
                    AddMouse(into, name);
                    break;
                case "pointer":
                    // A pointer's press is the mouse's primary button on a desktop.
                    if (name == "press") AddMouse(into, "leftbutton");
                    break;
            }

            return into.Count > before;
        }

        private static void AddKey(List<VoiceControl> into, string pathName)
        {
            foreach (var control in VoiceControls.All)
            {
                if (!control.IsMouseButton && string.Equals(control.PathName, pathName, StringComparison.Ordinal))
                {
                    into.Add(control);
                    return;
                }
            }
        }

        private static void AddMouse(List<VoiceControl> into, string pathName)
        {
            foreach (var control in VoiceControls.All)
            {
                if (control.IsMouseButton && string.Equals(control.PathName, pathName, StringComparison.Ordinal))
                {
                    into.Add(control);
                    return;
                }
            }
        }
    }
}
