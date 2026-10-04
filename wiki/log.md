# Project Wiki Log

## [2026-10-04] lint | Reconcile docs and wiki with Phase 0 security fixes
- Trigger: stale-documentation cleanup on `docs/cleanup-stale-reviews` after the Phase 0 IdP security fixes (C1–C18, docs/oidc-idp-assessment-2026-10-04.md)
- Sources consulted: `git log master..HEAD`, ClientStore, TokenHandler, AuthorizeRequestResolver, ResourceIndicatorPolicy, Handlers/Logout/*, WebAuthnHandler, AuthOptions, ForwardedHeadersConfigurator, AppHost.cs, docker-compose.dev.yml, ApiService Program.cs
- Pages updated: overview.md, index.md, concepts/oidc-protocol-surface.md, concepts/backchannel-logout.md, concepts/auth-persistence-model.md, concepts/deployment-modes.md, concepts/example-applications.md, entities/mrwhooidc-auth.md, entities/mrwhooidc-webauth.md, entities/mrwhooidc-apiservice.md, entities/mrwhooidc-apphost.md
- Structural changes captured: public-only credential-less client auth and /token auth-method/grant enforcement; single-use client-bound PAR and RequirePar; deny-by-default introspection; resource allow-list; verified-subject logout targeting; all-tenant background jobs; fail-closed JWE/JARM; WebAuthn UV gating; persisted auth-code login context; private JWKs kept out of Redis; fixed stale related_files (AppHost.cs, Handlers/Logout) and the ApiService role
- Curated docs reconciled alongside: docs/developer-guide.md (rewritten, 1027 → ~180 lines), docs/admin-guide.md, docs/example-applications-guide.md, docs/for-developers/quickstart-15-min.md, docs/for-administrators/webauthn.md, docs/copilot-instructions.md, .github/copilot-instructions.md
- Thin areas: no dedicated pages yet for client authentication policy, PAR, or CIBA/device flows
- Total pages touched: 12

## [2026-10-04] ingest | Stabilize development container startup
- Trigger: development Compose runtime warnings and dependency/build warning maintenance
- Sources consulted: docker-compose.dev.yml, WebAuth startup and AutoSeedMiddleware, example Data Protection configuration, docs/docker-compose-examples.md
- Pages updated: concepts/deployment-modes.md
- Structural changes: opted-in seeding before background services; shared example key-ring configuration; separate persistent encrypted example key-ring volumes
- Verification: warning-free Release build, full .NET tests, browser login flows, and cookie persistence after OidcDemo container recreation
- Total pages touched: 2

## [2026-07-23] ingest | Add client-bound delegated access
- Trigger: implementation of user-to-user delegation bound to an OAuth/OIDC client
- Sources consulted: delegated access implementation plan, grant persistence, token exchange, RazorClient, TestApi, and focused E2E coverage
- Pages updated: concepts/auth-persistence-model.md, concepts/oidc-protocol-surface.md, concepts/example-applications.md
- Structural changes: required client selection for new grants, explicit `delegation_id` token exchange, dual-identity/client claims, RazorClient delegated exchange demo
- Total pages touched: 4

## [2026-04-22] init | Bootstrap MrWhoOidc project wiki
- Trigger: initial project-wiki setup
- Sources consulted: README.md, docs/index.md, .github/copilot-instructions.md, repository structure, and existing project guidance
- Pages created: overview.md, schema.md, concepts/oidc-protocol-surface.md, concepts/backchannel-logout.md, concepts/deployment-modes.md, concepts/testing-strategy.md, entities/mrwhooidc-auth.md, entities/mrwhooidc-webauth.md, entities/mrwhooidc-apphost.md, entities/mrwhooidc-cli.md, entities/e2e-test-suite.md
- Pages updated: index.md
- Thin areas to expand later: MrWhoOidc.ApiService, MrWhoOidc.Security, example applications, and deeper data-model pages
- Total pages touched: 12

## [2026-04-22] ingest | Expand initial thin wiki areas
- Trigger: follow-up wiki expansion after bootstrap
- Sources consulted: docs/example-applications-guide.md, Examples/MrWhoOidc.RazorClient/README.md, MrWhoOidc.ApiService/Program.cs, MrWhoOidc.Security/DPoP.cs, MrWhoOidc.Security/DPoPProofGenerator.cs, MrWhoOidc.Auth/Persistence/AuthDbContext.cs
- Pages created: concepts/example-applications.md, concepts/auth-persistence-model.md, entities/mrwhooidc-security.md, entities/mrwhooidc-apiservice.md
- Pages updated: overview.md, index.md
- Thin areas to expand later: example-by-example deep dives, auth entity relationship pages, and additional service/entity coverage beyond the core projects
- Total pages touched: 6