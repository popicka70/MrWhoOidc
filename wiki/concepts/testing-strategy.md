---
title: Testing Strategy
type: concept
tags: [testing, unit-tests, e2e, quality]
created: 2026-04-22
updated: 2026-04-22
related_files:
  - README.md
  - MrWhoOidc.UnitTests/MrWhoOidc.UnitTests.csproj
  - e2e/README.md
  - e2e/conftest.py
  - e2e/tests/test_oidc_flows.py
---

The repository uses layered validation rather than relying on one giant test surface. Protocol and business behavior live primarily in .NET tests, while browser workflows and integration realism live in the Python Playwright suite.

## Layers

- `MrWhoOidc.UnitTests` covers token behavior, client store logic, consent, key rotation, PAR, token exchange, and DPoP-focused slices.
- The `e2e/` project drives authenticated browser flows, CRUD screens, CLI workflows, and OIDC protocol scenarios.
- Example applications and the sample API are part of the integration picture because they exercise the server from real client perspectives.

## Why It Matters

- UI regressions and configuration drift show up in Playwright.
- Protocol invariants and edge cases should stay cheap and deterministic in .NET tests.
- The wiki should be updated when new test layers appear, when coverage moves between layers, or when the test environment model changes.

## Related Pages

- [[e2e-test-suite]]
- [[oidc-protocol-surface]]
- [[deployment-modes]]