namespace TopiaForge.ModManager
{
    /// <summary>Distinguishes a destroyed canvas from an intentionally hidden or gameplay-gated HUD.</summary>
    internal interface IPresentationSurfaceState
    {
        bool IsAlive { get; }
    }
}
