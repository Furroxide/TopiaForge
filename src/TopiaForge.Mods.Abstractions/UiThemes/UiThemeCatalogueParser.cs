using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace TopiaForge.Mods
{
    internal sealed class UiThemeCatalogue
    {
        internal static readonly UiThemeCatalogue Empty = new UiThemeCatalogue(null, Array.Empty<UiThemeCatalogueEntry>());

        internal UiThemeCatalogue(string? defaultId, UiThemeCatalogueEntry[] entries)
        {
            Default = defaultId;
            Themes = Array.AsReadOnly(entries);
        }

        internal string? Default { get; }
        internal IReadOnlyList<UiThemeCatalogueEntry> Themes { get; }
    }

    internal sealed class UiThemeCatalogueEntry
    {
        internal UiThemeCatalogueEntry(string id, string definition, string? sha256)
        {
            Id = id;
            Definition = definition;
            Sha256 = sha256;
        }

        internal string Id { get; }
        internal string Definition { get; }
        internal string? Sha256 { get; }

        internal bool Matches(ReadOnlySpan<byte> definition)
        {
            if (Sha256 == null)
            {
                return true;
            }

            Span<byte> digest = stackalloc byte[32];
            using var sha256 = SHA256.Create();
            return sha256.TryComputeHash(definition, digest, out var written) && written == digest.Length
                && string.Equals(Sha256, UiThemeData.Hex(digest), StringComparison.Ordinal);
        }
    }

    internal static class UiThemeCatalogueParser
    {
        internal static OperationResult<UiThemeCatalogue> Parse(ReadOnlySpan<byte> utf8, bool packaged = false)
        {
            try
            {
                return OperationResult<UiThemeCatalogue>.Success(Parse(UiThemeJson.Read(utf8, "$.ui"), packaged));
            }
            catch (UiThemeDataException error)
            {
                return OperationResult<UiThemeCatalogue>.Failure(error.Code, error.Message);
            }
        }

        internal static OperationResult<UiThemeCatalogue> ParseManifest(ReadOnlySpan<byte> utf8, bool packaged = false)
        {
            try
            {
                var ui = UiThemeJson.ReadManifestUi(utf8);
                return OperationResult<UiThemeCatalogue>.Success(ui is { } declaration
                    ? Parse(declaration, packaged)
                    : UiThemeCatalogue.Empty);
            }
            catch (UiThemeDataException error)
            {
                return OperationResult<UiThemeCatalogue>.Failure(error.Code, error.Message);
            }
        }

        private static UiThemeCatalogue Parse(UiThemeJsonObject ui, bool packaged)
        {
            ui.Allow("schema", "default", "themes");
            ui.RequiredInteger("schema", 1, 1);
            var defaultId = ui.OptionalString("default");
            if (defaultId != null)
            {
                UiThemeData.Id(defaultId, ui.At("default"));
            }

            var declarations = ui.Array("themes", 8, required: true);
            var entries = new UiThemeCatalogueEntry[declarations.Count];
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < declarations.Count; index++)
            {
                var entry = UiThemeJsonObject.From(declarations[index], UiThemeData.Entry(ui.At("themes"), index));
                entry.Allow("id", "definition", "sha256");
                var id = UiThemeData.Id(entry.RequiredString("id"), entry.At("id"));
                if (!ids.Add(id))
                {
                    throw UiThemeDataException.Conflict(entry.At("id"), "Theme identifier is already declared.");
                }

                var definition = UiThemeData.Path(entry.RequiredString("definition"), entry.At("definition"));
                var hash = packaged ? entry.RequiredString("sha256") : entry.OptionalString("sha256");
                entries[index] = new UiThemeCatalogueEntry(id, definition,
                    hash == null ? null : UiThemeData.Sha256(hash, entry.At("sha256")));
            }

            if (defaultId != null && !ids.Contains(defaultId))
            {
                throw UiThemeDataException.Invalid(ui.At("default"), "Expected a declared theme identifier.");
            }

            return new UiThemeCatalogue(defaultId, entries);
        }
    }
}
