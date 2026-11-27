# Control plane operations

The Worker, Wrangler configuration, and [operations runbook](https://github.com/Furroxide/TopiaForge-Status/blob/main/docs/internal/ControlPlaneOperations.md) live in the private TopiaForge-Status repository. This repository hosts the [protected deployment workflow](CloudflareDeploymentHost.md), which deploys a reviewed revision of Status `main`.

The [API contract](ControlPlaneApi.md), TopiaForge clients, and shared client fixtures under `tests/fixtures/multiplayer` remain here. Service tests use copies of the shared fixtures in TopiaForge-Status.
