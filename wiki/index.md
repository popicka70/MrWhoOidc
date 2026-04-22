# Project Wiki Index

## Overview

- [Project Overview](overview.md) - Repo purpose, tech stack, architecture map, and runtime modes (updated 2026-04-22)
- [Schema](schema.md) - Wiki conventions, source-of-truth rules, and refresh heuristics (updated 2026-04-22)

## Concepts

- [OIDC Protocol Surface](concepts/oidc-protocol-surface.md) - Discovery, authorization, token, userinfo, and logout surfaces, plus layer boundaries (updated 2026-04-22)
- [Backchannel Logout](concepts/backchannel-logout.md) - Durable logout outbox, dispatch workflow, and sample RP integration shape (updated 2026-04-22)
- [Deployment Modes](concepts/deployment-modes.md) - Seeded local Docker, Aspire AppHost, and production container deployment (updated 2026-04-22)
- [Testing Strategy](concepts/testing-strategy.md) - Unit, integration, browser E2E, and sample-app validation coverage (updated 2026-04-22)

## Entities

- [MrWhoOidc.Auth](entities/mrwhooidc-auth.md) - Core OIDC domain, persistence, crypto, and key management project (updated 2026-04-22)
- [MrWhoOidc.WebAuth](entities/mrwhooidc-webauth.md) - HTTP surface, discovery, protocol endpoints, and admin UI host (updated 2026-04-22)
- [MrWhoOidc.AppHost](entities/mrwhooidc-apphost.md) - Aspire orchestration entry point for local development (updated 2026-04-22)
- [MrWhoOidc.Cli](entities/mrwhooidc-cli.md) - Administrative CLI for login, tenant operations, export/import, and automation (updated 2026-04-22)
- [E2E Test Suite](entities/e2e-test-suite.md) - Python Playwright coverage with screenshot-based evaluation and protocol helpers (updated 2026-04-22)

## Queries

- No filed query pages yet. Add durable synthesis answers under `wiki/queries/` and list them here.