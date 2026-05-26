# PoPunkouterSoftware Architecture Record

## Purpose
This file is the canonical architecture record for PoPunkouterSoftware. Treat it as the system prompt for coding agents and contributors.

## Project Identity
- Solution name: PoPunkouterSoftware
- Master prefix: PoPunkouterSoftware
- HTML app title and assembly naming must stay aligned with solution identity.
- Azure naming should use PoPunkouterSoftware or PoShared patterns where platform constraints allow.

## Runtime and Language Baseline
- .NET SDK pinned by `global.json` (net10 toolchain).
- C# language level: 14.
- Nullable enabled globally.
- Warnings treated as errors globally (tests may opt out when needed).
- Git-driven versioning: MinVer from Git tags (`vX.Y.Z`).

## Architecture Style
- Onion Architecture with physical project separation:
  - Domain: business rules and core abstractions.
  - Infrastructure: external systems and adapters.
  - Shared: contracts/models shared between server and WASM client.
  - Web host: API endpoints and Blazor hosting shell.
  - Client: Blazor WASM UI.
- Dependency direction must point inward to domain abstractions.
- Prefer SOLID and explicit interfaces at boundaries.

## UI and Client Rules
- Blazor WASM hosted by server project.
- Radzen is preferred for complex controls over custom thin wrappers.
- Mobile-first layout and left-aligned top navigation.
- If mock mode is active, show a clear top-bar indicator.
- No UI links for `/diag` or `/health`.

## API and Diagnostics Rules
- Local ports: HTTP 5000, HTTPS 5001.
- OpenAPI and Scalar are required for debugging APIs.
- Keep `api.http` examples current.
- Required diagnostics endpoints:
  - `/health`: machine-readable JSON health.
  - `/diag`: diagnostics details with masked secrets.

## Security and Secrets
- Primary secrets source: Azure Key Vault.
- Fallback: App Service application settings for critical keys.
- Local development can use appsettings.Development.json.
- If Azure CLI auth is available, prefer Key Vault to mirror production behavior.

## Azure and Storage
- Managed identity in Punkouter26 resource group.
- Table Storage DTOs should be strongly typed (`ITableEntity`).
- Environment switching:
  - Local: Azurite via Docker/well-known local connection string.
  - Cloud: Azure Storage via Key Vault and managed identity.

## Testing Strategy
- Unit tests: xUnit in C#.
- Integration tests: C# with Testcontainers (including Azurite/SQL when applicable).
- E2E tests: Playwright in TypeScript.

## Observability
- Structured logging with Serilog and enrichers.
- Include correlation/session/user context where available.
- OpenTelemetry should feed Application Insights.

## Engineering Hygiene
- Prefer zero-waste code: remove dead code and unused files promptly.
- Keep shared logic in shared project boundaries.
- Place automation scripts under `SCRIPTS/`.
- `setup.ps1` should support first-run machine setup for local development.

## Agent Execution Rule
When requirements are unclear:
1. Stop implementation.
2. State assumptions explicitly.
3. Wait for clarification before broad changes.

For major changes, include exactly:
- One Pro
- One Con

## Known Technical Debt
- Existing folder layout uses top-level project folders rather than strict `src/` and `test/` roots. Migrate only with a dedicated refactor plan.
- OAuth, guest persistence UX hardening, multiplayer lobby, and multi-model AI selection are requirements for feature-specific apps and may require dedicated milestones.
