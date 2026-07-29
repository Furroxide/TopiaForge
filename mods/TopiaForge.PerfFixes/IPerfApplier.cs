namespace TopiaForge.PerfFixes
{
    /// <summary>One behavior-identical performance fix. Captures any global state it changes in
    /// <see cref="Apply"/> and restores it in <see cref="Revert"/>; Harmony patches are removed centrally
    /// by the mod's owner-scoped Harmony lease.</summary>
    internal interface IPerfApplier
    {
        string Name { get; }

        void Apply();

        void OnSceneLoaded(string sceneName);

        void OnUpdate(float deltaTime);

        void Revert();
    }

    internal abstract class PerfApplierBase : IPerfApplier
    {
        public abstract string Name { get; }

        public virtual void Apply() { }

        public virtual void OnSceneLoaded(string sceneName) { }

        public virtual void OnUpdate(float deltaTime) { }

        public virtual void Revert() { }
    }
}
