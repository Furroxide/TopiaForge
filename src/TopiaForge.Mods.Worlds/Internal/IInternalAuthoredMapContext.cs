namespace TopiaForge.Mods.Internal
{
    // Package access is bound to the consuming gamemode, never to a caller-supplied host path.
    internal interface IInternalAuthoredMapContext
    {
        OperationResult<IEntity> WrapMapEntity(object nativeEntity);
        OperationResult<byte[]> ReadAnchorFile(string packagePath);
    }
}
