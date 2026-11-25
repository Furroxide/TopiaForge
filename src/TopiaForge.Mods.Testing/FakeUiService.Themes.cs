using System;
using System.Collections.Generic;
using System.Text;

namespace TopiaForge.Mods.Testing
{
    public sealed partial class FakeUiService
    {
        private const int MaximumDefinitionBytes = 64 * 1024;
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
        private readonly FakeUiThemeScope themeScope;

        /// <inheritdoc/>
        [RequiresTopiaForge(UiKitRelease.K1)]
        public IUiThemeService Themes
        {
            get
            {
                gameThread.AssertCurrent();
                return themeScope;
            }
        }

        /// <summary>Validates and declares the supplied theme definition in this fake package.</summary>
        [RequiresTopiaForge(UiKitRelease.K1)]
        public OperationResult<bool> AddThemeFixture(string definitionJson)
        {
            gameThread.AssertCurrent();
            if (definitionJson == null) throw new ArgumentNullException(nameof(definitionJson));
            var parsed = ReadThemeDefinition(definitionJson, entry: null);
            return parsed.TryGetValue(out var definition)
                ? themeScope.Package.Declare(themeScope, new[] { definition }, manifestDefault: null, catalogue: false)
                : OperationResult<bool>.Failure(parsed.ErrorCode, parsed.ErrorMessage);
        }

        /// <summary>Validates a manifest's UI catalogue and declares its theme definitions together.</summary>
        /// <param name="manifestJson">The package manifest as UTF-8 text.</param>
        /// <param name="readFile">Reads a catalogue definition by its validated package-relative path.</param>
        /// <remarks>The reader must preserve the file's bytes when re-encoded as UTF-8, including line endings.</remarks>
        [RequiresTopiaForge(UiKitRelease.K1)]
        public OperationResult<bool> AddThemeCatalogue(string manifestJson, Func<string, string> readFile)
        {
            gameThread.AssertCurrent();
            if (manifestJson == null) throw new ArgumentNullException(nameof(manifestJson));
            if (readFile == null) throw new ArgumentNullException(nameof(readFile));

            OperationResult<UiThemeCatalogue> parsed;
            try { parsed = UiThemeCatalogueParser.ParseManifest(StrictUtf8.GetBytes(manifestJson)); }
            catch (EncoderFallbackException)
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "The manifest must be strict UTF-8 JSON.");
            }

            if (!parsed.TryGetValue(out var catalogue))
                return OperationResult<bool>.Failure(parsed.ErrorCode, parsed.ErrorMessage);

            var definitions = new List<FakeUiThemeDefinition>(catalogue.Themes.Count);
            foreach (var theme in catalogue.Themes)
            {
                string json;
                try
                {
                    json = readFile(theme.Definition);
                    if (json == null) return ManifestReadFailure(definitions.Count);
                }
                catch (Exception)
                {
                    return ManifestReadFailure(definitions.Count);
                }

                var result = ReadThemeDefinition(json, theme);
                if (!result.TryGetValue(out var definition))
                    return OperationResult<bool>.Failure(result.ErrorCode, result.ErrorMessage);
                definitions.Add(definition);
            }

            return themeScope.Package.Declare(themeScope, definitions, catalogue.Default, catalogue: true);
        }

        /// <summary>Adds resource metadata before the package first acquires a theme.</summary>
        [RequiresTopiaForge(UiKitRelease.K1)]
        public void AddAssetFixture(FakeUiAssetMetadata asset)
        {
            gameThread.AssertCurrent();
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            themeScope.Package.AddAsset(themeScope, asset);
        }

        /// <summary>Completes queued fake registrations and retires cancelled fake load attempts.</summary>
        /// <remarks>Registrations waiting on a retired attempt start after this step and complete on a later step.</remarks>
        [RequiresTopiaForge(UiKitRelease.K1)]
        public void CompletePendingThemeRegistrations()
        {
            gameThread.AssertCurrent();
            themeScope.Package.Advance();
        }

        internal FakeUiService CreateThemeScope(FakeModLifetime childLifetime)
        {
            gameThread.AssertCurrent();
            if (childLifetime == null) throw new ArgumentNullException(nameof(childLifetime));
            if (ReferenceEquals(childLifetime, lifetime)) throw new ArgumentException("A child needs its own lifetime.", nameof(childLifetime));
            var lease = lifetime.Track(childLifetime);
            childLifetime.StoppingToken.Register(lease.Dispose);
            return new FakeUiService(childLifetime, gameThread, themeScope);
        }

        internal FakeUiThemeResolution ResolveTheme(UiThemeRef? theme) => themeScope.Package.Resolve(themeScope, theme);

        internal UiThemeDescriptor? ThemeForWire(UiThemeRef? theme) => themeScope.Package.DescriptorForWire(themeScope, theme);

        private static OperationResult<FakeUiThemeDefinition> ReadThemeDefinition(string json, UiThemeCatalogueEntry? entry)
        {
            byte[] bytes;
            try
            {
                if (json.Length > MaximumDefinitionBytes || StrictUtf8.GetByteCount(json) > MaximumDefinitionBytes)
                    return OperationResult<FakeUiThemeDefinition>.Failure(ModErrorCode.RateLimited, "The theme definition exceeds 64 KiB.");
                bytes = StrictUtf8.GetBytes(json);
            }
            catch (EncoderFallbackException)
            {
                return OperationResult<FakeUiThemeDefinition>.Failure(ModErrorCode.InvalidArgument, "The theme definition must be strict UTF-8 JSON.");
            }

            if (entry != null && !entry.Matches(bytes))
                return OperationResult<FakeUiThemeDefinition>.Failure(ModErrorCode.InvalidArgument, "A theme definition does not match its catalogue SHA-256.");

            var parsed = UiThemePackParser.Parse(bytes, expectedId: entry?.Id);
            return parsed.TryGetValue(out var descriptor)
                ? OperationResult<FakeUiThemeDefinition>.Success(new FakeUiThemeDefinition(bytes, descriptor))
                : OperationResult<FakeUiThemeDefinition>.Failure(parsed.ErrorCode, parsed.ErrorMessage);
        }

        private static OperationResult<bool> ManifestReadFailure(int entryIndex) =>
            OperationResult<bool>.Failure(ModErrorCode.Io, "Could not read definition at $.ui.themes[" + entryIndex + "].");
    }
}
