# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this app is

A personal **Azure portfolio / ops dashboard**: a .NET 10 Blazor Web App whose WASM front-end shows
App Service health, cost, SSL expiry, zombie-app detection and downtime diagnosis. The backend is a
minimal-API **BFF** that reads an Azure inventory report out of Azure Table Storage (with a local
JSON fallback).

There are exactly **two pages**: `/` (the app catalog) and `/azure` (the ops dashboard). Everything
else is an endpoint.

`AGENT.MD` used to hold this context and no longer exists — this file is the single context map.
Update it in the same change whenever you cross an architectural boundary; drift here actively
misleads the next agent.

## Commands

```powershell
# Build. TreatWarningsAsErrors is ON solution-wide — a warning fails the build.
dotnet build PoPunkouterSoftware.sln

# Fast tier: unit then integration. ONE project per invocation — `dotnet test` rejects two
# project paths in one command (MSB1008). Integration needs Docker (Testcontainers Azurite).
dotnet test tests/PoPunkouterSoftware.Unit
dotnet test tests/PoPunkouterSoftware.Integration

# A single test or class
dotnet test tests/PoPunkouterSoftware.Integration --filter "FullyQualifiedName~ConfigEndpointTests"
dotnet test tests/PoPunkouterSoftware.Unit --filter "FullyQualifiedName~SecretMaskingTests"

# Run locally. Use this, NOT `dotnet run` — see the footguns below.
./SCRIPTS/run-dev.ps1              # kills stale instance, starts Azurite, watch-run on :8000
./SCRIPTS/run-dev.ps1 -NoWatch

# E2E — on demand only, never in CI. Requires the app already running.
# BASE_URL for both, default http://localhost:8000. Point it at production for post-deploy smoke.
dotnet test tests/PoPunkouterSoftware.E2EAPI                                    # pure HTTP
pwsh tests/PoPunkouterSoftware.E2EUI/bin/Debug/net10.0/playwright.ps1 install   # once
dotnet test tests/PoPunkouterSoftware.E2EUI                                     # Playwright
$env:HEADED=1; $env:BROWSER_CHANNEL='chrome'; dotnet test tests/PoPunkouterSoftware.E2EUI

# `dotnet clean` leaves obj/ behind. When a build misbehaves for no visible reason:
Get-ChildItem -Recurse -Directory -Include bin,obj | Remove-Item -Recurse -Force
```

VS Code tasks wrap most of this: `build`, `test-unit`, `test-unit-watch`, `start-azurite`,
`start-api-server`, `deep-clean`. F5 runs `f5-prep` (kill stale dotnet → start Azurite → build).

### Never `dotnet run` this project directly

Three footguns, all documented in [SCRIPTS/run-dev.ps1](SCRIPTS/run-dev.ps1): a stale
`PoPunkouterSoftware.API.exe` locks port 8000 and the build DLLs (MSB3026 stalls, then a
half-replaced `wwwroot` gives the browser WASM 404s and SRI errors — the page hangs on the loading
skeleton forever); `--no-launch-profile` defaults to `ASPNETCORE_ENVIRONMENT=Production`, silently
attaching a local run to the **production** Key Vault, Table Storage and App Insights; and without
the profile it binds :5000 instead of :8000.

## Solution layout (vertical slices)

| Project | Role |
|---|---|
| [src/PoPunkouterSoftware.API/](src/PoPunkouterSoftware.API/) | ASP.NET Core host + BFF, and the Blazor WASM host. Slices are `Features/<Area>/<Area>Endpoints.cs` (**Config, Diag, Portfolio**), each exposing `Map<Area>Endpoints(this WebApplication)` called from [Program.cs](src/PoPunkouterSoftware.API/Program.cs). Host-level plumbing belonging to no slice lives in `Host/`. **The namespace is flat** — every file is `PoPunkouterSoftware.API` regardless of folder. |
| [src/PoPunkouterSoftware.Client/](src/PoPunkouterSoftware.Client/) | Blazor WASM (Radzen, mobile-first). `wwwroot` lives **only** here. The layout is **flat** — every component sits in the project root, named for what it is; there is no `Components/` tree. |
| [src/PoPunkouterSoftware.Shared/](src/PoPunkouterSoftware.Shared/) | DTOs + `DomainVocabulary.cs` only. **No PackageReferences at all**, no server- or browser-only deps — it ships in the WASM bundle (`IsTrimmable`). |
| [src/PoPunkouterSoftware.Infrastructure/](src/PoPunkouterSoftware.Infrastructure/) | Azure adapters (Table Storage, ARM, pinger, incident, AI triage, telemetry) plus cross-slice helpers (`ReportFileCache`, `AttentionItemsBuilder`, `SecretMasking`). **Slices must not reference each other** — shared logic goes here. |

### Naming schema

- **One type per file, named for the type.** Two exceptions: a cohesive vocabulary of constants is
  named for the group (`DomainVocabulary.cs`, `TableStorageVocabulary.cs`), and a group of related
  DTOs is `<Concern>Models.cs` (`CostModels.cs`, `SecurityModels.cs`).
- **Partial-class aspect files are `<Type>.<Aspect>.cs`, and the aspect names the concern**, not the
  shape: `AzureReportService.Cost.cs`, `AzureDashboard.DerivedViews.cs`. A `.Charts.cs` holding no
  charts is the failure mode to avoid.
- **A file name must be greppable.** If the name is not a type inside the file and is not one of the
  two exceptions, the name is wrong.
- **Test files mirror the source file under test**, not one file per test class
  (`DomainVocabularyTests.cs` holds `ServiceHealthTests`, `SeverityLevelTests`, …).
- **Records crossing a component boundary get their own top-level file**
  ([PriorityQueueItem.cs](src/PoPunkouterSoftware.Client/PriorityQueueItem.cs),
  [ResourceExplorerItem.cs](src/PoPunkouterSoftware.Client/ResourceExplorerItem.cs)) — a private
  nested record cannot be a Blazor component parameter.

## Architecture: how data actually flows

**The report is the spine.** `AzureReportService.RunAsync` performs a ~14-step subscription scan and
produces one `AzureReport`. `AzureReportStore` persists it to Table Storage (gzipped blob + a small
precomputed `HistorySummary` row per scan). Everything the UI shows is a projection of that stored
report — **no endpoint queries Azure live on a page load.**

**Two read contracts, deliberately split.** `/api/diag/summary` returns the compact `OpsSummary`
(built by `DiagEndpoints.BuildOpsSummary`) and is the *only* first-paint fetch for `/azure`. The full
`AzureReport` and the 30-day history load lazily, only when Advanced diagnostics is opened. Adding a
field to the first-paint path means adding it to `OpsSummary`, not returning the full graph — an
integration test asserts `/api/diag/summary` does not contain `webServices`.

**Refresh is asynchronous and pushed.** `POST /api/diag/refresh` returns 202 immediately;
`ReportRefreshRunner` runs the scan on a background task and broadcasts progress over the
`RefreshHub` SignalR hub (`/hubs/refresh`). The client also polls `/api/diag/summary` as a fallback
and shows an *indeterminate* progress bar when the hub is down, because percent values arrive only
over the hub.

**Degradation is a designed path, not an error path.** Table Storage unavailable falls back to a
local JSON report file (`ReportFileCache`); a corrupt cache degrades to "no report", never a 500.
`wwwroot/data/apps.json` is authoritative for the home page — live Azure inventory only *decorates*
entries, so a probe failure never removes an app from the portfolio.

**The report cache is server-side and must stay off the web root.** `ReportFileCache` keeps two
paths apart on purpose: `GetCatalogDir` is `wwwroot/data` and holds **only** `apps.json`, which is
public by nature; `GetCacheDir` is `ContentRootPath/App_Data` and holds the report. The cache used
to sit beside `apps.json` in `wwwroot/data`, where `UseStaticFiles()` served it to anyone who
requested `/data/azure-full-report.json` — a full Azure inventory including the raw subscription id,
every resource id, cost figures and SSL state, and it was committed to the repo besides. It has
never had a browser consumer. Do not put it back under any served directory, and do not "fix" a
future leak with a static-file exclusion when the file simply does not belong there.
`App_Data/` is gitignored; the Integration fixture seeds its own copy (see below).

**Each fact appears once on `/azure`.** The page carries one `.azure-glance-grid` of three cards
(forecast, cost-by-resource-group, response times) with the uptime heatmap full-width below it.
There used to be a second grid and a fourth card: a donut restating the healthy/total ratio the hero
tile already gives as a percentage — the same number appeared in the hero, the card subtitle and the
donut centre — and a cost chart that drew `CostHistory`, the exact series the hero's cost sparkline
draws. Trend belongs in the sparkline, breakdown in the chart, ratio in the hero. `OpsSummary` lost
`FleetHealth` with the donut; if you add a field to the first-paint contract, make sure nothing on
the page already renders it.

**Two big types are partial classes split by concern.** Find the concern, not the file:

- `AzureReportService.cs` — orchestrator + primary constructor, declared there **and only there**.
  Steps live in `.Discovery`, `.Metrics`, `.Cost`, `.Security`, `.Inventory`, `.Cleanup`,
  `.GitHubCorrelation`, `.Helpers`.
- `AzureDashboard.razor.cs` — state, lifecycle, loading, refresh, SignalR. Projections in
  `.DerivedViews`, display mapping in `.Presentation`, history in `.Trends`. Markup blocks are
  sibling components in the client root (`AzureStatusNarrative`, `AzureWhatChanged`,
  `AzureCostForecast`, `AzureUptimeHeatmap`, `Sparkline`, `AzurePriorityQueue`,
  `AzureResourceExplorer`, `AzureEvidenceDisclosures`, `AzureHistoryDisclosure`,
  `AzureSnoozedItems`).

## Cross-cutting decisions

- **Every endpoint needs a consumer.** An endpoint whose only caller is its own test is dead code
  with a green check mark. Three whole slices (GitHub, Infra, Pinger) were deleted in 2026-07 for
  exactly that — all tested, none reachable from the UI. Before adding a route, know what calls it.
- **No real login, by design.** No `/auth/*`, no `AuthenticationStateProvider`, no OAuth, no login
  UI. [Host/FakeAuthHandler.cs](src/PoPunkouterSoftware.API/Host/FakeAuthHandler.cs) reads
  `X-Fake-User` / `X-Fake-Roles` to build a `ClaimsPrincipal` purely so a caller can opt into the
  `management` role; it is registered **only outside Production** and throws in its own constructor
  under Production. In Production no scheme is registered at all and the `Management` policy denies
  outright rather than challenging (a challenge with no handler is a 500). Nothing exposes the
  principal over HTTP any more: `/api/whoami`, `/api/logout` and `/api/impersonate` were affordances
  of the deleted Session/Logout slot, had no caller left, and were removed under the
  "every endpoint needs a consumer" rule — `/api/config` is now the only route in the Config slice.
  Do not add a real identity provider unless the owner reverses this.
- **No login UI in the header, deliberately.** `MainLayout.razor` is statically server-rendered
  (`<Routes />` carries no render mode), so `@onclick` never fires and its injected `HttpClient` has
  no `BaseAddress`. A Session/Logout slot and "MOCK DATA" banner were built, could never render, and
  were deleted rather than kept as chrome that only looks functional (2026-08-03). Reinstating either
  requires a small `InteractiveWebAssembly` island in the header, not a change to `MainLayout`.
- **Management gate.** Mutating/expensive endpoints (`/api/diag/refresh`, `/api/diag/cancel-refresh`)
  carry `.RequireManagementActions()`, which enforces `FeatureFlags:EnableManagementActions` (on in
  Development/Testing, otherwise opt-in) plus an optional `Security:ManagementApiKey` via the
  `X-Management-Key` header. `/api/diag/ai` and the snooze endpoints are deliberately unprivileged.
- **Routing.** `/api/diag/*` and `/api/portfolio/*` use `MapGroup`. `/health` (deep probe, one
  `IHealthCheck` per external dependency), `/healthz` (static liveness) and `/diag` sit off the
  `/api` group. There is deliberately **no** `/api/health` alias.
- **Resilience.** Typed clients `github` and `azure-arm` use `AddStandardResilienceHandler`.
  `health`, `azure-probe` and `ai-hf` deliberately have **none** — they must report real reachability
  (or degrade), not retry through the outages they exist to detect.
- **Named `HttpClient`s are pooled and shared.** Never reassign `DefaultRequestHeaders` on an
  instance from `CreateClient(name)` — handler chains are pooled, so a per-call mutation leaks that
  header, credentials included, to every other consumer. The `github` PAT is bound once in
  `Program.cs`. Pass per-call values on the `HttpRequestMessage`.
- **Package versions go in `Directory.Packages.props` only** (Central Package Management, transitive
  pinning on). Some entries are *security pins* for transitive packages with no direct reference —
  `Microsoft.OpenApi` is one; removing it because "nothing references it" reintroduces a vulnerable
  version. `MinVer` is referenced from `Directory.Build.props`, not from any csproj.
- **Status narrative (AI).** `AiTriageService` calls **Azure AI Foundry** chat completions on the
  shared `po-aiservices-shared` account in the `poshared` resource group — deployment
  `gpt-5.4-nano`, the cheapest one there that writes a coherent paragraph. It produces the
  plain-English lead paragraph on `/azure` from the full fact set assembled by
  `StatusFactsBuilder`, not from the attention list alone. On by default
  (`FeatureFlags:EnableAiSummary`), and it degrades to a rule-based narrative
  (`BuildNarrativeFallback`), never a dead "unavailable" state. The per-scan paragraph is
  precomputed by `ReportRefreshRunner` and attached to the report before
  `AzureReportStore.SaveAsync`, so it round-trips through the existing storage paths;
  `POST /api/diag/ai` serves only the on-demand "Rewrite this" button. Regeneration is skipped
  (`Source: "cached"`) when the fact hash is unchanged. There is deliberately exactly **one** AI
  client — this replaced the Hugging Face `flan-t5-base` integration wholesale.
  **Auth order:** `StatusNarrator:ApiKey` → Entra ID via the shared `TokenCredential` → the app's
  existing `AzureOpenAI:ApiKey` vault secret. The keyless path needs the `Cognitive Services OpenAI
  User` role, declared in `infra/modules/cognitive-services-openai-user.bicep`; note that unlike
  `kv-poshared` (RBAC **disabled**, so Key Vault access is a classic access policy), Cognitive
  Services data-plane access is always RBAC. Until that role is applied, production runs on the key
  fallback and works either way.
- **Never name a config section after a generic Azure service.** `AppKeyVaultSecretManager` loads
  **every** secret in the shared vault into configuration, and configuration keys are
  **case-insensitive**. The vault holds estate-wide `AzureAI--ApiKey` / `AzureAI--Endpoint` secrets
  pointing at a different account, so a section named `AzureAi` silently inherited a foreign API key
  and every model call returned 401. The narrator's settings live under `StatusNarrator:*`, which
  collides with nothing. Its endpoint and deployment deliberately do **not** fall back to the vault's
  `AzureOpenAI--*` values either — `PoPunkouterSoftware--AzureOpenAI--DeploymentName` is stale
  (`gpt-4.1-nano`, which 404s on that account). Only the API key falls back there.
- **History-backed dashboard insights.** `DashboardInsightsBuilder` (pure, Unit-tested) derives
  "what changed since last scan", the month-end cost forecast, and the 30-day uptime grid. All three
  are projected onto `OpsSummary`, so they cost the first paint nothing extra. Two honesty rules are
  load-bearing: an uptime **cell** is worst-wins per day (one bad scan marks the day), but the
  **percentage** counts observations, not days — deriving it from the cells made 2-of-3 healthy scans
  in one day read as "0% uptime" beside a paragraph correctly calling the same app healthy.
- **The uptime grid has two data sources, and needs both.** Full scans are not scheduled: one runs
  only on a manual rescan or when a visitor loads a stale (>12h) portfolio, so an untrafficked day
  would be a blank column. `ServicePingerService` therefore also persists per-day reachability
  tallies (`UptimeSampleStore`, `uptime-samples` partition — one row per service per day, not per
  sweep, or the read path would scan ~35,000 rows). `BuildUptime` merges both: a day counts as
  observed if either saw it, and cells stay worst-wins across sources.
  - **Both writers must key on `NameMatching.ServiceIdentity`.** The grid joins scan rows and ping
    rows on that string. They disagreed once — `HistorySummaryMapper` wrote `PoMemeVideo` while the
    pinger wrote `app-pomemevideo` — and every service rendered as two half-populated rows.
  - **A ping `timeout` is recorded as neither up nor down.** These are F1 apps that sleep; a 14s
    probe missing a cold start is not evidence of downtime. `unreachable` and 5xx still count.
  - The pinger stops when the app does (F1 has no Always-On), which is why
    `.github/workflows/uptime-scan.yml` exists — see CI/CD below.
- **Secrets.** Key Vault `kv-poshared`, prefix `PoPunkouterSoftware--`, loaded at startup via
  System-Assigned Managed Identity, and **skipped entirely under the `Testing` environment**. The
  grant is a classic access policy, not RBAC — the vault is shared across Po* apps, so switching
  models is an estate-wide change.
- **Telemetry.** Serilog → Console + File; Azure Monitor via OpenTelemetry is the sole App Insights
  pipeline (`writeToProviders: true` on `UseSerilog` is load-bearing — without it application logs
  never reach App Insights). Traces are fixed-rate sampled at 10%
  (`ApplicationInsights:SamplingRatio`); exceptions are never lost to it because
  `GlobalExceptionHandler` logs them through `ILogger` and the logs pipeline is not trace-sampled.
  `cloud_RoleName` comes from the OTel resource `service.name`, set by reflection **in the API only,
  never in WASM**.
- **Storage & retention.** Azure Table Storage; local dev runs Azurite in Docker
  ([docker-compose.yml](docker-compose.yml), `UseDevelopmentStorage=true`). History rows are pruned
  after `Retention:HistoryDays` (default 30) on each save; blobs age out via the lifecycle policy in
  [infra/main.bicep](infra/main.bicep). Incidents are deliberately never pruned.
- **Snoozes.** Findings have no server-side identity — the `SnoozeStore` RowKey is a SHA-256 hash of
  the client's opaque key (Table Storage forbids `|` and friends), with the raw key kept as a
  property so it round-trips. Expiry is filtered in `GetActiveAsync`, not by a TTL or cleanup job;
  expired rows are excluded from reads, not deleted.
- **Caching.** None beyond the framework's. `HybridCache` and the sized `IMemoryCache` were removed
  along with the slices that used them; add one back deliberately if a read-through cache is needed.
- **No CORS.** Single-origin Blazor Web App — the WASM client is served from this same host.

## Things that will bite you

- **Client JSON is source-generated.** Every type the WASM client (de)serialises needs a
  `[JsonSerializable]` entry in [AppJsonContext.cs](src/PoPunkouterSoftware.Client/AppJsonContext.cs).
  `PublishTrimmed` + `EnableTrimAnalyzer` are on for `.Client` with no `WarningsNotAsErrors` escape
  hatch, so reflection-based JSON fails the build.
- **One application stylesheet.** `wwwroot/css/modern-ui.css` is the whole thing (plus `boot.css`
  for the pre-Blazor splash). It was a four-line aggregator over `modern-ui.base/.components/.responsive`;
  CSS `@import` is serial, so the split cost three extra round trips on the critical path and bought
  nothing a section comment does not. Order inside the file is load-bearing: tokens, then components
  that consume them, then breakpoint overrides.
- **Scoped CSS does not cross component boundaries.** Blazor stamps the scope attribute only on the
  owning component's own elements, so moving markup from a page into a child component silently kills
  every scoped rule that styled it. `AzureDashboard.razor.css` is written as
  `.azure-ops-page ::deep …` for exactly that reason — keep **every** rule in that form, media-query
  overrides included. `::deep` also moves the scope attribute leftward, so mixing forms breaks
  specificity: `.azure-ops-page ::deep .x` compiles to `.azure-ops-page[b-id] .x` (0,3,0) while a
  bare `.x` compiles to `.x[b-id]` (0,2,0). Media queries add no specificity, so a bare override
  **loses to its own base rule** and the layout never reflows — that is how the Azure glance grid
  stayed 3-up at 390px with correct-looking responsive rules, and `minmax(0,1fr)` hid it from the two
  E2E tests that check for overflow.
- **`Testing` is the hermetic switch.** It skips Key Vault entirely. Integration tests boot the real
  entry point via `WebApplicationFactory`, and collections run sequentially (`AssemblyInfo.cs`)
  because concurrent entry-point boots race.
- **UI tests must cover both viewports.** `PortfolioUiTests` drives every test at mobile-portrait
  (390×844) and desktop-landscape (1440×1000) through the shared `Viewports` theory data. Visual
  parity is a hard rule, not a preference.
- **Screenshots pin `PLAYWRIGHT_BROWSERS_PATH`** to the persistent `%HOME%` share and install only
  the Chromium headless shell — the worker's ephemeral disk previously filled with a full Chromium
  download and broke every deployment (2026-07-10). Kill switch:
  `FeatureFlags:EnableScreenshots=false`.

## Tests — four projects, one per tier (budget 100/50/25/25, currently 100/50/22/14)

**The budget is a ceiling, not a target.** All four tiers are at or under it. Adding a test means
finding one to remove, so prefer widening an existing test's assertions to adding a new method — the
two tiers that had to be cut back were full of one-assertion Facts that each re-fetched the same
document (nine for `/health`, twelve for `Result<T>`), and collapsing those to whole-contract
assertions cost no coverage at all. Parameterise only where the cases are genuinely different
branches; a `[Theory]` with six casing variants of one rule is six tests' worth of budget for one
rule's worth of coverage.

- **`PoPunkouterSoftware.Unit`** — strictly no I/O (HTTP stubs, in-memory). DTO-mapping tests live in
  `.Integration`, never here.
- **`PoPunkouterSoftware.Integration`** — `WebApplicationFactory` + Testcontainers Azurite, including
  a fixture that runs Azurite inside the factory with explicit teardown. `TestWebApp` seeds its own
  report cache into `App_Data` — it previously got a report for free from the committed
  `wwwroot/data/azure-full-report.json`, an undeclared dependency on a production data dump that
  made "hermetic" untrue. `ProductionBootTests` boots
  the entry point under `Production` with every external dependency blanked — the only coverage of
  that environment's hosting pipeline.
- **`PoPunkouterSoftware.E2EAPI`** / **`.E2EUI`** — pure HTTP and Playwright against a live instance
  via `BASE_URL`. On demand, not in CI.

## Git workflow — master only

**This repository uses `master` and nothing else.** Commit and push directly to `master`. Do not
create feature branches, topic branches, or PR branches, and do not leave one behind after a piece
of work — if a branch exists for any reason, delete it locally and on the remote once its commits
are on `master`. There is no review gate here to justify the indirection, and a stale branch on a
solo repo is just a second version of the truth.

This overrides any default "branch before committing to the default branch" habit. It has one real
consequence, which is the point rather than a side effect: **every push to `master` deploys to
production** via [deploy.yml](.github/workflows/deploy.yml). So run the fast tier locally before you
push — the pipeline will not run it for you.

**CI/CD:** two workflows, neither of which runs tests — run the fast tier locally before pushing.

- [deploy.yml](.github/workflows/deploy.yml) — build-and-deploy only, by design. Target is App
  Service `app-popunkoutersoftware` via OIDC, no secrets in the workflow.
- [uptime-scan.yml](.github/workflows/uptime-scan.yml) — nightly (06:17 UTC) `POST /api/diag/refresh`
  so the uptime grid gets one data point per day even when nobody visits and the F1 site is asleep.
  It wakes the site first (cold start), treats 409 "already refreshing" as success, and then
  **confirms a fresh report actually landed** — a 202 only proves the scan started, and a scan that
  fails every night would otherwise show as a green workflow forever.
  **Requires** `FeatureFlags:EnableManagementActions=true` in Production plus a
  `Security:ManagementApiKey` matching the `MANAGEMENT_API_KEY` GitHub secret. `ManagementActionFilter`
  **fails closed** in Production when the flag is on without a key — that combination would leave a
  free, repeatable, ~30-second subscription scan open to anonymous callers.
