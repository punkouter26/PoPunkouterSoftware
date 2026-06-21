# Conventions

- Keep server features under `Features/<Feature>` and infrastructure adapters under `PoPunkouterSoftware.Infrastructure`.
- Keep shared API models dependency-free and immutable where practical.
- Use async APIs, cancellation tokens for external work, structured logging, and bounded network timeouts.
- Validate external input at HTTP and process boundaries; never return secret values.
- Keep destructive Azure operations opt-in and reviewable.
- Follow existing `app-*` CSS tokens and Radzen components for UI changes.
- Preserve strict build settings and avoid unrelated cleanup in feature changes.

