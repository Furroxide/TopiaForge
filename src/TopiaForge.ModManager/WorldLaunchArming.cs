using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// Decides what a process start should launch into: the launcher's command, the manager's own
    /// remembered selection, or nothing.
    /// <para>
    /// Two parties can ask for a gamemode and they must not be conflated. The manager remembers a
    /// selection edited from its in-game overlay, which is what should happen when someone starts
    /// Robotopia directly. The launcher issues a one-shot command per run, which is what should
    /// happen when it starts the game. Treating "the launcher said play normally" as "the launcher
    /// said nothing" is what let a remembered autoload override an explicit None -- the launcher, the
    /// CLI and the docs all promised an ordinary boot while the manager started a gamemode anyway.
    /// </para>
    /// <para>
    /// Unity-free and side-effect-free, so the precedence is unit-tested rather than only observable
    /// by launching a game.
    /// </para>
    /// </summary>
    internal static class WorldLaunchArming
    {
        /// <param name="profile">
        /// The consumed one-shot launch profile, or null when the game was started without the
        /// launcher. Null is the only case that leaves the manager's own default in charge.
        /// </param>
        /// <param name="remembered">The manager's durable selection, edited from the overlay.</param>
        /// <returns>The intent to arm, or null to boot normally.</returns>
        public static WorldLaunchIntent? Resolve(
            ProfileLaunchConfiguration? profile,
            WorldLaunchSettings? remembered)
        {
            if (profile != null)
            {
                var commanded = profile.WorldLaunch;
                if (commanded != null)
                {
                    return commanded.IsMainMenu ? null : commanded;
                }

                // A profile from a launcher that predates the command. It cannot have asked for a
                // gamemode, but it also never asked to suppress the remembered one, so fall through
                // rather than inventing an intention it never expressed.
            }

            if (remembered == null
                || !remembered.AutoLoadOnStart
                || string.IsNullOrEmpty(remembered.SelectedGamemodeId))
            {
                return null;
            }

            return new WorldLaunchIntent
            {
                Command = WorldLaunchIntent.LaunchTargetCommand,
                WorldId = remembered.SelectedWorldId ?? string.Empty,
                GamemodeId = remembered.SelectedGamemodeId,
                LoadMode = WorldLaunchSettings.NormalizeLoadMode(remembered.LoadMode),
                AllowAdditiveFallback = remembered.AllowAdditiveFallback
            };
        }
    }
}
