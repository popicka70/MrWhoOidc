# Project Wiki Index

## Overview

- [Project Overview](overview.md) - Repo purpose, tech stack, architecture map, and runtime modes (updated 2026-10-04)
- [Schema](schema.md) - Wiki conventions, source-of-truth rules, and refresh heuristics (updated 2026-04-22)

## Concepts

- [OIDC Protocol Surface](concepts/oidc-protocol-surface.md) - Discovery, authorization, token, userinfo, and logout surfaces, client-auth/policy enforcement, plus layer boundaries (updated 2026-10-04)
- [Backchannel Logout](concepts/backchannel-logout.md) - Verified-subject logout targeting, durable outbox, and cross-tenant dispatch (updated 2026-10-04)
- [Deployment Modes](concepts/deployment-modes.md) - Seeded local Docker, Aspire AppHost, and production container deployment (updated 2026-10-04)
- [Testing Strategy](concepts/testing-strategy.md) - Unit, integration, browser E2E, and sample-app validation coverage (updated 2026-04-22)
- [Example Applications](concepts/example-applications.md) - Sample clients and APIs, their scenarios, and how they map to local workflows (updated 2026-10-04)
- [Auth Persistence Model](concepts/auth-persistence-model.md) - Main persistence domains in `AuthDbContext` and what they imply about the product surface (updated 2026-10-04)

## Entities

- [MrWhoOidc.Auth](entities/mrwhooidc-auth.md) - Core OIDC domain, persistence, crypto, and key management project (updated 2026-10-04)
- [MrWhoOidc.WebAuth](entities/mrwhooidc-webauth.md) - HTTP surface, discovery, protocol endpoints, and admin UI host (updated 2026-10-04)
- [MrWhoOidc.Security](entities/mrwhooidc-security.md) - Shared security helpers, with DPoP proof generation and validation primitives (updated 2026-04-22)
- [MrWhoOidc.AppHost](entities/mrwhooidc-apphost.md) - Aspire orchestration entry point for local development (updated 2026-10-04)
- [MrWhoOidc.Cli](entities/mrwhooidc-cli.md) - Administrative CLI for login, tenant operations, export/import, and automation (updated 2026-04-22)
- [E2E Test Suite](entities/e2e-test-suite.md) - Python Playwright coverage with screenshot-based evaluation and protocol helpers (updated 2026-04-22)

## Queries

- No filed query pages yet. Add durable synthesis answers under `wiki/queries/` and list them here.