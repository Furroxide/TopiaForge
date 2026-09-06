namespace TopiaForge.Worlds
{
    /// <summary>Evidence required after the game's void-returning import API.</summary>
    internal static class UgcImportCompletionPolicy
    {
        internal static bool IsFresh(object? previous, object? current) => current != null && !ReferenceEquals(previous, current);
    }
}
