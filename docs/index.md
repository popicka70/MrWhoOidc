# MrWhoOidc Documentation

Start with the guide for your task. Installation and configuration are covered here and in the repository [README](../README.md).

## Developers

- [for-developers/quickstart-15-min.md](for-developers/quickstart-15-min.md): get started with the published image first, source builds second.
- [developer-guide.md](developer-guide.md): integration guide for discovery, authorization, token exchange, JAR/JARM and DPoP.
- [example-applications-guide.md](example-applications-guide.md): demo applications and which one to use.
- [troubleshooting/local-development.md](troubleshooting/local-development.md): local Docker, port and certificate troubleshooting.
- [mailhog-local-dev.md](mailhog-local-dev.md) and [pgadmin-guide.md](pgadmin-guide.md): local mail capture and database tooling.
- [../e2e/README.md](../e2e/README.md): browser E2E test suite.

## Operators

- [production-setup-guide.md](production-setup-guide.md): production bootstrap and first-run requirements.
- [deployment-guide.md](deployment-guide.md): container deployment, environment variables, certificates and operations.
- [docker-compose-examples.md](docker-compose-examples.md): deployment variants and configuration patterns.
- [docker-security-best-practices.md](docker-security-best-practices.md): container hardening.
- [upgrade-guide.md](upgrade-guide.md): upgrade and rollback.
- [for-operators/client-secret-rotation.md](for-operators/client-secret-rotation.md) and [for-operators/key-rotation.md](for-operators/key-rotation.md): credential and key lifecycle.
- [for-operators/monitoring/alerting-rules.md](for-operators/monitoring/alerting-rules.md) and [for-operators/backup-restore/verification-testing.md](for-operators/backup-restore/verification-testing.md): monitoring and restore drills.
- [hybrid-cache-guide.md](hybrid-cache-guide.md) and [rate-limiting-dashboard.md](rate-limiting-dashboard.md): caching and rate limiting.

## Administrators

- [admin-guide.md](admin-guide.md): admin UI, tenant configuration, users and identity providers.
- [user-registration-and-enrollment.md](user-registration-and-enrollment.md): registration, invitations and tenant domain claims.
- [for-administrators/webauthn.md](for-administrators/webauthn.md): security-key enrollment, removal and recovery.
- [../MrWhoOidc.Cli/README.md](../MrWhoOidc.Cli/README.md): CLI administration and scripting.

## Security and compliance

- [oidc-idp-assessment-2026-10-04.md](oidc-idp-assessment-2026-10-04.md): **the single list of open findings and the roadmap.**
- [oidc-openid-certification-readiness.md](oidc-openid-certification-readiness.md): OpenID Foundation conformance status and how to re-run it. Conformance runs are not a certification.
- [for-security-teams/incident-response.md](for-security-teams/incident-response.md): incident response.
- [web-publication-checklist.md](web-publication-checklist.md): what the public website may and may not claim.

## Protocol reference

- [oidc-idp-feature-reference.md](oidc-idp-feature-reference.md): spec-based feature list for an OIDC IdP. This is a generic checklist, not the implementation status.
- [jar-jarm-guide.md](jar-jarm-guide.md)
- [reference/delegated-and-support-access.md](reference/delegated-and-support-access.md)
- [reference/obo-client-policy.md](reference/obo-client-policy.md) and [reference/obo-dpop-requiresamejkt-e2e.md](reference/obo-dpop-requiresamejkt-e2e.md)
- [reference/idp-chaining-client-configuration.md](reference/idp-chaining-client-configuration.md)
- [reference/jar-replay-cache.md](reference/jar-replay-cache.md)
- [reference/pairwise-subject-identifiers.md](reference/pairwise-subject-identifiers.md)
- [adr/](adr/): architecture decision records.

## Maintaining these docs

- **Code wins.** When a doc and the code or Compose files disagree, the code is what runs. Fix the doc in the same PR that changes the behaviour.
- **One place for findings.** Open defects, review findings and planned work go in the assessment. Do not add separate dated review, plan or status files. Retire completed plans by deleting them; git history keeps the record.
- **Guides describe current behaviour only.** No "historical" banners and no superseded sections.
