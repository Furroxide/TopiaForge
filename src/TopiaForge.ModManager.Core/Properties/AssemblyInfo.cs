using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("TopiaForge.ModManager.Tests")]
[assembly: InternalsVisibleTo("TopiaForge.ModManager")]
[assembly: InternalsVisibleTo("TopiaForge.GameCompat.Extractor")]

// The multiplayer realm rules and network profiles (Multiplayer/): the ModRuntime harness implements the gameplay factory
// seam that carries a profile, and the multiplayer harness compiles the runtime's realm sources.
[assembly: InternalsVisibleTo("TopiaForge.ModRuntime.Tests")]
[assembly: InternalsVisibleTo("TopiaForge.Mods.Multiplayer.Tests")]
