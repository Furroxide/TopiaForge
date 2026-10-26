namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// A surface Back can dismiss (windows, modals). Back is Escape, or a controller's B. While the surface is on the
    /// dismiss stack, the game never sees a Back press: one press dismisses only the topmost surface.
    /// </summary>
    public interface ITopiaForgeDismissable
    {
        /// <summary>Gets band.</summary>
        TopiaForgeLayerBand Band { get; }

        /// <summary>Dismisses the active UI element.</summary>
        void Dismiss();
    }
}
