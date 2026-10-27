using TopiaForge.ModManager.Core.Multiplayer;

namespace TopiaForge.ModManager
{
    public sealed partial class ModRuntime
    {
        private readonly OwnerModLifetime presentationLifetime = new OwnerModLifetime();

        private PresentationSink CreatePresentationSink()
        {
            // This receiver has no author callbacks or mod dependency: server-only gamemodes can show a plain HUD too.
            const string id = "topiaforge.presentation";
            var gameplay = coreGameplayServices.Create(id, string.Empty, paths.GetDataPath(id), presentationLifetime,
                logger.ForMod(id), new ModNetProfile(ModNetworkClass.ClientLocal, ModExecutionContext.Process));
            return new PresentationSink(gameplay.Ui, gameplay.Audio);
        }
    }
}
