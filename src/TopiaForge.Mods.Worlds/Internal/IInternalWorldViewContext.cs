namespace TopiaForge.Mods.Internal
{
    internal interface IInternalWorldViewFacade
    {
        void Activate(string worldId);
        IViewControls ForPlayer(string player);
    }
    internal interface IInternalWorldViewContext
    {
        bool TryGetViewWorld(out WorldReadiness? world, out IModLifetime? lifetime);
    }
    internal interface IInternalMapViewSource
    {
        IInternalWorldViewRenderer CreateRenderer(IModContext context);
    }
}
