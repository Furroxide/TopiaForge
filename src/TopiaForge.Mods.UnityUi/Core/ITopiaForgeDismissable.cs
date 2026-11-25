namespace TopiaForge.Mods.UnityUi
{
    /// <summary>
    /// A surface that handles Back, such as a window or modal. Back is Escape or a controller's B.
    /// TopiaForge delivers each Back press it takes to the topmost live surface.
    /// </summary>
    public interface ITopiaForgeDismissable
    {
        /// <summary>Gets band.</summary>
        TopiaForgeLayerBand Band { get; }

        /// <summary>Dismisses the active UI element.</summary>
        void Dismiss();
    }
}
