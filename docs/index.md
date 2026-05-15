# MrWhoOidc Documentation Hub

This hub points to the active documentation path for the current codebase. Use it to choose the right starting point by role or workflow.

## Start Here

### Developers
- [for-developers/quickstart-15-min.md](for-developers/quickstart-15-min.md) - Getting started with the published image first and source builds second
- [troubleshooting/local-development.md](troubleshooting/local-development.md) - Local Docker, port, and certificate troubleshooting
- [developer-guide.md](developer-guide.md) - Integration guide for discovery, authorization, token exchange, JAR/JARM, and DPoP
- [example-applications-guide.md](example-applications-guide.md) - Demo applications, sample architecture, and which example to use
- [../e2e/README.md](../e2e/README.md) - Browser E2E test suite

### Operators
- [production-setup-guide.md](production-setup-guide.md) - Production bootstrap and cloud deployment basics
- [deployment-guide.md](deployment-guide.md) - Container deployment, environment variables, certificates, and operations
- [docker-compose-examples.md](docker-compose-examples.md) - Deployment variants and configuration patterns
- [upgrade-guide.md](upgrade-guide.md) - Upgrade and rollback procedures

### Administrators
- [admin-guide.md](admin-guide.md) - Admin UI, tenant configuration, user management, and provider workflows
- [../MrWhoOidc.Cli/README.md](../MrWhoOidc.Cli/README.md) - CLI administration and scripting
- [reference/obo-client-policy.md](reference/obo-client-policy.md) - OBO and token exchange policy guidance

### Security Teams
- [docker-security-best-practices.md](docker-security-best-practices.md) - Hardening guidance for containerized deployments
- [for-security-teams/incident-response.md](for-security-teams/incident-response.md) - Incident response procedures
- [oidc-conformance-checklist.md](oidc-conformance-checklist.md) - Protocol compliance checklist
- [oidc-openid-certification-readiness.md](oidc-openid-certification-readiness.md) - OpenID Foundation certification and conformance-suite readiness

## Common Workflows

### Local Development
1. Start with [for-developers/quickstart-15-min.md](for-developers/quickstart-15-min.md) and choose the published-image path first unless you are actively changing source code.
2. Use `docker-compose.dev.yml` only for the source-build contributor path.
3. Use [troubleshooting/local-development.md](troubleshooting/local-development.md) if Docker, ports, certificates, or startup timing cause issues.
4. Use [example-applications-guide.md](example-applications-guide.md) to choose a demo application.
5. Use [../e2e/README.md](../e2e/README.md) for browser tests.

### Production Deployment
1. Read [production-setup-guide.md](production-setup-guide.md) for first-run bootstrap requirements.
2. Use [deployment-guide.md](deployment-guide.md) for container deployment and operations.
3. Use [docker-compose-examples.md](docker-compose-examples.md) and [docker-security-best-practices.md](docker-security-best-practices.md) for environment-specific hardening.

### Examples and Demos
- [example-applications-guide.md](example-applications-guide.md) summarizes all example applications.
- [../Examples/MrWhoOidc.RazorClient/README.md](../Examples/MrWhoOidc.RazorClient/README.md) and [../Examples/MrWhoOidc.TestApi/README.md](../Examples/MrWhoOidc.TestApi/README.md) describe the primary .NET demo pair.
- [../Examples/ReactOidcClient/README.md](../Examples/ReactOidcClient/README.md) covers the SPA example.
- [../Examples/MrWhoOidc.GoWebClient/README.md](../Examples/MrWhoOidc.GoWebClient/README.md) and [../Examples/MrWhoOidc.GoApi/README.md](../Examples/MrWhoOidc.GoApi/README.md) cover the Go samples.

### CLI and Automation
- [../MrWhoOidc.Cli/README.md](../MrWhoOidc.Cli/README.md) covers CLI installation, authentication, and automation.
- [../e2e/README.md](../e2e/README.md) covers browser E2E and CLI-driven E2E flows.

## Reference and Deep Dives

### Protocol Reference
- [oidc-idp-feature-reference.md](oidc-idp-feature-reference.md)
- [reference/obo-client-policy.md](reference/obo-client-policy.md)
- [reference/obo-dpop-requiresamejkt-e2e.md](reference/obo-dpop-requiresamejkt-e2e.md)
- [reference/idp-chaining-client-configuration.md](reference/idp-chaining-client-configuration.md)
- [reference/jar-replay-cache.md](reference/jar-replay-cache.md)

### Architecture and Design
- [adr/](adr/)
- [oidc-feature-gap-analysis.md](oidc-feature-gap-analysis.md)
- [well-known-idp-providers-plan.md](well-known-idp-providers-plan.md)

### Operations and Security
- [for-operators/monitoring/alerting-rules.md](for-operators/monitoring/alerting-rules.md)
- [for-operators/backup-restore/verification-testing.md](for-operators/backup-restore/verification-testing.md)
- [for-security-teams/incident-response.md](for-security-teams/incident-response.md)

## Notes on Scope

- The most accurate source of truth for runtime behavior remains the code and compose files.
- Historical implementation notes, backlog documents, and archived assessments are intentionally not the primary entry path from this hub.
- Local development and production deployment are documented separately because the development stack auto-seeds data while production requires explicit bootstrap.

**Last Updated:** 2026-04-09
