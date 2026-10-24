using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine.InputSystem;

namespace TopiaForge.ModManager.Voice
{
    /// <summary>
    /// The game's own robot recording, as the runtime's microphone arbiter reads it whenever a use of the microphone is
    /// decided: from the push-to-record key going down, when the game starts its microphone, until it has decoded what it
    /// heard and ended its microphone. A game update that moves this state makes the read throw, which the arbiter counts
    /// as recording, so voice chat and robot conversations keep off the microphone. Main thread only.
    /// </summary>
    internal static class UnityVoiceNativeRecording
    {
        /// <summary>Reads whether the game records now; it throws when the game's state cannot be read.</summary>
        internal static bool Read() => ReadState();

        // Kept out of line, so a game whose state moved fails here, inside the arbiter's guard, and not where it is called.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool ReadState() => UIState.Recording.Value || UIState.IsDecodingVoice.Value;
    }

    /// <summary>
    /// The game's bindings, as push-to-talk's census reads them: the effective path of every binding in every action map
    /// of each of the game's <c>PlayerInput</c> components, overrides included. A composite's own entry names no control and
    /// is skipped; its parts are read. With no player, as at the main menu, there is nothing to read. Main thread only.
    /// </summary>
    internal sealed class UnityVoiceNativeBindings : IVoiceNativeBindings
    {
        public bool TryRead(List<string> into)
        {
            var players = PlayerInput.all;
            var read = false;
            for (var index = 0; index < players.Count; index++)
            {
                var player = players[index];
                var asset = player == null ? null : player.actions;
                if (asset == null) continue;
                var maps = asset.actionMaps;
                for (var map = 0; map < maps.Count; map++)
                {
                    var bindings = maps[map].bindings;
                    for (var binding = 0; binding < bindings.Count; binding++)
                    {
                        var entry = bindings[binding];
                        if (entry.isComposite) continue;
                        var path = entry.effectivePath;
                        if (!string.IsNullOrEmpty(path)) into.Add(path);
                    }

                    read = true;
                }
            }

            return read;
        }
    }
}
