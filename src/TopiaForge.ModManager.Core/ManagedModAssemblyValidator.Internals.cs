using System;
using System.Collections.Generic;

namespace TopiaForge.ModManager.Core
{
    public static partial class ManagedModAssemblyValidator
    {
        // Every assembly a TopiaForge assembly names in InternalsVisibleTo sees TopiaForge internals, the runtime's session
        // ticket among them. A package may bundle one only when it is the first-party package that ships it; test and tool
        // assemblies never ship in a package. ManagedModAssemblyValidatorTests checks this covers every such name.
        private static readonly Dictionary<string, string?> InternalsVisibleToNames =
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["TopiaForge.GameCompat.Extractor.dll"] = null,
                ["TopiaForge.ModManager.Tests.dll"] = null,
                ["TopiaForge.ModRuntime.Tests.dll"] = null,
                ["TopiaForge.Mods.Analyzers.Tests.dll"] = null,
                ["TopiaForge.Mods.Multiplayer.Generators.Tests.dll"] = null,
                ["TopiaForge.Mods.Multiplayer.Tests.dll"] = null,
                ["TopiaForge.Multiplayer.Engine.dll"] = null,
                ["TopiaForge.Chronos.dll"] = "io.github.furroxide.topiaforge.chronos",
                ["TopiaForge.CreatorContent.dll"] = "io.github.furroxide.topiaforge.creatorcontent",
                ["TopiaForge.Multiplayer.dll"] = "io.github.furroxide.topiaforge.multiplayer",
                ["TopiaForge.Prompts.dll"] = "io.github.furroxide.topiaforge.prompts",
                ["TopiaForge.RobotKit.dll"] = "io.github.furroxide.topiaforge.robotkit",
                ["TopiaForge.UiGallery.dll"] = "io.github.furroxide.topiaforge.uigallery",
                ["TopiaForge.Worlds.dll"] = "io.github.furroxide.topiaforge.worlds"
            };

        /// <summary>
        /// Why the package of <paramref name="manifest"/> may not bundle <paramref name="fileName"/>, an assembly or its
        /// symbols named <paramref name="assemblyName"/>; null when it may.
        /// </summary>
        private static string? InternalsVisibleToRefusal(string fileName, string assemblyName, ModManifest manifest) =>
            InternalsVisibleToNames.TryGetValue(assemblyName, out var owner) && !string.Equals(manifest.Id, owner, StringComparison.Ordinal)
                ? "Package must not bundle " + fileName + ": TopiaForge trusts that assembly with its internals."
                : null;
    }
}
