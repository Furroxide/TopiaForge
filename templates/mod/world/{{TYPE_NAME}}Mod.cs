using System;
using TopiaForge.Mods;

namespace {{ASSEMBLY_NAME}}
{
    /// <summary>Registers a bundle-backed custom world using only safe SDK contracts.</summary>
    public sealed class {{TYPE_NAME}}Mod : TopiaForgeMod
    {
        private const string WorldId = "{{MOD_ID}}.world";

        protected override void OnLoad()
        {
            var worlds = Context.RequireExtension<IWorldGamemodeService>();
            var content = new BundleWorldContent(
                Context.Assets,
                "AssetBundles/{{BUNDLE_NAME}}.bundle",
                "assets/world/world.prefab",
                TransformState.Identity,
                new CustomWorldOptions(spawnPointName: "SpawnPoint"));

            EnsureRegistered(worlds.RegisterWorld(
                new WorldDefinition(
                    WorldId,
                    "{{DISPLAY_NAME}}",
                    "A custom Robotopia world."),
                content));
            EnsureRegistered(worlds.RegisterMenuEntry(new GamemodeMenuEntry(
                WorldId + ".menu",
                "{{DISPLAY_NAME}}",
                "Play {{DISPLAY_NAME}} with the Sandbox gamemode.",
                WellKnownWorldIds.SandboxGamemode,
                WorldId)));

            Context.Logger.Info("{{DISPLAY_NAME}} world registered.");
        }

        private static void EnsureRegistered(OperationResult<IWorldRegistration> result)
        {
            if (!result.Succeeded)
            {
                throw new InvalidOperationException("World registration failed: " + result.ErrorMessage);
            }
        }
    }
}
