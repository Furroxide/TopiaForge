using TopiaForge.Mods;
namespace TopiaForge.ModManager
{
    internal interface IRuntimeSessionSceneObserver
    {
        void OnSceneLifecycle(SceneLifecycleEvent scene);
    }
}
