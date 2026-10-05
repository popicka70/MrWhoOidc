using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services.SubjectIdentifiers;

public interface ISectorIdentifierResolver
{
    /// <summary>
    /// Returns the client's pairwise sector identifier: the (lowercased) host of its
    /// <c>sector_identifier_uri</c>, or else the single host of its redirect URIs.
    /// Never performs network I/O; called on every token issuance.
    /// </summary>
    Task<string> ResolveSectorIdentifierAsync(Client client, CancellationToken ct = default);

    /// <summary>
    /// Fetches the <c>sector_identifier_uri</c> document and checks that it lists every redirect URI
    /// (OIDC Core §8.1). Call when a client is registered or saved, not at token time.
    /// </summary>
    Task ValidateSectorIdentifierUriAsync(string sectorIdentifierUri, IReadOnlyCollection<string> redirectUris, CancellationToken ct = default);
}
