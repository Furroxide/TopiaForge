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
    /// staging directory, already consumed exactly once and deleted.
    /// </para>
    /// <para>
    /// The instruction is always explicit. "Play normally" is a command in its own right
    /// (<see cref="MainMenuCommand"/>), not the absence of one, because the manager also has a
    /// remembered selection of its own and the two need to be distinguishable: starting the game
    /// directly should honour that memory, while a launcher that asked for the ordinary menu must
    /// override it. Only a launch with no profile at all leaves the manager's own default in charge.
    /// </para>
    /// </summary>
    [DataContract]
    public sealed class WorldLaunchIntent
    {
        /// <summary>Start <see cref="GamemodeId"/> for this run.</summary>
        public const string LaunchTargetCommand = "launch-target";

        /// <summary>Boot to the game's ordinary menu, whatever the manager remembers.</summary>
        public const string MainMenuCommand = "main-menu";

        [DataMember(Name = "command")]
        public string Command { get; set; } = LaunchTargetCommand;

        public bool IsMainMenu => Command == MainMenuCommand;

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
        /// A launch target needs its gamemode -- that is the whole point of the intent -- and must not
        /// name a world it cannot use. The world itself is optional: when it is empty the manager falls
        /// back to the gamemode's own registered menu entry, which knows the world its author intended.
        /// A main-menu command carries no target at all, so requiring one would reject every ordinary
        /// launch.
        /// </summary>
        public IReadOnlyList<string> Validate()
        {
            var errors = new List<string>();
            if (Command != LaunchTargetCommand && Command != MainMenuCommand)
            {
                errors.Add("worldLaunch.command must be "
                    + LaunchTargetCommand + " or " + MainMenuCommand + ".");
                return errors;
            }

            if (IsMainMenu)
            {
                if (GamemodeId.Length > 0)
                {
                    errors.Add("worldLaunch.gamemodeId must be empty for a "
                        + MainMenuCommand + " command.");
                }

                return errors;
            }

            // Declaration ids, not package ids: a launch target is namespaced under its package, so
            // it uses the wider 96-character grammar. Validating at the package width here would
            // reject a target the manifest contract calls legal.
            if (!ManifestContributionValidator.IsValidDeclarationId(GamemodeId))
            {
                errors.Add("worldLaunch.gamemodeId must be a valid TopiaForge declaration id.");
            }

            if (!string.IsNullOrEmpty(WorldId)
                && !ManifestContributionValidator.IsValidDeclarationId(WorldId))
            {
                errors.Add("worldLaunch.worldId must be a valid TopiaForge declaration id when present.");
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
            Command = LaunchTargetCommand;
            WorldId = string.Empty;
            GamemodeId = string.Empty;
            LoadMode = WorldLaunchSettings.AdditiveArena;
            AllowAdditiveFallback = true;
        }
    }
}
