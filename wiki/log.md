# Project Wiki Log

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