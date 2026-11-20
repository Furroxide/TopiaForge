using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Core
{
    /// <summary>Manifest metadata for a package-local collection of UI themes.</summary>
    [DataContract]
    public sealed class ModUiCatalogue
    {
        [DataMember(Name = "schema")]
        public int Schema { get; set; }

        [DataMember(Name = "default", EmitDefaultValue = false)]
        public string? Default { get; set; }

        [DataMember(Name = "themes")]
        public List<ModUiCatalogueEntry> Themes { get; set; } = new List<ModUiCatalogueEntry>();
    }

    /// <summary>One definition in the package's UI theme catalogue.</summary>
    [DataContract]
    public sealed class ModUiCatalogueEntry
    {
        [DataMember(Name = "id")]
        public string Id { get; set; } = string.Empty;

        [DataMember(Name = "definition")]
        public string Definition { get; set; } = string.Empty;

        [DataMember(Name = "sha256", EmitDefaultValue = false)]
        public string? Sha256 { get; set; }
    }

    /// <summary>Immutable descriptors resolved at package admission.</summary>
    internal sealed class AdmittedUiCatalogue
    {
        private readonly Dictionary<string, UiThemeDescriptor> themes;

        internal AdmittedUiCatalogue(string? defaultId, Dictionary<string, UiThemeDescriptor> themes)
        {
            DefaultId = defaultId;
            this.themes = themes;
        }

        internal string? DefaultId { get; }

        internal bool TryGet(string id, out UiThemeDescriptor descriptor) => themes.TryGetValue(id, out descriptor!);
    }
}
