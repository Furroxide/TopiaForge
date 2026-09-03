using System.IO;

namespace TopiaForge.ModManager.Core
{
    /// <summary>
    /// Selects an immutable manifest contract before any version-specific field is interpreted. Adding a future
    /// schema means adding a new enum value, reader branch, and validator; it must never widen an existing branch.
    /// </summary>
    internal enum ManifestSchemaContract
    {
        V5 = ModManifest.ManifestV5SchemaVersion,
        V6 = ModManifest.ManifestV6SchemaVersion
    }

    internal static class ManifestSchemaDispatch
    {
        public static ManifestSchemaContract Resolve(int schemaVersion)
        {
            if (schemaVersion == 4)
            {
                throw new InvalidDataException(
                    "Manifest schemaVersion 4 was retired before TopiaForge 1.0. " +
                    "Run 'topiaforge migrate-manifest --project <path>' to create schemaVersion 5; " +
                    "omit multiplayer for a standalone-only mod.");
            }

            if (schemaVersion == ModManifest.ManifestV5SchemaVersion)
            {
                return ManifestSchemaContract.V5;
            }

            if (schemaVersion == ModManifest.ManifestV6SchemaVersion)
            {
                return ManifestSchemaContract.V6;
            }

            throw new InvalidDataException(
                "Unsupported manifest schemaVersion " + schemaVersion + "; schemaVersion 5 or 6 is required.");
        }
    }
}
