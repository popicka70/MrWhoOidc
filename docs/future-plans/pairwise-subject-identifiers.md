# Pairwise Subject Identifiers

## Overview

Pairwise subject identifiers are a privacy-enhancing feature defined in the [OpenID Connect Core 1.0 specification](https://openid.net/specs/openid-connect-core-1_0.html#PairwiseAlg). When enabled, the IdP provides a different `sub` (subject) claim value to each client for the same user, preventing clients from correlating users across different applications.

## Current State

MrWhoOidc currently supports only **public** subject identifiers:
- The same `sub` value is returned to all clients for a given user
- Advertised in discovery document: `"subject_types_supported": ["public"]`

## Why Implement Pairwise?

### Privacy Benefits
- Prevents cross-client user tracking without user consent
- Clients cannot correlate users by comparing `sub` values
- Required for compliance with certain privacy regulations (GDPR principle of data minimization)

### Use Cases
- Multi-tenant SaaS where tenants shouldn't correlate users
- Consumer-facing applications with strict privacy requirements
- B2B scenarios where partners shouldn't share user identifiers

## Implementation Requirements

### 1. Database Schema Changes

Add storage for pairwise identifiers:

```sql
CREATE TABLE pairwise_identifiers (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_id UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    sector_identifier VARCHAR(2048) NOT NULL,
    pairwise_sub VARCHAR(255) NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    
    CONSTRAINT uq_user_sector UNIQUE (user_id, sector_identifier),
    CONSTRAINT uq_pairwise_sub UNIQUE (pairwise_sub)
);

CREATE INDEX ix_pairwise_user ON pairwise_identifiers(user_id);
CREATE INDEX ix_pairwise_sector ON pairwise_identifiers(sector_identifier);
```

### 2. Client Configuration

Extend the `Client` entity:

```csharp
public class Client
{
    // Existing properties...
    
    /// <summary>
    /// Subject type for this client: "public" or "pairwise".
    /// </summary>
    public string SubjectType { get; set; } = "public";
    
    /// <summary>
    /// Sector identifier URI for pairwise subject calculation.
    /// If not set, defaults to the host of the first redirect_uri.
    /// </summary>
    public string? SectorIdentifierUri { get; set; }
}
```

### 3. Sector Identifier Resolution

Per OIDC spec, the sector identifier determines which clients share the same pairwise `sub`:

```csharp
public interface ISectorIdentifierResolver
{
    /// <summary>
    /// Resolves the sector identifier for a client.
    /// </summary>
    Task<string> ResolveAsync(Client client, CancellationToken ct = default);
}

public class SectorIdentifierResolver : ISectorIdentifierResolver
{
    public async Task<string> ResolveAsync(Client client, CancellationToken ct = default)
    {
        // If sector_identifier_uri is provided, fetch and validate it
        if (!string.IsNullOrEmpty(client.SectorIdentifierUri))
        {
            // Must be HTTPS
            // Must return JSON array of redirect_uris
            // All client redirect_uris must be in the response
            return new Uri(client.SectorIdentifierUri).Host;
        }
        
        // Default: use host from first redirect_uri
        var firstRedirect = client.RedirectUris?.FirstOrDefault();
        if (string.IsNullOrEmpty(firstRedirect))
        {
            throw new InvalidOperationException(
                "Client must have at least one redirect_uri for pairwise subject type");
        }
        
        return new Uri(firstRedirect).Host;
    }
}
```

### 4. Pairwise Subject Generation

```csharp
public interface IPairwiseSubjectService
{
    Task<string> GetOrCreateAsync(Guid userId, string sectorIdentifier, CancellationToken ct = default);
}

public class PairwiseSubjectService : IPairwiseSubjectService
{
    private readonly AuthDbContext _db;
    
    public async Task<string> GetOrCreateAsync(Guid userId, string sectorIdentifier, CancellationToken ct = default)
    {
        // Check for existing mapping
        var existing = await _db.PairwiseIdentifiers
            .FirstOrDefaultAsync(p => p.UserId == userId && p.SectorIdentifier == sectorIdentifier, ct);
        
        if (existing != null)
            return existing.PairwiseSub;
        
        // Generate new pairwise identifier
        // Option A: Random UUID (simpler, no reversibility)
        var pairwiseSub = Guid.NewGuid().ToString();
        
        // Option B: HMAC-based (deterministic, requires server secret)
        // var pairwiseSub = ComputeHmac(userId, sectorIdentifier, _serverSecret);
        
        var entry = new PairwiseIdentifier
        {
            UserId = userId,
            SectorIdentifier = sectorIdentifier,
            PairwiseSub = pairwiseSub
        };
        
        _db.PairwiseIdentifiers.Add(entry);
        await _db.SaveChangesAsync(ct);
        
        return pairwiseSub;
    }
}
```

### 5. Token Generation Changes

Modify token generation to use pairwise subjects when configured:

```csharp
// In token generation logic
public async Task<string> GetSubjectClaimAsync(User user, Client client, CancellationToken ct)
{
    if (client.SubjectType == OidcConstants.SubjectTypes.Pairwise)
    {
        var sector = await _sectorResolver.ResolveAsync(client, ct);
        return await _pairwiseService.GetOrCreateAsync(user.Id, sector, ct);
    }
    
    // Public: return the actual user ID
    return user.Id.ToString();
}
```

### 6. Discovery Document Update

Update `DiscoveryHandler.cs` to advertise both types:

```csharp
["subject_types_supported"] = new[] 
{ 
    OidcConstants.SubjectTypes.Public, 
    OidcConstants.SubjectTypes.Pairwise 
},
```

### 7. Admin UI Changes

- Add subject type selection to client configuration
- Add sector identifier URI field (optional)
- Validation: sector_identifier_uri must be HTTPS if provided

## Testing Requirements

### Unit Tests
- Sector identifier resolution logic
- Pairwise subject generation and persistence
- Same user + same sector = same pairwise sub
- Same user + different sector = different pairwise sub

### Integration Tests
- Token contains correct pairwise sub for pairwise clients
- Token contains real sub for public clients
- UserInfo endpoint returns correct sub type
- Sector identifier URI validation (HTTPS, valid JSON, redirect_uri matching)

### Compliance Tests
- Verify against OIDC certification test suite for pairwise subjects

## Migration Considerations

### Backward Compatibility
- Default new clients to `public` subject type
- Existing clients remain `public` unless explicitly changed
- Changing a client from public to pairwise will change its `sub` values

### Data Migration
- No migration needed for existing users/clients
- Pairwise mappings created on-demand

## Security Considerations

1. **Pairwise sub must be unpredictable** - Use cryptographically secure generation
2. **Sector identifier validation** - Strictly validate sector_identifier_uri responses
3. **Storage security** - Pairwise mappings are sensitive (could deanonymize users)
4. **Audit logging** - Log pairwise identifier creation for security audits

## Estimated Effort

| Component | Effort |
|-----------|--------|
| Database schema | 2 hours |
| Entity/model changes | 2 hours |
| Sector identifier resolver | 4 hours |
| Pairwise subject service | 4 hours |
| Token generation integration | 4 hours |
| Admin UI changes | 4 hours |
| Unit tests | 4 hours |
| Integration tests | 4 hours |
| Documentation | 2 hours |
| **Total** | **~30 hours** |

## References

- [OIDC Core 1.0 - Pairwise Identifier Algorithm](https://openid.net/specs/openid-connect-core-1_0.html#PairwiseAlg)
- [OIDC Core 1.0 - Subject Identifier Types](https://openid.net/specs/openid-connect-core-1_0.html#SubjectIDTypes)
- [RFC 7636 - Sector Identifier](https://openid.net/specs/openid-connect-registration-1_0.html#SectorIdentifierValidation)

## Related Files

- `MrWhoOidc.Auth/Protocols/OidcConstants.cs` - Subject type constants (already added)
- `MrWhoOidc.WebAuth/Handlers/DiscoveryHandler.cs` - Discovery document
- `MrWhoOidc.WebAuth/Handlers/TokenHandler.cs` - Token generation
- `MrWhoOidc.Auth/Persistence/Entities/Client.cs` - Client entity
