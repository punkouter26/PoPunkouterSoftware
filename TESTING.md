# Testing

- Unit tests cover pure analysis and generation behavior.
- Integration tests use `WebApplicationFactory<Program>` in the `Testing` environment and must not contact real Azure services or Key Vault.
- E2E tests use Playwright and can start the app on the configured local HTTP port.
- Run `dotnet test` for affected test projects, then `dotnet build PoPunkouterSoftware.sln --no-restore`.
- Browser-facing changes require runtime verification for the DOM, console, network, keyboard access, and responsive layouts.

