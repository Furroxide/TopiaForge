namespace TopiaForge.Mods.Internal
{
    internal interface IInternalWorldViewContext
    {
        bool TryGetViewWorld(out WorldReadiness? world, out IModLifetime? lifetime);
    }
    internal interface IInternalMapViewSource
    {
        IInternalWorldViewRenderer CreateRenderer(IModContext context);
    }
}
