namespace TopiaForge.Mods.Internal
{
    internal interface IInternalViewEntityContext
    {
        object? UnwrapViewEntity(IEntity entity);
        bool IsCurrentViewAvatar(IEntity entity, IInternalWorldViewRenderer renderer);
    }
}
