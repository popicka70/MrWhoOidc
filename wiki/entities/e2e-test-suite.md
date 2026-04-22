---
title: E2E Test Suite
type: entity
tags: [e2e, playwright, python, quality]
created: 2026-04-22
updated: 2026-04-22
related_files:
  - e2e/README.md
  - e2e/conftest.py
  - e2e/tests/test_admin_pages.py
  - e2e/tests/test_oidc_flows.py
---

The `e2e/` project is a separate Python test workspace that exercises the full WebAuth UI and protocol surfaces through Playwright and helper utilities. It is distinct from the .NET solution and has its own environment setup rules.

## Responsibilities

- Run authenticated browser coverage across public, account, tenant-admin, and platform-admin pages.
- Exercise protocol flows such as auth code with PKCE, client credentials, token exchange, and DPoP.
- Capture screenshots and generate scored HTML reports.
- Drive example-app and CLI-oriented end-to-end coverage.

## Notes

- The canonical virtual environment lives at `e2e/.venv`.
- The suite shares authenticated browser state across tests and writes reports under `e2e/reports/`.
- Changes in the UI surface or example apps often require synchronized wiki and E2E updates.

## Related Pages

- [[testing-strategy]]
- [[mrwhooidc-webauth]]
- [[oidc-protocol-surface]]