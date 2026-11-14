# Control plane operations

The Cloudflare control-plane Worker, its Wrangler configuration, deployment workflow, and current operations runbook are maintained in [TopiaForge-Status](https://github.com/Furroxide/TopiaForge-Status/blob/main/docs/internal/ControlPlaneOperations.md) for maintainers with repository access. Its staging and production deployments remain separate from the game and launcher.

The [API contract](ControlPlaneApi.md) remains here for TopiaForge's clients. Shared client fixtures remain under `tests/fixtures/multiplayer`; service tests use copies in TopiaForge-Status.
