# Test Strategy

This repository uses a tiered test model:

- Unit: fast logic checks in tests/unit
- Integration: data contract and cross-module checks in tests/integration
- E2E: Playwright browser paths in e2e

## Commands

- npm run test:unit
- npm run test:integration
- npm run test:e2e:smoke
- npm run test:e2e:regression
- npm run test:ci
- npm run test:trend
- npm run test:quality-gate
