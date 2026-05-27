# AGENT.MD

## What This Project Is
PoPunkouterSoftware is a .NET 10 Blazor Web App with a server host and a WebAssembly client. It exposes operational APIs (health, diagnostics, Azure, GitHub, infra) and serves a UI dashboard.

## Start Here
1. Read `architecture.md` before coding.
2. Preserve the Po-prefixed naming convention.
3. Keep the app title, assembly names, and solution identity aligned.

## Critical Conventions
- Local ports must remain 5000 (HTTP) and 5001 (HTTPS).
- Do not add UI links to `/diag` or `/health`.
- Keep warnings-as-errors and nullable enabled for production projects.
- Prefer Radzen for complex controls.
- Use strongly typed models for Table Storage entities.

## Project Boundaries
- `PoPunkouterSoftware.Infrastructure`: cloud/integration adapters.
- `PoPunkouterSoftware.Shared`: contracts, shared models, and cross-project value types.
- `PoPunkouterSoftware`: server host and API endpoints.
- `PoPunkouterSoftware.Client`: Blazor WASM UI.
- `tests/*`: unit, integration, and E2E suites.

## Secrets and Environment
- Priority order for secrets:
  1. Azure Key Vault
  2. App Service settings fallback
  3. Local development config
- If Azure CLI auth is available, prefer Key Vault-backed behavior.

## Required Quality Gates
- Unit, integration, and E2E tests should remain green.
- For major changes, always report:
  - One Pro
  - One Con

## Awesome Copilot Skills Workflow
Use this sequence as the default engineering workflow:

Phase 1 - Understand
- `acquire-codebase-knowledge`

Phase 2 - Design
- `architecture-blueprint-generator`
- `folder-structure-blueprint-generator`

Phase 3 - Build
- `dotnet-best-practices`
- `dotnet-design-pattern-review`
- `autoresearch`

Phase 6 - Harden
- `security-review`

Phase 7 - Observe
- `appinsights-instrumentation`

Phase 8 - Deploy
- `azure-deployment-preflight`

Phase 9 - Operate
- `azure-resource-health-diagnose`

Phase 10 - Document
- `create-readme`
- `repo-story-time`

## Safe Change Behavior
- Keep edits minimal and local.
- Avoid broad renames unless requested.
- If requirement scope is ambiguous, stop and list assumptions.
