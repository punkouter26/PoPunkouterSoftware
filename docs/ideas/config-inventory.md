# Config Inventory — Azure Custom Config Audit Page

## Problem Statement
**How might we** give one screen that catalogs every custom config value across all Azure services — and flags what's broken or drifting — so "where is that setting defined?" stops being a portal scavenger hunt?

## Recommended Direction
A new **Config Inventory** tab in the existing Azure dashboard, backed by a standalone, **cached** scan (memory + Table Storage, mirroring `AzureReportStore`) with a "last scanned" timestamp and a Refresh button.

The page leads with **relationship intelligence**, then drops into a full searchable catalog:
- **Broken Key Vault references** — App Service / Container App settings of the form `@Microsoft.KeyVault(SecretUri=...)` that point to a secret that's missing, disabled, or expired.
- **Duplicate keys across services** — the same key name in N apps (e.g. `ApplicationInsights--ConnectionString`), flagging where a sibling app is *missing* it.
- **Expiry & hygiene radar** — secrets expiring ≤30 days, untouched >2 years, disabled-but-referenced. All from metadata.
- **Orphan secrets** — Key Vault secrets referenced by nothing.
- **The catalog** — one filterable, exportable (CSV/JSON) table: `Service | Source | Key | Type | Last-Modified`.

**Names + metadata only. No secret value ever crosses the wire** — enforced server-side, so the browser, logs, and Table Storage never hold a value. This isn't an Azure limitation; it's a deliberate safety boundary, and it's exactly what makes the relationship features the product.

Sources inventoried: App Service app settings + connection strings, Key Vault secrets, Container Apps env vars/secrets, and storage/other connection-string settings — all within your single configured subscription.

## Key Assumptions to Validate
- [ ] **RBAC for listing.** Does `DefaultAzureCredential` have **List** on `kv-poshared` secrets and `listConfigurations` on the App Services? *Test:* one ARM call enumerating secret properties for the vault + one app-settings list. **This is the #1 thing that could kill the Key Vault half.**
- [ ] **Scan cost is acceptable cached.** Time a full sweep once; confirm it's fine as a deliberate, cached refresh (it would *not* be fine on every page load — why we cache).
- [ ] **KV-reference parsing.** Confirm App Service settings actually surface `@Microsoft.KeyVault(...)` references verbatim via ARM so they can be resolved against vault contents.

## MVP Scope
**In:** New `CustomConfigInfo` model in `AzureModels.cs`; a scan service (its own class, not bolted into the 1778-line `AzureReportService`) covering all four sources; a cached `GET /api/diag/config` + `POST /api/diag/config/refresh` (following the `DiagEndpoints` pattern); the catalog table + the four intelligence panels in a new Radzen tab; CSV/JSON export.

**Out of MVP (fast-follow):** "What's NOT configured?" baseline-gap detection — needs an authored expected-keys baseline.

## Not Doing (and Why)
- **Showing secret values** — deliberate security boundary; the whole design assumes values never leave the server.
- **Editing config from this page** — read-only audit tool; write access is a different, higher-risk feature.
- **Multi-subscription** — you're scoped to one subscription; adding fan-out is complexity the MVP doesn't need.
- **Pure live-every-load fetch** — too many ARM round-trips for a job where 30-min-stale is fine; cached + refresh wins.
- **Folding into the main 13-step report** — kept standalone so a config refresh doesn't trigger the full heavy analysis.

## Open Questions
- Container Apps: treat env-var-backed *secrets* differently from plain env vars in the catalog (mark which are secret-backed)?
- Export: does the CSV/JSON need to be safe to share externally (it is, by design — names only), or is it internal-only?
