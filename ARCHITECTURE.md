# Architecture

The solution is split into four runtime layers:

- `PoPunkouterSoftware`: ASP.NET Core composition root, HTTP endpoints, static delivery, and server-side feature slices.
- `PoPunkouterSoftware.Client`: Blazor WebAssembly pages and presentation logic.
- `PoPunkouterSoftware.Shared`: immutable contracts shared between server and browser.
- `PoPunkouterSoftware.Infrastructure`: Azure adapters, report collection, persistence, telemetry, and analysis.

Azure reporting flows from `AzureReportService` to `AzureReportStore`, then through `/api/diag/*` endpoints to the hidden `/azure` dashboard. Long-running refresh state is published over SignalR. Downloadable local automation remains an inert text asset on the server; Azure authentication and destructive commands execute only on the user's machine. Generated cleanup commands are commented out by default.

