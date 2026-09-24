using TopiaForge.Mods;

namespace {{ASSEMBLY_NAME}}
{
    /// <summary>Loads package services; the manifest owns world content and launch declarations.</summary>
    public sealed class {{TYPE_NAME}}Mod : TopiaForgeMod
    {
        protected override void OnLoad() => Context.Logger.Info("{{DISPLAY_NAME}} world package loaded.");
    }
}
