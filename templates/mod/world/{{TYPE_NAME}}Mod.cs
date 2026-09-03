using TopiaForge.Mods;

namespace {{ASSEMBLY_NAME}}
{
    /// <summary>
    /// A world mod. The world itself is declared in <c>topiaforge.mod.json</c> under
    /// <c>contributions.worlds</c>, so there is nothing to register here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The manifest names the bundle, the prefab, the transitions the world supports and where the
    /// player spawns. That is the whole world, and it is readable before any code runs -- which is
    /// what lets the launcher list it, and lets a broken declaration be reported to you instead of
    /// discovered by a player.
    /// </para>
    /// <para>
    /// The launch target names Free Play, the neutral gamemode the Worlds provider itself implements.
    /// A world mod therefore needs no dependency beyond Worlds: it does not have to borrow gameplay
    /// from some other package just to be playable.
    /// </para>
    /// <para>
    /// This entry point is left for anything the mod wants to do besides ship a world -- settings,
    /// commands, or content that reacts to the world being played.
    /// </para>
    /// </remarks>
    public sealed class {{TYPE_NAME}}Mod : TopiaForgeMod
    {
        protected override void OnLoad()
        {
            Context.Logger.Info("{{DISPLAY_NAME}} loaded.");
        }
    }
}
