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

## Working rules

Standing instructions from the owner. They outrank default habits and apply to every task here.

- **`master` only.** Commit straight to `master`; use another branch only when explicitly asked for
  one. See [Git workflow](#git-workflow--master-only) for the full rule.
- **Never push without being asked.** Not "asked once, months ago" — asked in the turn you are
  working on. A push deploys straight to production. Commit, then hand back and say it is ready.
- **Git-sync commit messages are short and sound human.** One line, casual American English —
  "fixed the busted app cards", "cleaned up the dead scan code". No formal subject/body essays, no
  bullet lists, no changelog voice.
- **Restart the app after any code change and confirm it came back up.** `./SCRIPTS/run-dev.ps1`,
  then check it actually serves (`/healthz` returns 200) before claiming the change works. A build
  that compiles is not a running app — and this project's stale-process footgun means a "successful"
  build can leave the old binary serving on :8000.
- **No `dotnet user-secrets`.** Local config goes in `appsettings*.json`; real secrets go in the
  `kv-poshared` Key Vault. Rationale: user secrets live outside the repo in a per-machine folder, so
  a value only one machine has looks like a bug on every other machine and in production.
  ⚠️ Not yet true — `PoPunkouterSoftware.API.csproj` still declares
  `<UserSecretsId>popunkouter-software-api</UserSecretsId>`, and ASP.NET auto-loads that store in
  Development. Removing it is a live config change, so it needs the owner's go-ahead.
- **Answers over ~100 words end with a 20-word TLDR.**
- **`docs/` is not project documentation.** The rule of thumb "check the root DOCS folder for a
  project summary" does not pay off in this repo: [docs/](docs/) is the GitHub Pages site — a
  landing page, `style.css`, per-app privacy policies and store art for PoBox, PoCross, PoDance,
  PoFlag, PoFootball, PoRacer, PoSoccer and PoSumo, none of which are in this portfolio. There is no
  project summary in it. **This file is the summary.**

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

# Rebuild the icon-font subset after adding an icon name (see "Things that will bite you").
python SCRIPTS/Build-IconFontSubset.py       # needs: pip install "fonttools[woff]" brotli

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
| [src/PoPunkouterSoftware.Client/](src/PoPunkouterSoftware.Client/) | Blazor WASM (Radzen, mobile-first). `wwwroot` lives **only** here. The layout is **flat** — every component sits in the project root, named for what it is; there is no `Components/` tree. `wwwroot/js` holds the graphics and audio layers — see [Graphics and audio](#graphics-and-audio) below. |
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
Two of those three draw with `MetricBars` — plain HTML/CSS rows — not `RadzenChart`. Both series
are a **top-N ranking** (`Take(5)` / `Take(6)`, pre-sorted server-side), and a chart was the wrong
shape for one three times over: it rebuilt an SVG scene graph on every parent render, it carried a
fixed pixel height with a horizontal category axis that could not survive 390px without an
`overflow-x` escape hatch, and it spent most of its ink on axis furniture around five numbers that
are now simply printed on the row. Reach for `RadzenChart` when a series is continuous
(`AzureHistoryDisclosure` still does); reach for `MetricBars` when it is a ranking.
There used to be a second grid and a fourth card: a donut restating the healthy/total ratio the hero
tile already gives as a percentage — the same number appeared in the hero, the card subtitle and the
donut centre — and a cost chart that drew `CostHistory`, the exact series the hero's cost sparkline
draws. Trend belongs in the sparkline, breakdown in the chart, ratio in the hero. `OpsSummary` lost
`FleetHealth` with the donut; if you add a field to the first-paint contract, make sure nothing on
the page already renders it.

**One design system, three dials.** Everything visual resolves from the token block at the top of
`modern-ui.css`, and three of those tokens are the only places a global visual decision is made:

- **`--app-density`** scales the whole spacing ladder (`--app-space-*` are all
  `calc(<base> * var(--app-density))`). Compacting the UI for a viewport is one declaration —
  0.9 at 820px, 0.78 at 640px — not a per-component `padding` override repeated in every
  breakpoint of every stylesheet, which is what it replaced.
- **`--app-step--2 … --app-step-5`** is the fluid type ladder. Every font-size above ~0.9rem
  resolves from a step. Do **not** write a new `clamp()`: the catalogue h1 grew at `5vw` while the
  dashboard hero grew at `3vw`, so the two pages' headings crossed over somewhere mid-range and
  neither size was ever chosen. Density does not scale type — the ladder already shrinks with the
  viewport and multiplying the two compounds into unreadable text.
- **`--app-viewport-fit`** is `100dvh` minus the fixed chrome and shell padding: what a
  full-height pane sizes itself to.

`--app-text-soft` is a **measured** value, not a taste call, in both themes: it is applied to the
smallest type in the app, so re-measure it against `--app-surface` (the darker of the two grounds
these sit on, and the one `[data-glass]` makes translucent), not against `--app-bg`.

**`/azure` pages instead of scrolling at mobile portrait; `/` guarantees its first screen.** The
two routes answer "fit the viewport" differently on purpose. `/azure` is pane-shaped — status,
what changed, spend, uptime, actions — so `[data-snap-pager]` makes it a one-viewport-tall
scroll-snap container whose `.app-pane` children are a screen each; `js/helpers.js`
(`appSnapPager`) injects the dot strip, driven off the **computed** `scroll-snap-type` so the CSS
media query stays the single source of truth for when paging is on. `/` is an 11-item catalogue —
a list, and paging a list fights the reader — so it keeps ordinary scrolling and instead
guarantees the first screen is complete. **Neither route truncates.** The one body of content that
cannot honestly be compressed to a screen (advanced diagnostics) opts out with `.app-pane--tall`
and scrolls internally. Both contracts are held by `PortfolioUiTests`
(`Azure_MobilePortrait_PanesFitTheViewport`, `Home_FirstScreen_ShowsAWholeCard`) — a new section
dropped into whichever pane is nearest will fail the first of those.

**Two big types are partial classes split by concern.** Find the concern, not the file:

- `AzureReportService.cs` — orchestrator + primary constructor, declared there **and only there**.
  Steps live in `.Discovery`, `.Metrics`, `.Cost`, `.Security`, `.Inventory`, `.Cleanup`,
  `.GitHubCorrelation`, `.Helpers`.
- `AzureDashboard.razor.cs` — state, lifecycle, loading, refresh, SignalR. Display mapping in
  `.Presentation`, history in `.Trends`. The pure projections are NOT a partial-class aspect any
  more: they live in `DashboardDerivations.cs`, a plain `public static` class, because as private
  members of the component ~460 lines of impact scoring, actionability tiering and cleanup
  reasoning were unreachable from a test even though both test projects reference this assembly.
  `.DerivedViews` keeps only `CleanupCandidates`, which reads the page's snooze set and so is not
  pure. Anything pure belongs in `DashboardDerivations`, with a test in
  `DashboardDerivationsTests`. Markup blocks are
  sibling components in the client root (`AzureStatusNarrative`, `AzureWhatChanged`,
  `AzureCostForecast`, `AzureUptimeHeatmap`, `Sparkline`, `AzurePriorityQueue`,
  `AzureResourceExplorer`, `AzureEvidenceDisclosures`, `AzureHistoryDisclosure`,
  `AzureSnoozedItems`).

## Graphics and audio

Eight files in [wwwroot/js/](src/PoPunkouterSoftware.Client/wwwroot/js/). Read the header
comment in each before changing it; they document the reasoning, this is the map.

| File | Role |
|---|---|
| `motion-kit.js` | **The frame governor.** ONE `requestAnimationFrame` loop for the whole app, one gate (reduced-motion / hidden / blur / no runnable subscriber), and an adaptive quality controller. Must load first. |
| `audio-kit.js` | Programmatic Web Audio — synthesis only, **zero audio assets**. UI SFX, the refresh drone, the `/azure` sonification, and the analyser tap the shaders read. |
| `gpu-backdrop.js` | Orchestrator for `#app-gpu-backdrop`: tier selection, colour tokens, glass rects, audio energy. Owns no pixels. |
| `gfx-webgl.js` | WebGL2 renderer (curl-noise field, transform-feedback particles, dual-Kawase blur, glass composite) with a WebGL1 field-only fallback. **The top of the ladder.** |
| `starfield-backdrop.js` | Catalog-page Three.js layer: starfield, globe, and the telemetry-driven orbit field. Lazily fetches Three.js. |
| `helpers.js` | Topbar, snap pager, clipboard, download. Unrelated to the above. (The `appMedia` matchMedia bridge went with the resource explorer's data-grid branch — it had exactly one caller.) |
| `app-boot.js` | Host-page boot: dismisses the loading splash, drives the nav progress bar, filters hot-reload console noise. Not an animation layer — no rAF. Was inline in `App.razor` until the CSP banned inline script. |
| `blazor-hooks.js` | Registers the enhanced-navigation callbacks. Must load **after** `blazor.web.js`, which defines `Blazor`. Also ex-inline. |

**One rAF loop, and reduced motion stops it.** Every animated layer is a named `motionKit`
subscriber. Nothing else may call `requestAnimationFrame` for animation. The governor's gate
is the *only* pause path and it truly cancels the callback — a layer that fades to
`opacity: 0` while still being scheduled still burns a phone's battery, so
`motionKit.stats().running` is what the E2E test asserts. It also measures its own dispatch
cost and steps render scale and particle count down when the device cannot afford them
(budget 6ms; both GPU tiers measure ~3ms on a desktop). The catalog starfield claims
exclusivity over the shared backdrop with `motionKit.suppress('app-gpu-backdrop')` — that
replaced a `data-suppress-gpu-backdrop` attribute plus a MutationObserver.

**Silence is the default and it is an accessibility floor.** `audioKit.enabled` starts false;
the `AudioContext` is not even *constructed* until a user gesture, because one built during
page load is a suspended context that silently swallows sound plus an unrequested hardware
claim. The header toggle is wired by **delegation on `document`** (enhanced navigation
re-inserts the header, so a direct listener would be lost or double-bound), as are
`[data-sfx]` / `[data-sfx-hover]`. Sound carries information in exactly two places — the
refresh lifecycle (a ~30s scan is exactly how long it takes someone to switch tabs) and the
`/azure` sonification, which is a second *modality* on facts already on the page, not a
second copy of them. Only primitives cross the interop boundary.

**The backdrop is a capability ladder**: WebGL2 → WebGL1 → the CSS grid in
`modern-ui.css`. Every rung degrades silently to the next and records the outcome in
`#app-gpu-backdrop`'s `data-gpu`. A **WebGPU rung sat on top until 2026-09-05**
(`gfx-webgpu.js`, 524 lines, compute-shader particles, lazily fetched behind
`navigator.gpu`) and was removed: a second complete renderer, in a second shader language,
for a decorative layer whose WebGL2 rung draws the same thing at the same measured ~3ms.
Two implementations of one decoration is the most expensive code in the repo to keep
honest, because neither can be verified except by looking at pixels. Re-adding it also
means re-adding the canvas swap it forced — `getContext` is sticky per element, so a canvas
offered to WebGPU can never yield a WebGL context, and `build()` had to clone and replace
the node mid-init (reassigning `canvas` *before* `status()` ran, or the diagnostic landed
on a detached node). `build()` is synchronous now.

**`data-glass` is the opt-in for shader-side glassmorphism.** `gpu-backdrop.js` collects the
on-screen rect of every tagged element (rate-limited, signature-guarded, corner radius cached
per element — `getComputedStyle` forces a style resolution and was the largest single cost)
and the renderer blurs and refracts the backdrop beneath them. This is what replaced the 23
per-card `backdrop-filter` roots: one blur for the whole page, in a pass that already runs.
CSS's only job is `html[data-gpu-backdrop] [data-glass]`, which makes those surfaces
translucent enough for it to show — gated so that a machine with no WebGL does not get
see-through cards over a flat background.

### Three bugs here that all failed silently — check for them before adding shader code

1. **An opaque `<body>` background hid every backdrop layer.** `#app-gpu-backdrop` and the
   `body::before` grid are both `position: fixed` with **negative** z-index, and negative-z
   descendants paint *before* an ancestor's in-flow block background. So `body` painting a
   colour covered both. The page colour now lives on `html`, and `html body` carries
   `background: transparent !important` — the `!important` is needed to beat something in the
   Radzen theme sheets. Proven by forcing the composite shader to solid opaque red and
   watching the page still render navy.
2. **`smoothstep(hi, lo, x)` is undefined** in both GLSL and WGSL when `edge0 > edge1`. It
   compiles, the layer reports itself healthy, and the driver may return zero everywhere.
   Always write `1.0 - smoothstep(lo, hi, x)`.
3. **`curl()` returns a true derivative** (the finite difference is divided by `2*epsilon`),
   so its magnitude is ~7, not ~1. Multiplying it by 0.085 displaced every sample point
   outside its own gaussian falloff and the whole field evaluated to zero — again with no
   error and no artefact. Check the *magnitude* of a noise term against the coordinate range
   it is perturbing.

The common thread: a decorative GPU layer has no failure mode that surfaces on its own. If
you change a shader, look at the rendered pixels — `dataset.gpu` reporting `webgl2` only
proves a context and program were created, not that anything reached the screen.

## Cross-cutting decisions

- **A projection component gets a `ShouldRender` guard.** Blazor treats a reference-typed
  parameter as "may have changed" on every parent render, so without one, a page render walks
  every child — 23 portfolio cards, a 30-day uptime grid, ~48 resource explorer cards. The
  guard is reference identity on the data (`AzureUptimeHeatmap`, `AzureCostForecast`,
  `AzureWhatChanged`, `AzurePriorityQueue`, `MetricBars`, `PortfolioAppCard`) plus any scalar that
  changes what is drawn (`AzureResourceExplorer` also compares `SelectedView`).
  This is sound only because the page **replaces** its DTOs rather than mutating them. Two rules
  follow: never include a `Func`/`EventCallback` parameter in the comparison — those are fresh
  objects every render and would make the guard a no-op — and **never add internal state to a
  guarded component**, because `ShouldRender` is consulted on its own `StateHasChanged` too and
  would block it. `AzureStatusNarrative` is deliberately unguarded for exactly that reason.
- **A hot signal must not re-render the page that shows it.** `POST /api/diag/refresh` streams ~20
  progress messages over ~30 seconds; each one used to call `StateHasChanged` on `AzureDashboard`.
  The strip lives in `AzureRefreshProgress` and the hub calls its `Update()` method directly — a
  method, not a `[Parameter]`, because setting a parameter requires the parent to render, which is
  the whole cost being removed. The page still renders on the transition that ends the scan.
- **Decorative layers ask the device first.** `motionKit.minimal` (Save-Data, `deviceMemory <= 2`,
  `hardwareConcurrency <= 2`, 2G) is a separate question from the quality tier: the tier says *how
  well* to draw, `minimal` says *whether to*. Under it the starfield skips its ~600KB Three.js
  fetch entirely and the GPU backdrop never sets `data-gpu-backdrop`, so the CSS fallback stays.
  Both hint APIs are Chromium-only — every check is written so a missing hint reads as "no
  objection". The default is to draw.
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
- **`<RadzenComponents />` in `MainLayout` carries its own `@rendermode`, and must.** It hosts the
  notification/dialog/tooltip outlets that `NotificationService` pushes into. Without a render mode
  it inherits the layout's static SSR: the `.rz-notification` container sits in the DOM but can
  never gain a child, and the page's `NotificationService` — resolved from the WASM container, a
  different one from the server's — pushes into an outlet that is not listening. Every toast in
  the app was silently swallowed, *every error path included*: "Refresh rejected", "Refresh
  failed", "Timeout", "Snooze failed", "Copy failed". A 500 from `POST /api/diag/snooze` produced
  no DOM change and no message at all. Interactive WASM islands on one page share a single
  WebAssembly host and therefore one DI container (a scoped service is a per-host singleton there),
  which is why the island and the page resolve the same instance. Do not remove the attribute.
- **Management gate.** Mutating/expensive endpoints (`/api/diag/refresh`, `/api/diag/cancel-refresh`)
  carry `.RequireManagementActions()`, which enforces `FeatureFlags:EnableManagementActions` (on in
  Development/Testing, otherwise opt-in) plus an optional `Security:ManagementApiKey` via the
  `X-Management-Key` header. `/api/diag/ai` and the snooze endpoints are deliberately unprivileged.
- **Routing.** `/api/diag/*` and `/api/portfolio/*` use `MapGroup`. `/health` (deep probe, one
  `IHealthCheck` per external dependency) and `/healthz` (static liveness) sit off the `/api` group.
  **`/openapi/v1.json` and `/scalar/v1` are mapped only outside Production** — they were
  unconditional, so the live site published its full route table, parameter shapes and response
  schemas (management routes included) to anonymous callers, for an API whose only client is
  compiled from this same solution.
  There is deliberately **no** `/api/health` alias, and deliberately **no** bare `/diag`: it was an
  unauthenticated page rendering ~130 lines of hand-built HTML with its own dark-only palette — a
  second design system — whose only caller was its own smoke test, and whose `?format=json` twin
  returned the server's absolute ContentRoot path, the environment name and masked-but-suffixed
  connection strings to anonymous callers in Production. Removed 2026-09-04.
- **A health check does the real operation, with the app's own credential.** Every check used to be
  an anonymous `HttpClient.GetAsync` at the dependency's URL, which proves DNS and TLS and nothing
  else: Key Vault read "healthy" off an HTTP **404** and Table Storage "degraded" off a **400**,
  both meaningless, through a period when the app could not read a single Azure resource. They now
  list one page of secret properties and query one table row. `AzureInventory` is the check that
  would have caught it: ARM answers an *unauthorized* list with an **empty page, not a 403**, so a
  scan with no Reader role completes and stores a report describing zero resources — the dashboard
  then renders truthful zeros and nothing anywhere reports a fault. **No check returns Unhealthy**,
  deliberately: `MapHealthChecks` answers Unhealthy with 503, and this app has no hard dependency
  (see "Degradation is a designed path"). A fault shows as `"status": "degraded"` plus the check's
  own verdict. The masked `config` block is a development diagnostic and is **omitted in
  Production** — `/health` is anonymous, and it was publishing the environment name and a
  masked-but-suffixed vault URI whose last four characters name the vault.
- **`/api/diag/report` masks the subscription id.** It is anonymous by necessity (the Advanced
  diagnostics panel is WASM with no credential) and returns every resource id in the estate.
  `SecretMasking.MaskSubscriptionIds` rewrites `/subscriptions/<guid>` to `/subscriptions/****` on
  the serialized JSON — on the JSON, not the object graph, because the id appears inside free-text
  strings across a dozen nested record types and a per-record rewrite would miss whichever one is
  added next. Resource groups and names stay: they are already public in the hostnames the
  portfolio links to. Use `MaskedJson`, never `Results.Json`, for a report in that slice.
- **Security headers come from `Host/SecurityHeaders.cs`, and the CSP forbids inline script.**
  They lived in `wwwroot/staticwebapp.config.json` — Static Web Apps configuration, in an App
  Service app, read by nothing — so production sent no CSP, no `nosniff` and no Referrer-Policy
  while a file in the repo said otherwise. A control that exists only in an unread file is worse
  than none, because it stops anyone noticing the gap. `script-src 'self' 'wasm-unsafe-eval'`
  carries **no** `'unsafe-inline'`, which is why App.razor's boot and Blazor-hook blocks are now
  `js/app-boot.js` and `js/blazor-hooks.js`: **a new inline `<script>` in the host page will
  silently stop running** — put it in a file under `wwwroot/js`. `style-src` keeps `'unsafe-inline'`
  because Radzen and Blazor both write inline style attributes. `X-Powered-By` is stripped by
  `web.config`, not middleware: on Windows App Service IIS appends it downstream of the managed
  pipeline, so a middleware `Remove()` is a no-op that reads like a fix.
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
  - **Both writers must also agree on what "up" means.** They did not: the pinger counted anything
    under 500 as reachable while the scan's `ProbeUrlAsync` judged on `IsSuccessStatusCode` alone.
    The probe sends HEAD with `AllowAutoRedirect = false`, so an app that redirects `/` to a
    sign-in page answers 302 — and PoRedoImage and PoRepoLineTracker were both reported
    "unavailable" on the home page while serving normally. The scan now counts any status under
    400 as reachable. Keep 4xx broken: a card is a link a visitor clicks, and PoSeeReview's flat
    403 is a real outage, not an auth handshake.
  - The pinger stops when the app does (F1 has no Always-On), which is why
    `.github/workflows/uptime-scan.yml` exists — see CI/CD below.
- **Secrets.** Key Vault `kv-poshared`, prefix `PoPunkouterSoftware--`, loaded at startup via
  System-Assigned Managed Identity, and **skipped entirely under the `Testing` environment**. The
  grant is a classic access policy, not RBAC — the vault is shared across Po* apps, so switching
  models is an estate-wide change.
- **The managed identity needs subscription-wide read, and that is not optional.** `infra/main.bicep`
  granted the site's identity Key Vault (access policy) and Cognitive Services (RBAC) and nothing
  else, so the two data sources the whole app exists to read were the two it could not touch:
  production reported 0 services, 0 resources, `$0.00` and an empty uptime grid while every health
  check showed green. `modules/subscription-inventory-reader.bicep` assigns **Reader**, **Cost
  Management Reader** (a separate data plane — Reader does not cover
  `Microsoft.CostManagement/query/action`, which is why the report said "Cost data unavailable
  (rate-limited or request failed)") and **Monitoring Reader** at subscription scope; main.bicep
  assigns **Storage Table/Blob Data Contributor** on the app's own account. Infrastructure is
  applied out-of-band — `deploy.yml` deliberately does not run bicep — so editing these files
  changes nothing until someone runs `az deployment group create`. All six assignments were
  applied on 2026-09-05. **Run `what-if` before ever deploying the whole template**: it reported
  `tags -> None` on the site, because the portal-written
  `hidden-link: /app-insights-resource-id` was not declared and an omitted `tags` block deletes
  the tag. It is declared now (`appInsightsResourceId`), but the lesson generalises — this file
  claims to describe what already exists, and anything the portal wrote that it does not name,
  it deletes.
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
  [infra/main.bicep](infra/main.bicep). There is no incident log: `IncidentService` wrote a row per
  health transition on every scan, kept them forever, and **nothing ever read them** — no endpoint,
  no DTO on the wire, no UI. "What changed since last scan" is the feature it looked like, and
  `DashboardInsightsBuilder.BuildDelta` already provides that from history. Removed 2026-09-04
  along with `IncidentEntry`, `IncidentTypes`, the `incidents` partition and the never-configured
  `Incidents:WebhookUrl`. The `incidents` table already in Azure is untouched; drop it by hand.
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
- **The whole app has one rAF loop.** It lives in `js/motion-kit.js`. Do not add another —
  see [Graphics and audio](#graphics-and-audio).
- **The icon font is a SUBSET, and adding an icon means rebuilding it.** Radzen.Blazor ships the
  Material Symbols variable font (~3,600 glyphs, 1,068,920 bytes) and self-hosts it as family
  `"Material Symbols"`. This app draws about thirty of those glyphs, so
  [SCRIPTS/Build-IconFontSubset.py](SCRIPTS/Build-IconFontSubset.py) builds
  `wwwroot/fonts/material-symbols-subset.woff2` (16,000 bytes) and `modern-ui.css` declares the
  family against that. **Material Symbols renders through LIGATURES** — the element's text is the
  icon's name — so a name missing from the subset does not degrade to a blank box: it renders the
  literal word "refresh" inside the button. Add the name to `APP_ICONS` and re-run the script; it
  verifies every ligature survived and fails loudly if one did not, and
  `Azure_EveryIcon_RendersAsAGlyphNotItsName` catches a miss in a real browser. Two subtleties the
  script documents: this font's features are `rlig`/`rclt`, not `liga`, and `--text` alone saves
  only 11% because all 3,600 names share the same 26 letters, so unused ligatures must be pruned
  out of GSUB before subsetting. Also: do not re-add the Google Fonts `<link>` `App.razor` used to
  carry, and keep the `@font-face` in `modern-ui.css` rather than inheriting Radzen's — theirs are
  `media`-scoped by colour scheme, an `@font-face` in a non-matching media context does not apply,
  and ours coming later in the cascade is what stops the 1 MB file being fetched at all.
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
- **A global rule that must beat a page-root scoped rule needs (0,2,1), not (0,2,0).** A page root
  such as `.azure-ops-page` carries a scoped rule compiling to `.azure-ops-page[b-hash]` — (0,2,0)
  — and `App.razor` loads `PoPunkouterSoftware.Client.styles.css` **after** `modern-ui.css`, so a
  global selector that merely ties loses the tie on document order. That is why every selector in
  the `[data-snap-pager]` block is written `html .app-page[data-snap-pager]`. Without the leading
  `html`, the scoped `display: grid` beat the pager's `display: block`, the six panes became six
  grid rows sharing 756px at 126px each, and all six rendered on top of one another with their text
  overlapping — on the phone layout, the only place the rule applies. It failed silently through
  four E2E assertions because the panes still existed, still snapped, and still reported a
  `scrollHeight` that fit inside the container; only their rendered height was wrong.
  `Azure_MobilePortrait_PanesFitTheViewport` now measures the box, not just the content.
- **`Testing` is the hermetic switch.** It skips Key Vault entirely. Integration tests boot the real
  entry point via `WebApplicationFactory`, and collections run sequentially (`AssemblyInfo.cs`)
  because concurrent entry-point boots race.
- **UI tests must cover both viewports.** `PortfolioUiTests` drives every test at mobile-portrait
  (390×844) and desktop-landscape (1440×1000) through the shared `Viewports` theory data. Visual
  parity is a hard rule, not a preference.
- **Screenshots pin `PLAYWRIGHT_BROWSERS_PATH`** to the persistent `%HOME%` share and install only
  the Chromium headless shell — the worker's ephemeral disk previously filled with a full Chromium
  download and broke every deployment (2026-07-10). Kill switch:
  `FeatureFlags:EnableScreenshots=false`. In-process capture is off in Production for that reason,
  and `appsettings.json` said the capture happened in `screenshots.yml` instead — **a workflow that
  did not exist**, so every production card rendered `screenshotUrl: null` for months. It exists now
  (see CI/CD below) and writes the same container, blob names and viewport as `AppScreenshotService`,
  so the two paths are interchangeable. Serving those blobs also needs `AzureBlobStorage:Endpoint`,
  without which `GetContainerAsync` returns null and stored images never appear —
  indistinguishable from never having captured any.

## Tests — four projects, one per tier (budget 100/50/25/25, currently 100/50/21/23)

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
  made "hermetic" untrue. The seed now **displaces** any file already there (restored on dispose):
  bailing out when one existed meant a developer who had run the app locally ran this whole tier
  against their own `App_Data` report — different names, different counts, a real subscription id —
  which is the same undeclared dependency one directory over. `ProductionBootTests` boots
  the entry point under `Production` with every external dependency blanked — the only coverage of
  that environment's hosting pipeline.
- **`PoPunkouterSoftware.E2EAPI`** / **`.E2EUI`** — pure HTTP and Playwright against a live instance
  via `BASE_URL`. On demand, not in CI.

## Git workflow — master only

**This repository uses `master` and nothing else.** Commit directly to `master`. Use another branch
only when the owner asks for one by name. Do not create feature branches, topic branches, or PR
branches on your own initiative, and do not leave one behind after a piece of work — if a branch
exists for any reason, delete it locally and on the remote once its commits are on `master`. There
is no review gate here to justify the indirection, and a stale branch on a solo repo is just a
second version of the truth.

This overrides any default "branch before committing to the default branch" habit.

**Committing is yours; pushing is the owner's.** Never `git push` unless the owner asks for it in
that turn, because **every push to `master` deploys to production** via
[deploy.yml](.github/workflows/deploy.yml). Commit freely, run the fast tier locally, then stop and
say the work is ready to push — the pipeline will not run the tests for you, and it will not ask
before shipping.

**CI/CD:** three workflows. Only `deploy.yml` runs tests, and only the Unit tier — the other
three tiers still run locally before a push.

- [deploy.yml](.github/workflows/deploy.yml) — build, **unit tests**, deploy. Target is App
  Service `app-popunkoutersoftware` via OIDC, no secrets in the workflow. Unit is the only tier
  that belongs in a deploy pipeline: no Docker, no network, no live host, ~0.5s for 100 tests
  against an assembly the job already built. Integration needs Testcontainers Azurite; both E2E
  tiers need a running app and a `BASE_URL`. Adding any of them would trade a fast gate for a
  slow flaky one and start blocking deploys on infrastructure rather than on code.
- [uptime-scan.yml](.github/workflows/uptime-scan.yml) — nightly (06:17 UTC) `POST /api/diag/refresh`
  so the uptime grid gets one data point per day even when nobody visits and the F1 site is asleep.
  It wakes the site first (cold start), treats 409 "already refreshing" as success, and then
  **confirms a fresh report actually landed** — a 202 only proves the scan started, and a scan that
  fails every night would otherwise show as a green workflow forever.
  **Requires** `FeatureFlags:EnableManagementActions=true` in Production plus a
  `Security:ManagementApiKey` matching the `MANAGEMENT_API_KEY` GitHub secret. `ManagementActionFilter`
  **fails closed** in Production when the flag is on without a key — that combination would leave a
  free, repeatable, ~30-second subscription scan open to anonymous callers.
  ⚠️ **This ran red every night from at least 2026-08-29 to 2026-09-05** because neither setting was
  ever applied: `/api/config` reported `managementActionsEnabled: false`, so `/api/diag/refresh`
  answered 403 to a correct key and an absent one alike, and the workflow's own 403 branch fired
  nightly. Nothing else schedules a scan, so the consequence was not a missing data point — it was
  an empty `History` table (`/health` showed `TableStorage: readable, rowsVisible: 0`), no uptime
  grid, and a report that only ever refreshed when a visitor happened to load one past the 12h
  staleness line. **A red scheduled workflow here means the dashboard has no data**, not that a
  nightly nicety was skipped. Check its run history before believing the grid.
- [screenshots.yml](.github/workflows/screenshots.yml) — nightly (04:40 UTC) Playwright capture of
  every card's URL at 390×844, uploaded to the `app-screenshots` blob container under the host name
  the card looks up. Exists because in-process capture cannot run on the Windows F1 sandbox.
  Uploads with the storage **account key**, resolved at run time through ARM by the same OIDC
  identity `deploy.yml` uses — Contributor on the resource group covers `listKeys`, so this needs
  no new role assignment and no new secret. It was written with `--auth-mode login` and no
  matching data-plane grant anywhere, which would have failed on its first run. The stricter
  option is `--auth-mode login` plus **Storage Blob Data Contributor** scoped to the container;
  prefer it if you know the identity's object id.
