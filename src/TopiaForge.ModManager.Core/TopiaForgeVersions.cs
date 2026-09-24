namespace TopiaForge.ModManager.Core
{
    public static class TopiaForgeVersions
    {
        // BepInEx 5 parses BepInPlugin.Version with System.Version, which rejects
        // SemVer prerelease labels such as "-rc.1". Keep this numeric identity
        // aligned with the core of LoaderVersion while retaining the full SemVer
        // everywhere TopiaForge evaluates loader compatibility.
        public const string BepInExPluginVersion = "0.1.0";
        public const string LoaderVersion = "0.1.0-rc.1";
        public const string SdkVersion = "0.1.0-rc.1";
    }
}
