# Telemetry Taxonomy – External OIDC & Admin Surfaces

Updated: 2025-09-29

This note captures the canonical event and metric taxonomy for the IdP chaining workstream. It closes the outstanding tracking items around admin CRUD instrumentation and external cancel/error categorisation. Use this as the single source of truth when wiring new telemetry or building dashboards.

## Metered telemetry (System.Diagnostics.Metrics)

### External OIDC start

| Metric | Instrument | Tags | Outcome values | Notes |
|--------|------------|------|----------------|-------|
| `oidc.external.start.requests` | Counter | `provider`, `clientId`, `outcome` | `missing_params`, `unknown_provider`, `invalid_provider_config`, `discovery_failed`, `discovery_timeout`, `discovery_exception`, `par`, `query`, `jar` | Increments for every `/Auth/External/Start` invocation. Successful starts also increment `oidc.external.start.success` (same tags). Failures increment `oidc.external.start.failures`. |
| `oidc.external.start.duration.ms` | Histogram (double) | `provider`, `clientId`, `outcome` | same as above | Captures elapsed milliseconds from request handling until redirect/ProblemDetails generation. |

### External OIDC callback

All callback metrics share the tag set `{ provider, clientId, outcome, correlation, handle }` where `correlation` ∈ {`present`,`missing`,`unknown`}, and `handle` ∈ {`fresh`,`stale`,`unused`}.

| Metric | Instrument | Notes |
|--------|------------|-------|
| `oidc.external.callback.requests` | Counter | Every callback hit (success + failure). |
| `oidc.external.callback.success` / `failures` | Counter | Split by `outcome`. |
| `oidc.external.callback.duration.ms` | Histogram (double) | Duration from callback entry until final `IResult`. |
| `oidc.external.callback.outcomes` | Counter | Canonical series for alerting and dashboards. Every callback increments exactly once with the outcome taxonomy below. |

#### Outcome taxonomy (callback)

| Outcome value | Classification | Cancellation bucket | Description / Trigger |
|---------------|----------------|----------------------|-----------------------|
| `signed_in` | Success | N/A | Final sign-in completed (default path). |
| `linked` | Success | N/A | External identity already linked; reused linkage. |
| `linked_immediate` | Success | N/A | Email match allowed immediate linking (no confirmation). |
| `auto_provisioned` | Success | N/A | New local account created during callback. |
| `confirm_link_success` | Success | N/A | User confirmed linking via confirmation screen. |
| `cid_ref_stale` | Failure | Session expiry | Cache entry expired or missing; new CID minted and user prompted to restart. |
| `upstream_error` | Failure | **UserCancel** when `error` ∈ {`access_denied`,`login_required`,`interaction_required`,`consent_required`}; otherwise `ExternalProviderFailure`. | Upstream returned `error` parameter; value and description logged separately. |
| `missing_code` | Failure | ProtocolError | Callback missing `code`. |
| `unknown_provider` | Failure | Configuration | Provider disabled or not recognised. |
| `invalid_config` | Failure | Configuration | Provider registration failed validation mid-flow. |
| `discovery_failed` | Failure | UpstreamTransport | HTTP error status from discovery fetch. |
| `discovery_timeout` | Failure | UpstreamTransport | Discovery timed out (10 s budget). |
| `discovery_exception` | Failure | UpstreamTransport | Non-timeout exception during discovery. |
| `token_timeout` | Failure | UpstreamTransport | Token endpoint timed out (15 s budget). |
| `token_exception` | Failure | UpstreamTransport | Non-timeout exception during token call. |
| `token_exchange_failed` | Failure | UpstreamProvider | Token endpoint returned non-success. |
| `jwks_failed` | Failure | UpstreamProvider | Provider JWKS fetch failed. |
| `nonce_mismatch` | Failure | Validation | Nonce in ID token did not match stored value. |
| `id_token_validation_failed` | Failure | Validation | General ID token validation error. |
| `missing_sub_or_issuer` | Failure | Validation | Upstream token missing subject/issuer. |
| `policy_denied` | Failure | Policy | Local policy forbids linking or auto-provision. |

> **Dashboard note:** treat `upstream_error` rows with `error=access_denied` (see logs) as user-driven cancels. Create a derived dimension `cancel_category` with values `user_cancel`, `upstream_failure`, `session_expired`, `policy`, `validation`, `transport` for alerting.

#### Friendly error codes

The Razor error page receives a `code` query parameter aligned to the table above (same strings). Use that value in UI log correlation or support documentation.

## Admin audit telemetry

All admin operations emit structured audit events via `IAuditSink`. Event names follow `admin.<resource>.<action>` with dotted sub-resources on demand. Payloads must avoid raw secrets; use hashed or bucketised values.

### Canonical events (current + planned)

| Resource | Action | Event name | Payload fields | PII stance |
|----------|--------|------------|----------------|------------|
| Client | Back-channel settings updated | `admin.client.backchannel.update` | `client_id`, `backchannel_logout_uri_old/new`, `backchannel_logout_session_required_*`, `user`, `ip`, `when` | Values already hashed/sanitised where appropriate. |
| Providers | Create | `admin.provider.create` | `provider_id`, `provider_name`, `type`, `enabled`, `user`, `ip`, `when` | Provider IDs/names are non-PII. |
| Providers | Update | `admin.provider.update` | `provider_id`, changed field summary (boolean flags for config sections), `user`, `ip`, `when` | Do not log secrets or full config JSON—log boolean flags (`config_changed`, `discovery_url_changed`). |
| Providers | Delete | `admin.provider.delete` | `provider_id`, `user`, `ip`, `when` | |
| Client ↔ Provider mapping | Upsert | `admin.client.provider_map.upsert` | `client_id`, `provider_id`, `enabled`, `is_default`, `order`, `user`, `ip`, `when` | |
| Client ↔ Provider mapping | Delete | `admin.client.provider_map.delete` | same as above | |
| Claim mappings | Upsert | `admin.provider.claim_map.upsert` | `provider_id`, `claim_external_hash`, `claim_local`, `transform`, `user`, `ip`, `when` | Hash external claim name with SHA-256 short hash (`claim_external_hash`) to avoid leaking raw names if needed. |
| Claim mappings | Delete | `admin.provider.claim_map.delete` | `provider_id`, `claim_id`, `user`, `ip`, `when` | |
| Provider keys | Import | `admin.provider.key.import` | `provider_id`, `purpose`, `alg`, `kid`, `active`, `publishable`, `user`, `ip`, `when` | `kid` safe; never log JWK body. |
| Provider keys | Update | `admin.provider.key.update` | same fields + before/after booleans for `active`, `publishable`. |
| Provider keys | Delete | `admin.provider.key.delete` | `provider_id`, `kid`, `user`, `ip`, `when` | |
| Client keys | Update | `admin.client.keys.update` | `client_id`, `jwks_source` (`manual`,`uri`), `key_count`, `duplicate_kid_count`, `user`, `ip`, `when` | Include SHA-256 short hash of JWKS payload if manual input. |
| BCL outbox | List snapshot | `bcl.admin.outbox.list` | `count`, `backlog`, `ip` | Already implemented. |
| BCL outbox | Retry | `bcl.admin.outbox.retry` | `id`, `client_id`, `target`, `ip` | Already implemented. |

Implementation guidance:

- Use `_audit.HashValue(value)` (or equivalent) when referencing URIs or sensitive text.
- Always include `user`, `ip`, and `when` on mutating operations.
- Emit a paired `admin.*.error` event with `reason` when validation fails after user submission but before save (optional enhancement).

## Cancel taxonomy quick reference

| Cancel bucket | Included outcomes | Primary dimensions |
|---------------|------------------|--------------------|
| `user_cancel` | `upstream_error` with provider `error` ∈ {`access_denied`,`interaction_required`,`login_required`,`consent_required`} | `provider`, `clientId`, upstream `error`, `correlation` |
| `session_expired` | `cid_ref_stale` | `handle=stale` indicates cache miss. |
| `policy` | `policy_denied` | `clientId`, `provider`. |
| `upstream_failure` | `upstream_error` (other `error` values), `discovery_failed`, `discovery_timeout`, `discovery_exception`, `token_exchange_failed`, `jwks_failed`, `token_timeout`, `token_exception` | Correlate with upstream status codes where logged. |
| `validation` | `missing_code`, `nonce_mismatch`, `id_token_validation_failed`, `missing_sub_or_issuer`, `invalid_config` | Use to flag integration drift. |
| `configuration` | `unknown_provider` | Investigate admin toggles or stale cached state. |

Dashboards should map `outcome` → `cancel bucket` using the table above. Alert when `user_cancel` ratio spikes (>30 % over 5 min) or `session_expired` exceeds 1 % baseline.

## Client Secret Metrics

> **Added:** October 17, 2025 (Client Secret Rotation feature)

Client secret rotation and expiry tracking uses dedicated metrics via `IClientSecretMetrics`. These are critical for preventing authentication outages due to expired secrets.

### Client Secret Counters and Gauges

| Metric | Instrument | Tags | Description |
|--------|------------|------|-------------|
| `oidc.client_secrets.authentication_success` | Counter | `client_id`, `secret_id`, `is_primary` | Incremented when a client successfully authenticates with a secret. Use `is_primary` tag to track usage of primary vs. secondary secrets. |
| `oidc.client_secrets.authentication_failure` | Counter | `client_id`, `reason` | Incremented when client authentication fails. Reason values: `expired`, `revoked`, `invalid`, `missing`. |
| `oidc.client_secrets.active_count` | Gauge | `client_id` | Current number of active (non-expired, non-revoked) secrets per client. Monitor for values >3 (indicates misconfiguration). |
| `oidc.client_secrets.days_until_expiry` | Gauge | (global minimum) | Minimum days until any secret expires across all clients. Alert when <7 days. Updated by `ClientSecretExpiryMonitor` background service. |
| `oidc.client_secrets.rotation_events` | Counter | `action` | Lifecycle event counter. Action values: `created`, `activated`, `revoked`, `set_primary`. |
| `oidc.client_secrets.total_active` | Gauge | (none) | Total count of active secrets across all clients (snapshot). Updated by expiry monitor. |

### Alert Recommendations

| Alert | Condition | Severity | Action |
|-------|-----------|----------|--------|
| **Secret Expiring Soon** | `oidc.client_secrets.days_until_expiry < 7` | Warning | Notify admins to rotate affected client secrets. |
| **Secret Expired** | `oidc.client_secrets.authentication_failure{reason="expired"} > 0` | Critical | Client authentication blocked; immediate rotation required. |
| **Too Many Active Secrets** | `oidc.client_secrets.active_count > 3` for any client | Info | Review client secret management; may indicate incomplete rotation. |
| **Secondary Secret High Usage** | `oidc.client_secrets.authentication_success{is_primary="false"} / total_auth > 0.5` | Info | Clients may not have switched to new primary secret after rotation. |

### Structured Logging

Client secret authentication events are logged with correlation to secret lifecycle:

**Success:**

```log
Client secret authenticated: ClientId={ClientIdHash}, SecretId={SecretId}, IsPrimary={IsPrimary}
```

**Expiry:**

```log
Client secret expired: ClientId={ClientId}, SecretId={SecretId}, ExpiredAt={ExpiredAt}, Description={Description}
```

**Rotation Events (via Admin API):**

```log
Client secret created: ClientId={ClientId}, SecretId={SecretId}, Description={Description}, ExpiresAt={ExpiresAt}, CreatedBy={User}
Client secret activated: ClientId={ClientId}, SecretId={SecretId}, ActivatedBy={User}
Client secret revoked: ClientId={ClientId}, SecretId={SecretId}, RevokedBy={User}
Client secret set as primary: ClientId={ClientId}, SecretId={SecretId}, UpdatedBy={User}
```

> **PII Handling:** `ClientId` values are bucketed/hashed in logs. `SecretId` is a GUID (non-PII). Secret values/hashes are NEVER logged.

### Dashboard Queries

**Prometheus/Grafana examples:**

```promql
# Secrets expiring within 7 days
oidc_client_secrets_days_until_expiry < 7

# Authentication failure rate by reason
rate(oidc_client_secrets_authentication_failure[5m])

# Primary vs secondary secret usage ratio
sum(rate(oidc_client_secrets_authentication_success{is_primary="true"}[5m]))
/ sum(rate(oidc_client_secrets_authentication_success[5m]))

# Clients with >2 active secrets (potential rotation issues)
oidc_client_secrets_active_count > 2
```

---

Maintainers: update this document whenever new events or outcomes are introduced. Keep the taxonomy stable—renaming an outcome/event without updating collectors is a breaking change for dashboards.
