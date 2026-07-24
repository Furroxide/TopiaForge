using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Interop.Unity;

namespace TopiaForge.CreatorContent
{
    /// <summary>Publishes the shared creator content, project library, and F5 router services.</summary>
    public sealed class CreatorContentMod : TopiaForgeMod
    {
        private static readonly ConfigDefinition<CreatorContentConfig> ConfigContract =
            new ConfigDefinition<CreatorContentConfig>(1, () => new CreatorContentConfig(), CreatorContentConfig.Validate);
        private CreatorContentService? content;
        private CreatorBuiltInCatalog? builtIns;
        private CreatorToolHostRouter? router;

        /// <inheritdoc />
        protected override void OnLoad()
        {
            var loadedConfig = Context.Config.Load(ConfigContract);
            var config = loadedConfig.TryGetValue(out var configured) ? configured : new CreatorContentConfig();
            config.Normalize();
            Context.Config.Save(ConfigContract, config);

            content = new CreatorContentService(Context.Identity.Id, Context.Runtime, Context.Logger);
            var validator = new CreatorProjectValidator(() => content.Catalog);
            var library = new CreatorProjectLibrary(Context.Files, validator, Context.Logger);
            router = new CreatorToolHostRouter(Context.Identity.Id, Context.Input, Context.Scenes, Context.Logger);
            var mutationSafety = new UnavailableMutationSafetyService();

            Context.Lifetime.Track(content);
            TryAttachBuiltInCatalog();
            Context.Lifetime.Track(library);
            Context.Lifetime.Track(router);

            Register<ICreatorContentService>(content);
            Register<ICreatorSceneAdapterRegistry>(content);
            Register<ICreatorProjectLibrary>(library);
            Register<ICreatorToolHostService>(router);
            Register<ICreatorToolHostRouter>(router);
            Register<ICreatorMutationSafetyService>(mutationSafety);

            var hotkey = router.AttachInput(config.ToggleKey);
            if (!hotkey.Succeeded)
            {
                Context.Logger.Warn("Creator Content loaded without its F5 hotkey: " + hotkey.ErrorMessage);
            }

            Context.Events.SubscribeUpdate(_ => router?.Tick());
            Context.Events.SubscribeSceneLoaded(OnSceneLoaded);
            Context.Logger.Info("TopiaForge Creator Content loaded; shared creator services registered.");
        }

        /// <inheritdoc />
        protected override void OnUnload()
        {
            router = null;
            builtIns = null;
            content = null;
        }

        private void OnSceneLoaded(SceneLoadEvent scene)
        {
            if (!scene.IsWorldReplacement)
            {
                return;
            }

            router?.OnSceneChanged();
            content?.OnSceneChanged();
            var refreshed = builtIns?.Refresh();
            if (refreshed != null && !refreshed.Succeeded)
            {
                Context.Logger.Warn("Creator Content could not refresh native catalog sources: " + refreshed.ErrorMessage);
            }
        }

        private void TryAttachBuiltInCatalog()
        {
            try
            {
                var current = content ?? throw new InvalidOperationException("Creator Content is not initialized.");
                builtIns = new CreatorBuiltInCatalog(
                    current,
                    Context.RequireUnityInterop(),
                    Context.Runtime,
                    Context.Logger);
                Context.Lifetime.Track(builtIns);
                current.SetBuiltInRefresher(builtIns.Refresh);
                var refreshed = builtIns.Refresh();
                if (!refreshed.Succeeded)
                {
                    Context.Logger.Warn("Creator Content loaded without native catalog entries: " + refreshed.ErrorMessage);
                }
            }
            catch (Exception exception)
            {
                builtIns = null;
                Context.Logger.Warn(
                    "Creator Content native catalog adapters are unavailable; custom registrations remain active. "
                    + exception.Message);
            }
        }

        private void Register<T>(T provider) where T : class
        {
            var result = Context.Extensions.Register(provider);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(result.ErrorMessage);
            }
        }
    }
}
