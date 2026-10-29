namespace TopiaForge.Mods.Internal
{
    // Read-only authority minted by the runtime before a trusted provider spawns an owned entity.
    // This grants neither registry identity nor access to native objects through the public SDK.
    internal interface IInternalNativeEntityReadContext
    {
        IInternalNativeEntityReadScope? CaptureNativeEntityReadScope();
    }

    internal interface IInternalNativeEntityReadScope
    {
        int SceneInstanceId { get; }
        string SceneName { get; }
        bool IsCurrent(IModLifetime caller);
        bool IsCurrentWorld(IInternalWorldViewContext caller);
    }

    internal interface IInternalOwnedNativeEntity : IInternalNativeObjectEntity
    {
        IInternalNativeEntityReadScope? NativeReadScope { get; }
    }
}
