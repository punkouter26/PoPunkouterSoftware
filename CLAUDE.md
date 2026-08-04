# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Read AGENT.MD first

[AGENT.MD](AGENT.MD) is the authoritative architecture and boundaries document — slice layout, the
naming schema, telemetry, secrets, retention, the deliberate no-auth and no-AI decisions, and the
reasons behind them. It is maintained as the context map for agents. **Update it in the same change
whenever you cross an architectural boundary**; drift in it actively misleads the next agent.

This file covers commands and the cross-file data flow that AGENT.MD does not spell out.

## Commands

```powershell
# Build (TreatWarningsAsErrors is ON solution-wide — a warning fails the build)
dotnet build PoPunkouterSoftware.sln

# Fast tier: unit + integration. One project per invocation — `dotnet test` rejects two
# project paths in one command (MSB1008). Integration needs Docker (Testcontainers Azurite).
dotnet test tests/PoPunkouterSoftware.Unit
dotnet test tests/PoPunkouterSoftware.Integration

# A single test / class
dotnet test tests/PoPunkouterSoftware.Integration --filter "FullyQualifiedName~GetConfig_ReturnsExactlyTheThreeFieldsTheClientBinds"
dotnet test tests/PoPunkouterSoftware.Integration --filter "FullyQualifiedName~ConfigEndpointTests"

# Run locally. Use this, not `dotnet run` — see the footguns below.
./SCRIPTS/run-dev.ps1              # kills stale instance, starts Azurite, watch-run on :8000
./SCRIPTS/run-dev.ps1 -NoWatch

# E2E (on demand only, never in CI). Requires the app already running.
# BASE_URL for both, default http://localhost:8000.
dotnet test tests/PoPunkouterSoftware.E2EAPI                                      # pure HTTP
pwsh tests/PoPunkouterSoftware.E2EUI/bin/Debug/net10.0/playwright.ps1 install     # once
dotnet test tests/PoPunkouterSoftware.E2EUI                                       # Playwright
$env:HEADED=1; $env:BROWSER_CHANNEL='chrome'; dotnet test tests/PoPunkouterSoftware.E2EUI

# `dotnet clean` leaves obj/ behind. When a build misbehaves for no visible reason:
Get-ChildItem -Recurse -Directory -Include bin,obj | Remove-Item -Recurse -Force
```

VS Code tasks wrap most of this: `build`, `test-unit`, `test-unit-watch`, `start-azurite`,
`start-api-server`, `deep-clean`. F5 runs `f5-prep` (kill stale dotnet → start Azurite → build).

### Never `dotnet run` this project directly

Three footguns, all documented in [SCRIPTS/run-dev.ps1](SCRIPTS/run-dev.ps1): a stale
`PoPunkouterSoftware.exe` locks port 8000 and the build DLLs (MSB3026 stalls, then a half-replaced
`wwwroot` gives the browser WASM 404s and SRI errors — the page hangs on the loading skeleton
forever); `--no-launch-profile` defaults to `ASPNETCORE_ENVIRONMENT=Production`, which silently
attaches a local run to the **production** Key Vault, Table Storage and App Insights; and without
the profile it binds :5000 instead of :8000.

## Architecture: how data actually flows

Two pages only — `/` (app catalog) and `/azure` (ops dashboard).

**The report is the spine.** `AzureReportService.RunAsync` performs a ~14-step subscription scan and
produces one `AzureReport`. `AzureReportStore` persists it to Table Storage (gzipped blob + a small
precomputed `HistorySummary` row per scan). Everything the UI shows is a projection of that stored
report — no endpoint queries Azure live on a page load.

**Two read contracts, deliberately split.** `/api/diag/summary` returns the compact `OpsSummary`
(built by `DiagEndpoints.BuildOpsSummary`) and is the *only* first-paint fetch for `/azure`. The
full `AzureReport` and the 30-day history load lazily, only when the user opens Advanced
diagnostics. Adding a field to the first-paint path means adding it to `OpsSummary`, not returning
the full graph — an integration test asserts `/api/diag/summary` does not contain `webServices`.

**Refresh is asynchronous and pushed.** `POST /api/diag/refresh` returns 202 immediately;
`ReportRefreshRunner` runs the scan on a background task, broadcasting progress over the
`RefreshHub` SignalR hub. The client also polls `/api/diag/summary` as a fallback and shows an
*indeterminate* progress bar when the hub is down, because percent values only arrive over the hub.

**Degradation is a designed path, not an error path.** Table Storage unavailable falls back to a
local JSON report file (`ReportFileCache`); a corrupt cache file degrades to "no report", never a
500. The `apps.json` catalog is authoritative for the home page — live Azure inventory only
*decorates* entries, so a probe failure never removes an app from the portfolio.

### Two big types are partial classes split by concern

Both use `<Type>.<Concern>.cs`. Find the concern, not the file:

- `AzureReportService.cs` — orchestrator + primary constructor only. Steps live in
  `.Discovery`, `.Metrics`, `.Cost`, `.Security`, `.Inventory`, `.Cleanup`, `.GitHubCorrelation`,
  `.Helpers`. The primary constructor is declared in the orchestrator file **and only there**.
- `AzureDashboard.razor.cs` — state, lifecycle, loading, refresh, SignalR. Projections in
  `.DerivedViews` (priority queue, resource explorer, cleanup candidates), display mapping in
  `.Presentation`, history in `.Trends`. Markup blocks are sibling components in the client
  project root (`AzurePriorityQueue`, `AzureResourceExplorer`, `AzureEvidenceDisclosures`,
  `AzureHistoryDisclosure`) — the layout is flat, there is no `Components/` tree.

## Things that will bite you

- **Client JSON is source-generated.** Every type the WASM client (de)serialises needs a
  `[JsonSerializable]` entry in `AppJsonContext.cs`. `PublishTrimmed` + `EnableTrimAnalyzer` are on
  for `.Client` with no `WarningsNotAsErrors` escape hatch, so reflection-based JSON fails the build.
- **Scoped CSS does not cross component boundaries.** Blazor stamps the scope attribute only on the
  owning component's own elements. If you move markup from a page into a child component, every
  scoped rule that styled it silently stops applying. `AzureDashboard.razor.css` is written on
  `.azure-ops-page ::deep …` for exactly this reason — keep **every** rule in that form,
  media-query overrides included. `::deep` also *moves* the scope attribute leftward, so mixing
  the two forms silently breaks specificity: `.azure-ops-page ::deep .x` compiles to
  `.azure-ops-page[b-id] .x` (0,3,0) while a bare `.x` compiles to `.x[b-id]` (0,2,0). Media
  queries add no specificity, so a bare override **loses to its own base rule** and the layout
  never reflows — that is how the Azure glance grid stayed 3-up at 390px while the responsive
  rules sat there looking correct. Two E2E tests passed straight through it, because
  `minmax(0,1fr)` prevents the overflow they check for.
- **Named `HttpClient`s are pooled and shared.** Never reassign `DefaultRequestHeaders` on an
  instance from `CreateClient(name)`; it leaks the header (including credentials) to every other
  consumer. Set per-call values on the `HttpRequestMessage`.
- **Package versions go in `Directory.Packages.props` only** (Central Package Management, with
  transitive pinning on). Some entries there are *security pins* for transitive packages with no
  direct reference — `Microsoft.OpenApi` is one; removing it because "nothing references it"
  reintroduces a vulnerable version. `MinVer` is referenced from `Directory.Build.props`, not a csproj.
- **`Testing` environment is the hermetic switch.** It skips Key Vault entirely. Integration tests
  boot the real entry point via `WebApplicationFactory`, and collections run sequentially
  (`AssemblyInfo.cs`) because concurrent entry-point boots race.
- **UI tests must cover both viewports.** `PortfolioUiTests` drives every UI test at mobile-portrait
  (390×844) and desktop-landscape (1440×1000) through the shared `Viewports` theory data. Visual
  parity is a hard rule, not a preference.
- **No test steps run in CI by design.** `deploy.yml` is build-and-deploy only. Run the fast tier
  locally before pushing to master.

## Project-level skills

`.claude/skills/` is auto-managed by the `dotnet-skills` global tool — do not hand-edit. Refresh
with `dotnet skills install --auto --prune --agent claude` after changing package references.
