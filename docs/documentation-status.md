# Documentation Status and Follow-up

Reconciled 2026-09-05. This page routes readers between current operating guidance, active designs, and historical evidence. It is not a security certification or a claim that every old finding has been fixed. Use the [documentation index](index.md) for task-oriented navigation.

## Current Guidance

- Installation: [deployment](deployment-guide.md), [production setup](production-setup-guide.md), [Compose variants](docker-compose-examples.md), [local troubleshooting](troubleshooting/local-development.md), and [MailHog](mailhog-local-dev.md).
- Operations: [upgrade](upgrade-guide.md), [restore verification](for-operators/backup-restore/verification-testing.md), [monitoring](for-operators/monitoring/alerting-rules.md), and [incident response](for-security-teams/incident-response.md).
- Credential lifecycle: [client secrets](for-operators/client-secret-rotation.md), [keys and certificates](for-operators/key-rotation.md), and [WebAuthn](for-administrators/webauthn.md). These replace the operational role of the archived playbooks, which remain as historical records.
- Protocol references: [OBO client policy](reference/obo-client-policy.md), [DPoP same-key tests](reference/obo-dpop-requiresamejkt-e2e.md), [IdP chaining](reference/idp-chaining-client-configuration.md), [JAR replay cache](reference/jar-replay-cache.md), and [pairwise subjects](reference/pairwise-subject-identifiers.md).
- Specialized guides retained: [JAR/JARM](jar-jarm-guide.md), [hybrid caching](hybrid-cache-guide.md), [pgAdmin](pgadmin-guide.md), [rate-limiting dashboard](rate-limiting-dashboard.md), and [registration/enrollment](user-registration-and-enrollment.md). Retention means they have a distinct use, not that all examples were retested in this review.

## Design and Review Status

| Material | Classification | How to use it |
| --- | --- | --- |
| [Delegated-access review](impersonation-and-delegated-access-review-2026-08-01.md) and [implementation plan](tenant-support-and-delegated-access-implementation-plan.md) | Active, partially implemented | The demo profile path and support revocation UI exist; production authorization adoption and remaining acceptance criteria still need evidence. |
| [Provider-template plan](well-known-idp-providers-plan.md) | Partially implemented design | Catalog/template code exists. Verify each remaining integration/UI requirement rather than treating the whole proposal as either missing or complete. |
| [Dynamic registration draft](dynamic-client-autoregistration-spec.md) | Historical design with unresolved policy reconciliation | Current registration behavior is controlled by code/configuration, not the draft's unconditional IAT and expiry statements. |
| [Pairwise proposal](future-plans/pairwise-subject-identifiers.md) | Implemented foundation; historical proposal retained | Use the active reference for current mapping and sector behavior; separately verify delegated-token semantics. |
| [General review](code-review.md), [comprehensive review](comprehensive-code-review.md), [implementation assessment](oidc-implementation-assessment.md) | Historical assessments | Retain findings and provenance. Read their new scope warnings before reusing readiness, architecture, or framework claims. |
| [Multi-tenancy assessment](multi-tenancy-assessment.md), [fix plan](multi-tenancy-fix-plan.md), [agent instructions](multi-tenancy-fix-instructions.md) | Historical security/remediation evidence | Reproduce findings on the current revision. Do not apply OLD/NEW patches blindly. |
| [E2E proposal](E2E_TEST_COVERAGE_PROPOSAL.md), [July failure triage](test-failure-fix-plan-2026-07-23.md), [performance plan](test-performance-improvement-plan.md) | Historical test snapshots | Current status comes from fresh scoped runs, not old counts, elapsed times, or unchecked boxes. |

## Open Verification Queue

Owners below are responsible roles to assign, not named commitments. No product defects were closed by this documentation-only update. A closure needs a revision/configuration, a focused test or drill, its result, and a link to retained evidence.

| Follow-up | Status | Owner to assign | Closure evidence |
| --- | --- | --- | --- |
| Production delegated-resource adoption, pairwise actor/subject policy, authorization inventory, and audit correlation | Open release verification | Security and API maintainers | Negative resource/client/tenant/revocation tests and review of the remaining findings in the delegated-access review. |
| Tenant isolation findings in older assessments | Needs reproduction | Auth and persistence maintainers | Finding-by-finding mapping to current handlers, filters, queries, and cross-tenant negative tests. |
| Dynamic registration policy | Needs reconciliation | Protocol maintainers | Tests for `AuthOptions.RequireInitialAccessToken`, production `Dcr:AllowAnonymousInProduction`, registration access tokens, and actual secret expiry. Production anonymous registration is guarded; do not enable it merely to match an old draft. |
| Provider-template acceptance criteria | Partial implementation | Federation/UI maintainers | Map unchecked requirements to catalog, migrations, admin workflows, and real-provider integration tests. |
| Secret rotation and upstream JAR rotation | Needs operational drill | Client owners and IdP operators | Replacement authentication succeeds, old credentials fail after revocation, upstream verification survives the planned public-key overlap, and no private material is disclosed. |
| Restore, upgrade, and compromised-key response | Needs operational drill | Platform operations and security | Isolated restore and migration results, measured recovery time/data loss, downstream token acceptance, and approved rollback/containment decisions. |
| Monitoring and alert delivery | Deployment-specific | Observability/on-call owners | Actual exporter names/units, baseline thresholds, routed alert and recovery tests, and documented response ownership. |
| Test coverage/performance and WebAuthn compatibility | Needs fresh runs | Test and account-security maintainers | Current test selection/results plus supported authenticator/browser and recovery exercises. Do not infer hardware support from page-load tests. |

The registration guard is implemented in [RegistrationHandler](../MrWhoOidc.WebAuth/Handlers/RegistrationHandler.cs); the current auth options are in [AuthOptions](../MrWhoOidc.Auth/Services/AuthOptions.cs).

## Historical Evidence and Retention

Keep the [completed-work archive](done/README.md), [older archive](_archive/), and [ADRs](adr/) for traceability. Older plans and progress summaries are not active runbooks; their commands need revalidation. Do not delete security findings merely because their date is old or the implementation has changed.

Retain dated security, SOLID, compliance, and conformance submission records. For example, [June security review](security-review-2026-06.md), [May SOLID review](solid-review-2026-05-28.md), and [June Basic OP submission notes](oidc-basic-op-dynamic-client-submission-notes-2026-06-20.md) provide evidence about their reviewed snapshots, not a blanket verdict for a later release. See [certification readiness](oidc-openid-certification-readiness.md) for the distinction between test evidence and official certification.

The root documentation file named `CODE_REVIEW.md` was removed because it contained only a serialized tool request, not a review. The distinct [archived code-review guidelines](_archive/CODE_REVIEW.md) remain. Other historical files were retained in place so existing references remain useful; selected files now link to their current replacements.

The former duplicate assistant guidance now [points to the canonical instructions](copilot-instructions.md). Historical copies under the archive are not active customization files.

## Maintenance Rules

When behavior changes, update the active guide and mark an older plan as superseded or partially implemented with a link. Preserve original evidence, but correct clearly identified contradictions in maintained reviews. Do not silently turn a proposed design or unchecked requirement into an implementation claim.

For a new release, revalidate operational examples against that release's Compose/configuration and run the relevant acceptance checks. This review performed source and static documentation checks, not deployment, recovery, credential rotation, or conformance runs.
