---
title: MrWhoOidc.Security
type: entity
tags: [security, dpop, shared-library]
created: 2026-04-22
updated: 2026-04-22
related_files:
  - MrWhoOidc.Security/DPoP.cs
  - MrWhoOidc.Security/DPoPProofGenerator.cs
  - MrWhoOidc.Security/MrWhoOidc.Security.csproj
---

`MrWhoOidc.Security` is the small cross-cutting security library in the repo. Its current visible focus is DPoP: validating incoming proofs and generating outbound proofs with rotating key material.

## Responsibilities

- Validate DPoP proofs against request method, target URI, required claims, and optional access-token hash binding.
- Compute JWK thumbprints for `cnf.jkt` style confirmation behavior.
- Provide replay-cache and nonce-store abstractions.
- Generate DPoP proofs using ephemeral key material with controlled rotation.

## Notes

- The validator is intentionally strict about `typ`, `htm`, `htu`, `iat`, and `ath` semantics.
- The project keeps replay and nonce storage behind interfaces, which leaves room for in-memory or durable implementations.
- Because DPoP is a protocol edge feature, changes here often ripple into `MrWhoOidc.Auth`, downstream API behavior, and tests.

## Related Pages

- [[oidc-protocol-surface]]
- [[testing-strategy]]
- [[mrwhooidc-apiservice]]