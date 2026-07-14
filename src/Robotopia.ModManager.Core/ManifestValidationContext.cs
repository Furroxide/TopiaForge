namespace Robotopia.ModManager.Core
{
    /// <summary>
    /// Versions against which a manifest is validated. Callers that are only inspecting legacy packages may
    /// omit the game version; production install/scan paths should set <see cref="RequireKnownGameVersion"/>.
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
            LoaderVersion = loaderVersion ?? RobotopiaVersions.LoaderVersion;
            SdkVersion = sdkVersion ?? RobotopiaVersions.SdkVersion;
            RequireKnownGameVersion = requireKnownGameVersion;
        }

        public string? GameVersion { get; }
        public string LoaderVersion { get; }
        public string SdkVersion { get; }
        public bool RequireKnownGameVersion { get; }

        /// <summary>
        /// Backward-compatible validation used by tooling that has no installed-game context. Loader and SDK
        /// constraints are still checked; a constrained game range is syntax-checked but not rejected as unknown.
        /// </summary>
        public static ManifestValidationContext Current { get; } = new ManifestValidationContext();
    }
}
