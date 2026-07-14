namespace {{ASSEMBLY_NAME}}
{
    /// <summary>
    /// Persisted per-mod JSON config (BepInEx/TopiaForge/config/{{MOD_ID}}.json). Loaded and re-saved
    /// on every load so new fields appear for players to edit.
    /// </summary>
    public sealed class {{TYPE_NAME}}Config
    {
        public bool Enabled { get; set; } = true;

        public void Normalize()
        {
            // Clamp/repair user-edited values here.
        }
    }
}
