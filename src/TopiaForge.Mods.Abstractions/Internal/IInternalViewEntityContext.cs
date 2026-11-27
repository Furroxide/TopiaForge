namespace TopiaForge.Mods.Internal
{
    internal interface IInternalViewEntityContext
    {
        object? UnwrapViewEntity(IEntity entity);
        object? UnwrapViewProviderEntity(IEntity entity, IInternalWorldViewRenderer renderer);
        bool IsCurrentViewAvatar(IEntity entity, IInternalWorldViewRenderer renderer);
    }
}
