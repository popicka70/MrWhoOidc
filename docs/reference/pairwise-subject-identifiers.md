# Pairwise Subject Identifiers

Pairwise subjects are implemented, not a future feature. They provide a stable opaque `sub` for a user within a tenant and sector. Clients in the same sector can receive the same subject; this is not necessarily a different identifier for every client.

## Configuration and Resolution

- Set the client's `SubjectType` to `pairwise`; `public` retains the normal public subject behavior.
- If `SectorIdentifierUri` is supplied, it must satisfy the server's HTTPS and safe-fetch validation and return a JSON array containing all registered login redirect URIs. The URI's normalized host identifies the sector.
- Without that URI, registered login redirect URIs must resolve to exactly one host; that normalized host is the sector.
- A failed sector validation is an issuance error, not a fallback to a public subject. Keep the sector document reachable and synchronized with client registrations.

[SectorIdentifierResolver](../../MrWhoOidc.Auth/Services/SubjectIdentifiers/SectorIdentifierResolver.cs) resolves sectors. [PairwiseSubjectService](../../MrWhoOidc.Auth/Services/SubjectIdentifiers/PairwiseSubjectService.cs) generates opaque random values and persists mappings by tenant, user, and sector. Restoring these mappings is part of preserving client account identity.

## Integration Risks

Changing a client's subject type or sector can change its user identifiers. Plan account mapping with the relying party before changing production registration. Do not delete pairwise mappings as cache cleanup.

Pairwise subjects reduce correlation through `sub`; they do not prevent correlation through shared email addresses, other claims, logs, or application data. They are not a standalone privacy-compliance guarantee.

Check each flow separately. In particular, [delegated-access review](../impersonation-and-delegated-access-review-2026-08-01.md) records a concern about delegated-token subject semantics; do not infer that every token-exchange path uses the ordinary pairwise service.

## Verification

Use controlled accounts and clients to verify stable subjects for the same user/sector, different subjects across sectors, consistency between ordinary ID-token and UserInfo responses, rejection of invalid sector documents, and continuity after restore. Do not expose subject mapping data in public test reports.

## References

- [OIDC Core pairwise algorithm](https://openid.net/specs/openid-connect-core-1_0.html#PairwiseAlg)
- [OIDC Registration sector validation](https://openid.net/specs/openid-connect-registration-1_0.html#SectorIdentifierValidation)
- [Original implementation notes](../future-plans/pairwise-subject-identifiers.md)

Reviewed against the resolver and mapping service on 2026-09-05; no live protocol run was performed.
