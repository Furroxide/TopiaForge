using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.Worlds
{
    public sealed class WorldsMod : TopiaForgeMod
    {
        private static readonly ConfigDefinition<WorldsConfig> ConfigContract = new ConfigDefinition<WorldsConfig>(
            3, () => new WorldsConfig(), migrate: (_, value) => OperationResult<WorldsConfig>.Success(value));
        private PauseMenuBridge? pauseBridge;

        protected override void OnLoad()
        {
            var loaded = Context.Config.Load(ConfigContract);
            var config = loaded.TryGetValue(out var value) ? value : new WorldsConfig();
            Context.Config.Save(ConfigContract, config);
            if (!(Context is IInternalWorldSessionContext runtime))
                throw new InvalidOperationException("The loader did not provide its authoritative session services.");

            var local = new LocalWorldService(Context.Logger,
                () => new UgcImportHostBridge(Context.Logger), config.EnableLocalWorlds, config.LocalWorldFolder);
            Context.Lifetime.Track(local);
            var ui = TopiaForgeUi.For(Context);
            Context.Lifetime.Track(ui);
            pauseBridge = new PauseMenuBridge(runtime.Sessions, Context.Logger, ui, config.InterceptPauseMenu);
            Context.Lifetime.Track(pauseBridge);
            RegisterExtension<IWorldSessionService>(new WorldSessionServiceForwarder(runtime.Sessions));
            RegisterExtension<ILocalWorldService>(local);
            RegisterExtension<IWorldPauseMenuService>(pauseBridge);
            Context.Events.SubscribeUpdate(delta => pauseBridge?.Update(delta));
            Context.Logger.Info("TopiaForge Worlds loaded; declarations and sessions are managed by the loader.");
        }

        private void RegisterExtension<T>(T provider) where T : class
        {
            var registration = Context.Extensions.Register(provider);
            if (!registration.Succeeded) throw new InvalidOperationException(registration.ErrorMessage);
        }
    }
}
