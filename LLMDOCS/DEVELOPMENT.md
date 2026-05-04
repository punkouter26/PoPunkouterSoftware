# PoPunkouterSoftware — Local Development Guide

## Prerequisites

| Tool | Version |
|---|---|
| .NET SDK | 10.0.x (pinned by `global.json`) |
| Docker Desktop | Latest (for Azurite) |
| Node.js | 18+ (for Playwright E2E tests) |
| Azure CLI (`az`) | Latest (for local `DefaultAzureCredential`) |
| VS Code | Latest with C# Dev Kit extension |

---

## First-Time Setup

### 1. Start Azure Storage emulator (Azurite)

```bash
docker compose up azurite -d
```

Azurite exposes:
- Blob:  `http://127.0.0.1:10000`
- Queue: `http://127.0.0.1:10001`
- Table: `http://127.0.0.1:10002`

The `appsettings.Development.json` sets `AzureTableStorage:ConnectionString = "UseDevelopmentStorage=true"` which automatically points to Azurite.

### 2. Authenticate with Azure (optional — for live data)

```bash
az login
```

This allows `DefaultAzureCredential` to reach Key Vault and the Azure subscription for the real Azure scan.
Without it, Key Vault is skipped (non-fatal warning logged) and only local Azurite is used.

### 3. Run the app

Press **F5** in VS Code (uses the `▶ Run PoPunkouterSoftware (.NET) — Edge` launch config).

This automatically:
1. Kills any existing `dotnet` process
2. Launches the server on `https://localhost:5001` / `http://localhost:5000`
3. Opens Edge browser

Alternatively: `dotnet run --project PoPunkouterSoftware/PoPunkouterSoftware.csproj`

---

## Key URLs (local)

| URL | Purpose |
|---|---|
| `http://localhost:5000` | App home |
| `https://localhost:5001` | App home (HTTPS) |
| `http://localhost:5000/diag` | Diagnostics page |
| `http://localhost:5000/api/health` | Health check JSON |
| `http://localhost:5000/scalar` | Scalar OpenAPI explorer |
| `http://localhost:5000/openapi/v1.json` | Raw OpenAPI spec |

---

## Feature Flags

Controlled via `appsettings.json` or environment variables — no code changes needed.

| Flag | Default | Effect |
|---|---|---|
| `FeatureFlags:EnableAzureRefresh` | `true` | Allows the Azure SDK refresh trigger |
| `FeatureFlags:EnableAiIntegration` | `false` | Gates Azure OpenAI fix-plan calls |

Set `FeatureFlags:EnableAiIntegration=true` + configure `AzureOpenAI` settings to enable real AI calls locally.

---

## Running Tests

### Unit Tests (C#)

```bash
dotnet test tests/PoPunkouterSoftware.Tests.Unit
```

Target: Domain logic (pure, no external dependencies).

### Integration Tests (C#)

```bash
docker compose up azurite -d   # Azurite needed for Table Storage tests
dotnet test tests/PoPunkouterSoftware.Tests.Integration
```

Integration tests use `WebApplicationFactory<Program>` + `Testcontainers.Azurite` to spin up real Azurite containers.
Key Vault and App Insights are disabled in the test host (in-memory config overrides).

### E2E Tests (TypeScript / Playwright)

```bash
cd tests/PoPunkouterSoftware.Tests.E2E
npm install
npx playwright install
npx playwright test --headed
```

Headed mode is required in Dev. Point `playwright.config.ts` `baseURL` at `http://localhost:5000`.
The server must be running before E2E tests execute.

---

## Secrets (local)

No secrets are stored in `appsettings.json`.

For local dev with Key Vault: `az login` then set `AzureKeyVaultUri` in `appsettings.json` (already set to `https://kv-poshared.vault.azure.net/`).

For overriding specific secrets locally: use [.NET User Secrets](https://learn.microsoft.com/en-us/aspnet/core/security/app-secrets):
```bash
cd PoPunkouterSoftware
dotnet user-secrets set "AzureOpenAI:ApiKey" "sk-..."
```

User secrets ID: `popunkouter-software-api` (set in `.csproj`).

---

## Logs

Rolling log files: `PoPunkouterSoftware/logs/app-YYYYMMDD.log`
Logs are git-ignored via `.gitignore`.

Every log entry includes: `CorrelationId`, `UserId`, `Environment`, `Application`.

---

## Adding a New Feature

Follow the vertical slice pattern used in `Features/`:

1. **Domain**: Add interface to `Domain/` if a new data contract is needed.
2. **Application**: Add use-case interface to `Application/` if consumers need an abstraction.
3. **Feature**: Create `Features/{Name}/{Name}Endpoints.cs` with `Map{Name}Endpoints(this WebApplication app)`.
4. **Register**: Call `app.Map{Name}Endpoints()` in `Program.cs`.
5. **Client**: Add Razor page in `PoPunkouterSoftware.Client/Components/Pages/`.
6. **Shared**: Add DTOs to `PoPunkouterSoftware.Shared/` if consumed by both client and server.
7. **Tests**: Add unit tests in `Tests.Unit/` and integration tests in `Tests.Integration/`.
8. **api.http**: Document the new endpoint.
9. **LLMDOCS/API.md**: Update the API reference.
