---
title: Backchannel Logout
type: concept
tags: [logout, oidc, background-jobs, reliability]
created: 2026-04-22
updated: 2026-04-22
related_files:
  - MrWhoOidc.WebAuth/Background/BackchannelLogoutDispatcher.cs
  - MrWhoOidc.WebAuth/Handlers/LogoutHandler.cs
  - MrWhoOidc.Auth/Persistence/AuthDbContext.cs
  - docs/admin-guide.md
---

Back-channel logout is implemented as a durable, retryable delivery workflow rather than a synchronous best-effort call. That keeps RP notification separate from the interactive logout request and allows delivery monitoring and retry behavior.

## Flow Shape

```mermaid
sequenceDiagram
  participant User
  participant WebAuth
  participant Outbox
  participant Dispatcher
  participant RP

  User->>WebAuth: logout request
  WebAuth->>Outbox: persist logout work item
  WebAuth-->>User: logout response
  Dispatcher->>Outbox: load pending work
  Dispatcher->>RP: POST logout_token
  RP-->>Dispatcher: success or retryable failure
```

## Responsibilities

- `LogoutHandler` shapes the logout token with the required claims and `typ=logout+jwt`.
- The Auth persistence layer stores the outbox work items.
- `BackchannelLogoutDispatcher` performs background fan-out with retry and health/metrics integration.

## Constraints

- Audit behavior should avoid logging raw JWTs.
- Replay protection and strict RP-side validation are important follow-on concerns.
- This area is operationally sensitive because it crosses service boundaries and may fail independently of user logout UX.

## Related Pages

- [[mrwhooidc-auth]]
- [[mrwhooidc-webauth]]
- [[oidc-protocol-surface]]