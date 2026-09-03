using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace TopiaForge.ModManager.Core
{
    /// <summary>
    /// "Start this gamemode for this run" — a one-shot instruction from the launcher, carried on the
    /// launch profile and consumed once by the manager.
    /// <para>
    /// This exists because the previous channel did not work at all. The launcher used to write the
    /// selection into the Worlds mod's own config file, but that file is a
    /// <c>{"schemaVersion":N,"value":{...}}</c> envelope owned by the mod, and the launcher wrote its
    /// keys flat at the top level. The mod read <c>value</c>, never saw them, and rewrote the document
    /// on its next save — deleting them. Every gamemode the player chose was silently discarded, and
    /// the game booted to its ordinary menu.
    /// </para>
    /// <para>
    /// The launch profile is the right home for it: already validated, already scoped to the manager's
    /// staging directory, already consumed exactly once and deleted. Absence of an intent means "boot
    /// normally", so a profile that carries none behaves exactly as before.
    /// </para>
    /// </summary>
    [DataContract]
    public sealed class WorldLaunchIntent
    {
        [DataMember(Name = "worldId")]
        public string WorldId { get; set; } = string.Empty;

        [DataMember(Name = "gamemodeId")]
        public string GamemodeId { get; set; } = string.Empty;

        [DataMember(Name = "loadMode")]
        public string LoadMode { get; set; } = WorldLaunchSettings.AdditiveArena;

        [DataMember(Name = "allowAdditiveFallback")]
        public bool AllowAdditiveFallback { get; set; } = true;

        public bool PreferSceneReplacement => LoadMode == WorldLaunchSettings.SceneReplacement;

        /// <summary>
        /// The gamemode is the whole point of the intent, so it is required. The world is optional:
        /// when it is empty the manager falls back to the gamemode's own registered menu entry, which
        /// knows the world its author intended.
        /// </summary>
        public IReadOnlyList<string> Validate()
        {
            var errors = new List<string>();
            if (!ManifestValidator.IsValidId(GamemodeId))
            {
                errors.Add("worldLaunch.gamemodeId must be a valid TopiaForge id.");
            }

            if (!string.IsNullOrEmpty(WorldId) && !ManifestValidator.IsValidId(WorldId))
            {
                errors.Add("worldLaunch.worldId must be a valid TopiaForge id when present.");
            }

            if (LoadMode != WorldLaunchSettings.AdditiveArena
                && LoadMode != WorldLaunchSettings.SceneReplacement)
            {
                errors.Add("worldLaunch.loadMode must be "
                    + WorldLaunchSettings.AdditiveArena + " or " + WorldLaunchSettings.SceneReplacement + ".");
            }

            return errors;
        }

        // DataContractJsonSerializer builds instances with GetUninitializedObject, bypassing the
        // constructor and property initializers, so absent members would arrive as null.
        [OnDeserializing]
        private void OnDeserializing(StreamingContext context)
        {
            WorldId = string.Empty;
            GamemodeId = string.Empty;
            LoadMode = WorldLaunchSettings.AdditiveArena;
            AllowAdditiveFallback = true;
        }
    }
}
