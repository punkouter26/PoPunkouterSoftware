# PoPunkouterSoftware — API Reference

> Update when endpoints are added, removed, or their contracts change.

Base URL (local): `http://localhost:5000` / `https://localhost:5001`
Base URL (Azure): determined at deploy time.

All endpoints return JSON. Error responses follow RFC 7807 ProblemDetails format.

---

## Health & Config

### `GET /api/health` | `GET /health`
Probes all external connections and returns masked config snapshot.

**Response 200:**
```json
{
  "status": "healthy | degraded",
  "application": "PoPunkouterSoftware",
  "environment": "Development",
  "timestamp": "2026-01-01T00:00:00Z",
  "checks": {
    "KeyVault":     { "status": "reachable", "httpStatus": 401 },
    "TableStorage": { "status": "reachable", "httpStatus": 200 }
  },
  "config": {
    "AzureKeyVaultUri":                    "https://kv-p***",
    "AzureTableStorage:ConnectionString":  "(not set)",
    "ASPNETCORE_ENVIRONMENT":              "Development"
  }
}
```

### `GET /api/config`
Lets the WASM client discover the canonical API base URL and environment mode.

**Response 200:**
```json
{
  "apiBase":    "https://localhost:5001/api",
  "isMockMode": false
}
```
`isMockMode` is `true` when `ASPNETCORE_ENVIRONMENT` is `"Testing"`.

### `GET /openapi/v1.json`
Raw OpenAPI 3.x specification.

### `GET /scalar`
Interactive Scalar API explorer (browser only).

---

## Azure Report

### `GET /api/diag/report`
Returns the latest `AzureReport` JSON from Table Storage, falling back to the local `wwwroot/data/azure-full-report.json` file.

**Responses:** 200 (report), 404 (no report yet), 503 (storage unavailable)

### `POST /api/diag/refresh`
Triggers a background Azure SDK scan. Returns immediately with 202 Accepted.
Poll `/api/diag/refresh-progress` to track progress.

**Response 202:**
```json
{ "started": true }
```

**Response 409:** Refresh already running.

### `GET /api/diag/refresh-progress`
Returns current refresh progress.

**Response 200:**
```json
{
  "running":  true,
  "step":     "Scanning App Services…",
  "percent":  45,
  "error":    null,
  "done":     false
}
```

### `GET /api/diag/az-status`
Checks whether a valid Azure credential is available (`az login` / Managed Identity).

**Response 200:**
```json
{
  "loggedIn": true,
  "identity": "user@example.com",
  "source":   "AzureCli"
}
```

---

## Fix Plans (AI-powered)

### `GET /api/diag/fix-plan/{serviceName}`
Generates an AI-powered step-by-step repair plan for a broken App Service.
Grounded in the latest `AzureReport`. Cached for 6 hours per service name.

Requires `FeatureFlags:EnableAiIntegration=true` and configured `AzureOpenAI` settings.

**Response 200 (AI enabled):**
```json
{ "plan": "Step 1: Check application logs…" }
```

**Response 200 (AI disabled):**
```json
{
  "plan":     null,
  "disabled": true,
  "message":  "AI integration is disabled. Set FeatureFlags:EnableAiIntegration=true…"
}
```

**Response 404:** Service not found in the latest report.

---

## GitHub Activity

### `GET /api/github-activity?repo={owner}/{repo}`
Returns last commit date and 8-week sparkline data for a GitHub repository.
Cached 30 minutes. `repo` must match pattern `owner/repo` (alphanumeric, dash, dot, underscore).

**Response 200:**
```json
{
  "lastCommitDate": "2026-01-15T10:30:00Z",
  "sparkline":      [3, 7, 2, 0, 5, 12, 4, 8]
}
```

---

## Infrastructure / CI-CD Review

### `GET /api/infra/cicd-review`
Scans all GitHub repos owned by the configured account for CI/CD workflow files and
infrastructure definitions (Bicep, ARM). Returns a structured side-by-side comparison.

Requires `GitHub:PersonalAccessToken` (or `gh` CLI logged in locally).
Cached 6 hours.

**Response 200:**
```json
[
  {
    "repo":         "punkouter/MyApp",
    "workflows":    [ { "name": "deploy.yml", "triggers": ["push"], "deployTargets": ["App Service"] } ],
    "bicepTypes":   ["Microsoft.Web/sites"],
    "hasAzureLogin": true
  }
]
```
