namespace Robotopia.Performance
{
    /// <summary>
    /// A single, self-contained performance lever group. Every applier captures the original game
    /// state it touches in <see cref="Apply"/> and restores it in <see cref="Revert"/>, so the mod
    /// is fully reversible on unload.
    /// </summary>
    internal interface IPerfApplier
    {
        string Name { get; }

        /// <summary>Apply the lever once at mod load (game state captured here).</summary>
        void Apply();

        /// <summary>Re-assert state that the game clobbers on scene load / quality switch.</summary>
        void OnSceneLoaded(string sceneName);

        /// <summary>Re-assert sticky per-frame state (kept minimal/cheap). Most appliers no-op.</summary>
        void OnUpdate(float deltaTime);

        /// <summary>Restore every captured original. Never throws.</summary>
        void Revert();
    }

    /// <summary>No-op defaults so appliers only override the hooks they use.</summary>
    internal abstract class PerfApplierBase : IPerfApplier
    {
        public abstract string Name { get; }

        public virtual void Apply() { }

        public virtual void OnSceneLoaded(string sceneName) { }

        public virtual void OnUpdate(float deltaTime) { }

        public virtual void Revert() { }
    }
}
