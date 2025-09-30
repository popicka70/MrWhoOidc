# MrWhoOidc.Client NuGet Backlog

Status legend
- [ ] Not started
- [~] In progress
- [x] Done

## Vision & Scope
- Ship a reusable NuGet package (`MrWhoOidc.Client`) that gives first-party and partner apps a batteries-included way to configure and talk to the MrWhoOidc authorization server (`MrWhoOidc.WebAuth`).
- Focus areas: configuration primitives, resilient discovery/bootstrap, typed client registration helpers, token acquisition convenience, and guidance for secure defaults.
- Target runtime: .NET 9. Package should multi-target `net9.0` (initial) with room to add `net8.0` if demanded.

## Non-goals (for this backlog)
- Federated sign-in UI or higher-level UX components (leave to app teams).
- Generic OIDC middleware replacement; we build on ASP.NET Core primitives rather than re-implementing OpenIdConnect middleware.
- Support for legacy .NET Framework or Xamarin.

## Guardrails & Constraints
- No dependency on OpenIddict or Microsoft Identity Platform packages (aligns with server constraints).
- Consumers configure endpoints via discovery or explicit override; never hardcode environment secrets.
- Configuration must pass security review (no storing client secrets unencrypted, enforce TLS endpoints, validate audiences/scopes).
- Package must be source-link enabled and pass NuGet package validation (symbol package optional but preferred).

## Cross-cutting Requirements
- Options validation errors should be actionable (include config path, hint at environment variable overrides).
- All HTTP flows instrumented with meter/activities using `MrWhoOidc.ServiceDefaults` conventions.
- Provide automatic correlation ID propagation using existing `CorrelationPipeline`. If pipeline can't be reused directly, add opt-in integration.
- Document Dev vs Prod configuration matrix (e.g., HTTP vs HTTPS, PKCE enforcement, DPoP availability).

---

## Epic 0 – Foundations & Packaging
- Story 0.1: Finalize package naming, icons, tags, README template
  - AC: [ ] Align with corporate NuGet gallery guidelines; [ ] Reserve namespace on internal feed
- Story 0.2: Project audit & clean-up
  - AC: [~] Remove placeholder `Class1.cs`; [x] Adjust `MrWhoOidc.Client.csproj` with PackageId, metadata, SourceLink, nullable settings, analyzers
- Story 0.3: Build scripts & CI
  - AC: [ ] Add `dotnet pack` task; [ ] Integrate versioning (GitHeight/MinVer); [ ] Validate pack succeeds in CI

## Epic 1 – Configuration Model
- Story 1.1: Define `MrWhoOidcClientOptions`
  - AC: [x] Issuer, DiscoveryUri, ClientId, Secret/Assertion, Scopes, Resource/Audience, PKCE, DPoP toggles; [x] Data annotations/Fluent validation
- Story 1.2: Configuration binding helpers
  - AC: [x] `services.ConfigureMrWhoOidcClient(Configuration, sectionName)` extension; [ ] Support for multiple named clients; [x] Configuration reload support
- Story 1.3: Options validation & diagnostics
  - AC: [x] `IValidateOptions` implementation; [x] Log-friendly validation failures; [x] Unit tests covering missing issuer/redirect URIs/secret combos

## Epic 2 – Discovery & Metadata Bootstrap
- Story 2.1: Discovery client helper
  - AC: [x] Typed service that downloads and caches `.well-known/openid-configuration`; [x] Respects ETag/Cache-Control; [~] Handles transient failures with retry/backoff
- Story 2.2: JWKS cache integration
  - AC: [x] Provide `IJwksCache` abstraction; [~] Cache invalidation per `kid`; [~] Hook into consumer token validation APIs
- Story 2.3: Developer ergonomics
  - AC: [x] `services.AddMrWhoOidcDiscovery()` extension wiring HttpClientFactory; [~] Telemetry (meter + activity) for discovery fetches

## Epic 3 – Authentication Flow Helpers
- Story 3.1: Authorization code flow scaffolding
  - AC: [x] Helper to build authorize URL with PKCE & correlation state; [x] Round-trip validator to parse callback and surface tokens/errors; [x] Tests covering nonce/state mismatches
- Story 3.2: Token endpoint client
  - AC: [x] `IMrWhoOidcTokenClient` with methods for code exchange, refresh token, client credentials, token exchange (OBO); [x] Support DPoP proof attachment
- Story 3.3: Token storage abstraction
  - AC: [ ] Optional interface for persisting refresh tokens securely (consumer-provided implementation); [ ] Provide in-memory sample implementation for quick starts
- Story 3.4: On-behalf-of helpers
  - AC: [x] Extend options with downstream API registrations (audience/resource → scope mapping, cache hints); [x] Provide `IMrWhoOnBehalfOfManager` that wraps token exchange using configured mappings; [x] Supply delegating handler/extension to attach OBO access tokens to `HttpClient`
- Story 3.5: Machine-to-machine token helpers
  - AC: [x] Add configuration model for service principals (client credentials) with default scopes/audiences; [x] Provide `IMrWhoClientCredentialsManager` that caches M2M access tokens per registration; [x] Offer HttpClient handler/extension that injects the cached M2M token for background services

## Epic 4 – Security & Hardening
- Story 4.1: PKCE + DPoP enforcement defaults
  - AC: [x] Public clients auto-enforce PKCE; [x] When DPoP configured, ensure proof builder validates method/uri/nonce; [~] Include replay protection guidance
- Story 4.2: Secret management guidance
  - AC: [ ] Document best practices (Azure Key Vault, AWS Secrets Manager); [ ] Provide interface hooks for late-binding client secrets/assertions
- Story 4.3: Threat modeling checklist
  - AC: [ ] Capture MITM, token leakage, refresh token rotation; [ ] Align with internal security review template

## Epic 5 – Observability & Diagnostics
- Story 5.1: Logging primitives
  - AC: [~] Structured logging for request/response (redacting secrets); [~] Correlation ID propagation; [x] Integration with `ILogger<M>`
- Story 5.2: Metrics
  - AC: [x] Counters for token requests (success/failure) and latency histogram; [x] Expose meter name `MrWhoOidc.Client`
- Story 5.3: Health checks
  - AC: [ ] Optional `IHealthCheck` verifying discovery + token endpoint reachability; [ ] Document wiring

## Epic 6 – Samples & Tests
- Story 6.1: Unit tests
  - AC: [x] Coverage for options validation, discovery caching, token client error handling; [x] Use MSTest to match repo
- Story 6.2: Integration samples
  - AC: [ ] Minimal console app using client credentials; [x] ASP.NET Core web app sample performing auth code flow; [x] Web API sample validating OBO tokens with `MrWhoOidc.Client`
- Story 6.3: Automated smoke tests
  - AC: [ ] Pipeline job running samples against local AppHost (Aspire); [ ] Validate token acquisition & userinfo call succeeds

## Epic 7 – Documentation & Adoption
- Story 7.1: Package README + docs
  - AC: [x] Usage overview, configuration matrix, troubleshooting; [~] Link back to `docs/developer-guide.md`
- Story 7.2: Migration guide
  - AC: [ ] Steps to move from manual configuration to package; [ ] Highlight breaking changes (if any)
- Story 7.3: Adoption checklist
  - AC: [ ] Partner teams sign-off; [ ] Track first production consumer; [ ] Feedback loop for backlog updates

## Epic 8 – Release & Support
- Story 8.1: Internal preview
  - AC: [ ] Publish to internal feed; [ ] Collect feedback via issue template; [ ] Triage week-one bugs
- Story 8.2: General availability
  - AC: [ ] Tag release in repo; [ ] Publish signed package to public NuGet (if approved); [ ] Announce in dev channel
- Story 8.3: Support plan
  - AC: [ ] Define SLA owners; [ ] Set up monitoring for download stats; [ ] Schedule quarterly review of backlog

## Epic 9 – JAR & JARM Enhancements
- Story 9.1: Signed authorization requests (JAR)
  - AC: [x] Introduce request-object builder that creates and signs JWT payloads using configured client keys; [x] Support both symmetric (client secret) and asymmetric signing with key rotation hooks; [x] Add validator tests covering required claims and audience/nonce handling
- Story 9.2: JWT-secured authorization responses (JARM)
  - AC: [x] Extend callback processing to detect and validate JARM responses; [x] Leverage JWKS cache for issuer response signing keys; [x] Surface detailed error states when validation fails
- Story 9.3: Documentation & samples for JAR/JARM
  - AC: [x] Update README/backlog docs with configuration walkthrough; [x] Add Razor sample toggle showing JAR/JARM usage; [x] Provide troubleshooting section for common validation errors

---

## Open Questions
- Should package expose typed HTTP handlers for backchannel logout notifications or keep those server-side only?
- Do we require multi-tenant awareness (issuer per tenant) in v1, or can we assume single issuer with environment overrides?
- How should we distribute and rotate client signing keys for outbound JAR (config-based vs managed store), and what defaults make sense for partner apps?

## Dependencies & Risks
- Requires `MrWhoOidc.ServiceDefaults` stable interfaces for telemetry; coordinate with service defaults team if breaking changes planned.
- Token exchange flows depend on server-side policy endpoints; ensure docs/tests updated if APIs change.
- Risk of duplicating logic already in `MrWhoOidc.Security` (DPoP). Mitigation: share common components or move to shared project.
- JAR/JARM support depends on availability and lifecycle of client signing keys; coordinate with security team on recommended storage and rotation approach.

## Tracking & Reporting
- Establish GitHub milestone `mrwhooidc-client-1.0` with issues per story.
- Create dashboard (Azure DevOps or GitHub project board) to visualize epic/story progress.
- Review progress bi-weekly with platform guild.
