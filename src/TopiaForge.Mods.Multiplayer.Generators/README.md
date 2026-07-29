# TopiaForge multiplayer generator

This package is installed automatically by `topiaforge mod add multiplayer`. It generates the bounded codecs,
stable wire identifiers, registration, snapshots, typed submission proxies, and prediction bookkeeping required by
`TopiaForge.Mods.Multiplayer` contracts.

Use the public attributes and generated APIs documented in the
[TopiaForge multiplayer guide](https://docs.topiaforge.dev/guides/multiplayer/). Do not call the generator/provider
SPI directly or hand-author wire identifiers and serialization. The package has no runtime component and should be
referenced with `PrivateAssets="all"`.
