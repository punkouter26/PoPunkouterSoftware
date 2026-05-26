# Copilot Instructions

Use `architecture.md` and `AGENT.MD` as primary context before making code changes.

## Project Rules
- Preserve Po-prefixed naming consistency.
- Keep diagnostics endpoints available but not linked from UI.
- Maintain strict build quality defaults from `Directory.Build.props`.
- Prefer minimal, targeted patches.

## Awesome Copilot Skill Flow
Use these skills in this order when they are available in your environment.

1. `acquire-codebase-knowledge`
2. `architecture-blueprint-generator`
3. `folder-structure-blueprint-generator`
4. `dotnet-best-practices`
5. `dotnet-design-pattern-review`
6. `autoresearch`
7. `security-review`
8. `appinsights-instrumentation`
9. `azure-deployment-preflight`
10. `azure-resource-health-diagnose`
11. `create-readme`
12. `repo-story-time`

## Major-Change Output Rule
For major changes, include:
- One Pro
- One Con
