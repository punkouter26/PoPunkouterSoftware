# PoPunkouterSoftware — Architecture Overview

> Update this file when layers, public contracts, or hosting model change significantly.

## Solution Layout

```
PoPunkouterSoftware.sln
├── PoPunkouterSoftware/          # Server project — ASP.NET Core host + API
├── PoPunkouterSoftware.Client/   # Blazor WASM frontend (hosted by server)
├── PoPunkouterSoftware.Shared/   # DTOs shared by server and WASM client
└── tests/
    ├── PoPunkouterSoftware.Tests.Unit/        # Domain + service unit tests (xUnit)
    ├── PoPunkouterSoftware.Tests.Integration/ # API integration tests (Testcontainers + Azurite)
    └── PoPunkouterSoftware.Tests.E2E/         # Playwright TypeScript E2E tests
```

## Onion Architecture (server project)

Each ring can only reference inward — never outward.

```
Domain/          ← Interfaces + pure value types. No external dependencies.
  Azure/
    IAzureReportRepository.cs  — data access contract
    Result<T>.cs               — typed Result monad (no exceptions as control flow)

Application/     ← Use-case interfaces. References Domain only.
  Azure/
    IAzureReportService.cs     — Azure scan entry-point contract

Features/        ← Concrete implementations + endpoint registration (vertical slices).
  Azure/
    AzureReportService.cs      — implements IAzureReportService via Azure SDK
    AzureReportStore.cs        — implements IAzureReportRepository via Azure Table Storage
    FixPlanEndpoints.cs        — /api/diag/fix-plan/* (OpenAI-powered repair plans)
    RefreshProgressService.cs  — singleton progress tracker for background refresh
  Diag/
    DiagEndpoints.cs           — /api/diag/* (report, refresh, progress, az-status)
  GitHub/
    GitHubEndpoints.cs         — /api/github-activity
  Infra/
    InfraEndpoints.cs          — /api/infra/cicd-review

Infrastructure/  ← Framework-level cross-cutting concerns.
  AppKeyVaultSecretManager.cs  — filters KV secrets by "PoPunkouterSoftware--" prefix
  AzureClientFactory.cs        — creates typed Azure SDK clients with DefaultAzureCredential
  IAzureClientFactory.cs       — DI interface for the factory
```

## SOLID & GoF Patterns in Use

| Pattern | Location |
|---|---|
| Repository (GoF) | `IAzureReportRepository` / `AzureReportStore` |
| Facade (GoF) | `AzureReportService.RunAsync` orchestrates the Azure SDK |
| Proxy — Cache (GoF) | `IMemoryCache` wraps GitHub API and AI calls in `InfraEndpoints`, `GitHubEndpoints`, `FixPlanEndpoints` |
| Adapter (GoF) | `AppKeyVaultSecretManager` adapts flat KV names to .NET config hierarchy |
| Extension Method / Decorator (GoF) | `Map*Endpoints()` methods decorate `WebApplication` |
| Single Responsibility (SOLID) | Every class/endpoint group handles exactly one bounded concern |
| Open/Closed (SOLID) | Storage strategy extendable without changing callers |
| Dependency Inversion (SOLID) | All services registered against interfaces; callers never reference concrete types |

## Hosting Model

- **Server** (port 5000 HTTP / 5001 HTTPS) serves:
  - Blazor static web assets from the Client project (`MapStaticAssets`)
  - Interactive WASM render mode (`AddInteractiveWebAssemblyComponents`)
  - Minimal API endpoints under `/api/*`
- **Client** (Blazor WASM) renders interactively in the browser; calls `/api/*` via `HttpClient` with base address = server origin.

## Key Configuration Keys

| Key | Source |
|---|---|
| `AzureKeyVaultUri` | appsettings.json |
| `AzureTableStorage:ConnectionString` | Key Vault (prefixed) or appsettings.Development.json for Azurite |
| `AzureTableStorage:Endpoint` | Key Vault (prefixed) — used in prod instead of connection string |
| `ApplicationInsights:ConnectionString` | Key Vault (prefixed) |
| `Azure:SubscriptionId` | appsettings.json |
| `GitHub:PersonalAccessToken` | Key Vault (prefixed) |
| `AzureOpenAI:Endpoint/ApiKey/DeploymentName` | Key Vault (prefixed) |
| `FeatureFlags:EnableAzureRefresh` | appsettings.json |
| `FeatureFlags:EnableAiIntegration` | appsettings.json |

## Secret Naming Convention (Azure Key Vault — kv-poshared)

- App-specific secrets: `PoPunkouterSoftware--{SectionName}--{KeyName}`
- Shared secrets (not scoped to this app): un-prefixed

`AppKeyVaultSecretManager` loads only secrets starting with `PoPunkouterSoftware--`
and strips the prefix so they map to standard .NET config keys.

## Azure Resources

| Resource | Location |
|---|---|
| App Service | App's own resource group |
| Azure Table Storage | App's own resource group |
| Key Vault (`kv-poshared`) | PoShared resource group (shared) |
| App Service Plan | PoShared resource group (shared) |
| App Insights | PoShared resource group (shared) |

## Logging Context Properties

Every log event is enriched with: `CorrelationId`, `UserId`, `SessionId`, `Environment`, `Application`.
Sinks: Console, rolling File (`logs/app-YYYYMMDD.log`), Azure Application Insights (when connection string is set).
