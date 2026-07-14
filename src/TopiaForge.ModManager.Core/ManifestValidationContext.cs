namespace TopiaForge.ModManager.Core
{
    /// <summary>
    /// Versions against which a manifest is validated. Authoring tools may omit the game version;
    /// production install/scan paths should set <see cref="RequireKnownGameVersion"/>.
    /// </summary>
    public sealed class ManifestValidationContext
    {
        public ManifestValidationContext(
            string? gameVersion = null,
            string? loaderVersion = null,
            string? sdkVersion = null,
            bool requireKnownGameVersion = false)
        {
            GameVersion = gameVersion;
            LoaderVersion = loaderVersion ?? TopiaForgeVersions.LoaderVersion;
            SdkVersion = sdkVersion ?? TopiaForgeVersions.SdkVersion;
            RequireKnownGameVersion = requireKnownGameVersion;
        }

        public string? GameVersion { get; }
        public string LoaderVersion { get; }
        public string SdkVersion { get; }
        public bool RequireKnownGameVersion { get; }

        /// <summary>
        /// Validation for authoring tools that have no installed-game context. Loader and SDK constraints
        /// are still checked; a constrained game range is syntax-checked but not rejected as unknown.
        /// </summary>
        public static ManifestValidationContext Current { get; } = new ManifestValidationContext();
    }
}
