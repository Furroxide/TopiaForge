using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("TopiaForge.Multiplayer")]
[assembly: InternalsVisibleTo("TopiaForge.Multiplayer.Engine")]
[assembly: InternalsVisibleTo("TopiaForge.Mods.Testing")]
[assembly: InternalsVisibleTo("TopiaForge.Mods.Multiplayer.Tests")]

// The internal multiplayer SPI (Internal/): ModManager's composition root drives the provider through it.
[assembly: InternalsVisibleTo("TopiaForge.ModManager")]
[assembly: InternalsVisibleTo("TopiaForge.ModManager.Tests")]
[assembly: InternalsVisibleTo("TopiaForge.ModRuntime.Tests")]
