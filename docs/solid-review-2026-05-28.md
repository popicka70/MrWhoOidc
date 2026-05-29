# MrWhoOidc — SOLID Principles Code Review

**Date:** 2026-05-28
**Scope:** `MrWhoOidc` solution (`MrWhoOidc.slnx`), with emphasis on `MrWhoOidc.Auth`, `MrWhoOidc.WebAuth`, `MrWhoOidc.ApiService`, `MrWhoOidc.Security`.
**Focus:** SOLID principles (SRP, OCP, LSP, ISP, DIP).
**Method:** Static inspection of source. File/line references were verified against the working tree at review time; line numbers are approximate and may drift with edits.

---

## Summary

The codebase shows a generally mature design: constructor injection is used throughout, most implementations are `sealed`, and several extensibility points (notably the token-grant pipeline) are textbook OCP/DIP. The main SOLID debt is concentrated in a handful of oversized "handler" and "service" classes that have accumulated many responsibilities, plus a few static utility classes and a wide store interface that hurt testability and segregation.

| Severity | Count | IDs |
|----------|-------|-----|
| High     | 3     | SLD-1, SLD-2, SLD-3 |
| Medium   | 4     | SLD-4, SLD-5, SLD-6, SLD-7 |
| Low      | 3     | SLD-8, SLD-9, SLD-10 |

Nothing here is a correctness or security defect; these are maintainability/design findings. Prioritize SLD-1 and SLD-2 (they are the largest, highest-churn files) and SLD-3 (it blocks unit testing of a security-sensitive path).

---

## High

### SLD-1 (High) — `ExportImportHandler` is a 1,500-line static God class (SRP, DIP)

- **File:** [MrWhoOidc.WebAuth/Handlers/ExportImportHandler.cs](../MrWhoOidc.WebAuth/Handlers/ExportImportHandler.cs) (1,514 lines)
- **Evidence:** A single `static` class exposes ~20 endpoint methods spanning four entity scopes plus auditing plus route registration:
  - Tenant export/preview/import: `ExportTenant` (L31), `GetExportPreview` (L92), `PreviewImport` (L426), `ImportTenant` (L479)
  - Realm: `ExportRealm` (L145), `GetRealmExportPreview` (L196), `PreviewRealmImport` (L557), `ImportRealm` (L627)
  - Client: `ExportClient` (L242), `GetClientExportPreview` (L291), `PreviewClientImport` (L700), `ImportClient` (L784)
  - Provider: `ExportProvider` (L333), `GetProviderExportPreview` (L381), `PreviewProviderImport` (L879), `ImportProvider` (L947)
  - Cross-cutting: `GetAuditLogs` (L1239), `GetAuditLogDetail` (L1320), `MapExportImportEndpoints` (L1019), `CreateExportOptions` (L1367)
- **Why it matters (SRP):** Export, import, preview, audit-log querying, and endpoint wiring are five distinct reasons to change living in one file. Any new export scope edits this file; the four scopes duplicate near-identical export/preview/import shapes.
- **Secondary (DIP):** Methods are `static`, so collaborators are reached via parameters/`HttpContext` service resolution rather than injected abstractions, making isolated unit testing awkward.
- **Note:** `CreateExportOptions` (the `mode => "export_*"` switch) is duplicated at [L51](../MrWhoOidc.WebAuth/Handlers/ExportImportHandler.cs#L51) and [L1369](../MrWhoOidc.WebAuth/Handlers/ExportImportHandler.cs#L1369) — minor DRY violation that rides along with the SRP problem.
- **Recommendation:** Split per scope (`TenantExportImportService`, `RealmExportImportService`, `ClientExportImportService`, `ProviderExportImportService`) behind a small `IExportImportService<TScope>` or a generic exporter/importer, move audit-log reads to their own handler, and keep the static class only as a thin endpoint-mapping shim. Introduce instance services so collaborators are injected.

### SLD-2 (High) — `UserInfoHandler` has too many responsibilities (SRP)

- **File:** [MrWhoOidc.WebAuth/Handlers/UserInfoHandler.cs](../MrWhoOidc.WebAuth/Handlers/UserInfoHandler.cs) (798 lines)
- **Evidence:** One class with 11 constructor dependencies ([L27-L38](../MrWhoOidc.WebAuth/Handlers/UserInfoHandler.cs#L27)) drives the full UserInfo flow: bearer/DPoP token validation, DPoP replay/nonce handling, OIDC `claims`-parameter constraint evaluation (`ClaimConstraint` record at L41), database profile/email lookup (`UserInfoDbData` at L42), scope→claim filtering, and the signed/encrypted JWT response decision tree.
- **Why it matters (SRP):** The claim-filtering rules, the DPoP rules, and the JWT-encoding rules each change for independent reasons; today they all change this file. The 11-dependency constructor is a smell that several collaborators belong to extracted units.
- **Recommendation:** Extract `IClaimFilteringEngine` (scope + `claims` parameter → claim set), reuse the existing DPoP validation services behind a thin `IUserInfoDPoPGuard`, and extract `IUserInfoResponseWriter` for the plain/JWT/encrypted-JWT decision. The handler then orchestrates 3–4 collaborators.

### SLD-3 (High) — `ClientJwksResolver` is a static class on a security-sensitive path (DIP, testability)

- **File:** [MrWhoOidc.Auth/Services/ClientJwksResolver.cs](../MrWhoOidc.Auth/Services/ClientJwksResolver.cs) (122 lines)
- **Evidence:** `public static class ClientJwksResolver` with `GetSigningKeysAsync` (L13), `GetEncryptionKeyAsync` (L23), `ParseSecurityKeys` (L38). Dependencies (`IHttpClientFactory`, `IJwksCache`) are passed as **optional** method parameters rather than injected, and it performs outbound HTTP to client JWKS endpoints.
- **Why it matters (DIP):** Consumers (e.g. `UserInfoHandler`) bind to a concrete static type and cannot substitute a fake. Testing UserInfo encryption/signing requires live HTTP or elaborate `HttpClientFactory` plumbing.
- **Recommendation:** Introduce `IClientJwksProvider` with instance methods, register it in DI, and inject it. The static methods can remain as a private implementation detail behind the interface during migration.

---

## Medium

### SLD-4 (Medium) — `IClientStore` is a wide interface mixing read, validation, and full secret lifecycle (ISP)

- **File:** [MrWhoOidc.Auth/Services/ClientStore.cs](../MrWhoOidc.Auth/Services/ClientStore.cs#L14) (interface L14-L64)
- **Evidence:** 14+ members across three unrelated concerns:
  - Lookup/cache: `FindByClientIdAsync` (L22), `QueryClients` (L39), `InvalidateClientCacheAsync` (L47)
  - Secret validation: `ValidateClientSecretAsync` (L33)
  - Secret lifecycle CRUD: `GetPrimarySecretAsync`, `GetActiveSecretsAsync`, `CreateSecretAsync`, `ActivateSecretAsync`, `SetPrimarySecretAsync`, `RevokeSecretAsync`, `RecordSecretUsageAsync` (L56-L63)
- **Why it matters (ISP):** A consumer that only needs to look up a client (the hot auth path) takes a dependency that also exposes secret-rotation mutations. Consumers are forced to know about members they never call, and test doubles must stub the whole surface.
- **Recommendation:** Segregate into `IClientLookup` (find/query/cache), `IClientSecretValidator` (validate), and `IClientSecretManager` (the CRUD lifecycle). One class may still implement all three; consumers depend only on what they use.

### SLD-5 (Medium) — `WebAuthnService` bundles challenge, credential management, and options resolution (SRP)

- **File:** [MrWhoOidc.Auth/Services/WebAuthnService.cs](../MrWhoOidc.Auth/Services/WebAuthnService.cs) (644 lines)
- **Evidence:** Registration/authentication challenge+completion logic coexists with credential CRUD (`GetUserCredentialsAsync` L393, `RemoveCredentialAsync` L405, `UpdateCredentialNameAsync` L429, `HasWebAuthnCredentialsAsync` L452), per-tenant effective-options resolution (`GetEffectiveOptions` L487 → `EffectiveWebAuthnOptions` record L602), and AAGUID policy validation (`ValidateAaguidPolicy` L543).
- **Why it matters (SRP):** Ceremony orchestration, credential persistence, options cascading, and attestation policy are independent change axes.
- **Recommendation:** Extract `IWebAuthnCredentialManager` (CRUD), `IEffectiveWebAuthnOptionsResolver` (tenant override cascade), and an attestation/AAGUID `IWebAuthnPolicyEvaluator`. `ValidateAaguidPolicy` is already `internal static` and pure — a clean candidate to move first.

### SLD-6 (Medium) — Per-tenant options cascade is a recurring, hand-rolled pattern (SRP/DRY)

- **Files:** `GetEffectiveOptions` in [WebAuthnService.cs L487](../MrWhoOidc.Auth/Services/WebAuthnService.cs#L487); tenant settings resolution in [ITenantSettingsService.cs](../MrWhoOidc.Auth/Services/ITenantSettingsService.cs).
- **Evidence:** "platform default → tenant override → effective value" merging logic is implemented inline per feature area rather than via a shared mechanism.
- **Why it matters:** Each new tenant-overridable option family re-implements the same cascade, and the resolution source (default vs override) is implicit to callers.
- **Recommendation:** Provide a reusable `IEffectiveOptionsResolver<TOptions, TOverrides>` (or a small merge helper) and return a result type that records the source, so the cascade lives in one place.

### SLD-7 (Medium) — Orchestrators mix extraction, metrics, and flow control (SRP)

- **File:** [MrWhoOidc.WebAuth/Services/AuthorizeRequestOrchestrator.cs](../MrWhoOidc.WebAuth/Services/AuthorizeRequestOrchestrator.cs)
- **Evidence:** Parameter extraction/validation, JAR/PAR request-object resolution, metrics/client bucketing, and return-URL management are handled in the same unit.
- **Why it matters (SRP):** Metrics recording and parameter parsing are cross-cutting concerns that obscure the core orchestration.
- **Recommendation:** Extract a `RequestParameterExtractor` and move metric/bucket recording behind a small recorder abstraction; keep the orchestrator focused on sequencing already-abstracted steps (`IRequestObjectDecryptor`, `IPushedAuthorizationRequestStore`, which are already cleanly injected — good).

---

## Low

### SLD-8 (Low) — `mode` switch in export is closed for extension (OCP)

- **File:** [MrWhoOidc.WebAuth/Handlers/ExportImportHandler.cs#L51](../MrWhoOidc.WebAuth/Handlers/ExportImportHandler.cs#L51) and [#L1369](../MrWhoOidc.WebAuth/Handlers/ExportImportHandler.cs#L1369)
- **Evidence:** `mode?.ToLowerInvariant() switch { "clients" => ..., "idps" => ..., ... }` — adding an export mode means editing the switch (in two places).
- **Why it matters (OCP):** Compare with the token-grant pipeline (see Positives) which adds behavior without editing existing code. The export path has not adopted the same model.
- **Recommendation:** Low priority; if export scopes are split per SLD-1, fold the mode mapping into a registry/dictionary keyed by mode so new modes register rather than edit a switch.

### SLD-9 (Low) — `RequestObjectDecryptor` throws `NotSupportedException` for unsupported algorithms (LSP, minor)

- **File:** [MrWhoOidc.Auth/Services/RequestObjectDecryptor.cs#L93](../MrWhoOidc.Auth/Services/RequestObjectDecryptor.cs#L93)
- **Evidence:** `throw new NotSupportedException($"Unsupported request object encryption (alg={alg}, enc={enc})");`
- **Assessment:** Acceptable. The supported algorithm set is platform-constrained and the message is descriptive; this is a runtime guard, not a broken Liskov substitution. Optionally surface unsupported-alg as a typed validation result instead of an exception so callers can branch without catching.

### SLD-10 (Low) — Two near-identical validation interfaces (ISP, borderline)

- **Files:** [AuthorizeService.cs](../MrWhoOidc.Auth/Services/AuthorizeService.cs) and [Authorization/AuthorizeRequestValidator.cs](../MrWhoOidc.Auth/Services/Authorization/AuthorizeRequestValidator.cs)
- **Evidence:** Both expose a single `ValidateAsync`-style method returning different result shapes.
- **Assessment:** Acceptable as-is — the differing result types justify separate roles, and each interface is already minimal (good ISP). Flagged only so a future reader does not collapse them by mistake; document the distinction at each interface.

---

## Positive observations (SOLID done well)

- **OCP/DIP — Token grant pipeline (exemplary).** `ITokenGrantHandler` ([ITokenGrantHandler.cs#L14](../MrWhoOidc.WebAuth/TokenEndpoint/Grants/ITokenGrantHandler.cs#L14)) with a `GrantType` property and per-grant implementations (`AuthorizationCodeGrantHandler`, `ClientCredentialsGrantHandler`, `RefreshTokenGrantHandler`, `DeviceCodeGrantHandler`, `CibaGrantHandler`, `TokenExchangeGrantHandler`) injected as `IEnumerable<ITokenGrantHandler>` into `TokenHandler` ([TokenHandler.cs](../MrWhoOidc.WebAuth/Handlers/TokenHandler.cs)). New grant types are added by adding a class — no existing code is modified.
- **DIP — Constructor injection throughout.** No service-locator anti-pattern in domain services; dependencies arrive via primary constructors (e.g. `ClientStore` [L65](../MrWhoOidc.Auth/Services/ClientStore.cs#L65), `UserInfoHandler` [L27](../MrWhoOidc.WebAuth/Handlers/UserInfoHandler.cs#L27)).
- **Encapsulation.** Implementations are predominantly `sealed` (and many `internal sealed`), preventing accidental inheritance and keeping public surface small (e.g. `WebAuthnService` [L19](../MrWhoOidc.Auth/Services/WebAuthnService.cs#L19)).
- **SRP — Small focused services exist.** Single-purpose abstractions such as `IClientIdGenerator`, `IClientSecretGenerator`, `IRequestObjectDecryptor`, and `IPushedAuthorizationRequestStore` are good SRP/ISP examples to model the larger refactors on.
- **DI organization.** Registration is centralized and explicit in [DependencyInjection.cs](../MrWhoOidc.Auth/DependencyInjection.cs) (~81 registrations in ~209 lines), keeping wiring out of business code.

---

## Recommended action order

1. **SLD-3** — Introduce `IClientJwksProvider` (small, unblocks testing of a security path). *(High, low effort)*
2. **SLD-4** — Segregate `IClientStore` into lookup / validation / secret-management interfaces. *(Medium, low effort, high readability gain)*
3. **SLD-2** — Extract `IClaimFilteringEngine` and `IUserInfoResponseWriter` from `UserInfoHandler`. *(High)*
4. **SLD-1** — Split `ExportImportHandler` per scope + extract audit-log reads. *(High, larger effort — do incrementally per scope)*
5. **SLD-5 / SLD-6** — Extract WebAuthn credential manager + a shared effective-options resolver (also serves the tenant-settings cascade). *(Medium)*
6. **SLD-7 / SLD-8** — Tidy orchestrator concerns and the export-mode switch opportunistically while touching those files. *(Medium/Low)*

> Suggested guardrail: apply the existing `ITokenGrantHandler` plugin pattern as the template for any new "many variants of the same operation" feature (export scopes, options families) to keep new code OCP-compliant by default.
