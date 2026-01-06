using System.Threading;
using System.Threading.Tasks;

namespace MrWhoOidc.Auth.Services.Token;

/// <summary>
/// Domain service for creating tokens using the client_credentials grant.
/// </summary>
public interface IClientCredentialsTokenFactory
{
    /// <summary>
    /// Creates an access token for a client using the client_credentials grant.
    /// </summary>
    Task<(bool ok, object? payload, string? error, int status)> CreateTokenAsync(ClientCredentialsRequest request, CancellationToken ct = default);
}

/// <summary>
/// Request parameters for client credentials token creation.
/// </summary>
public record ClientCredentialsRequest(
    string ClientId,
    string Audience,
    string[] RequestedScopes,
    string Issuer,
    string? DpopJkt = null,
    string? MtlsX5tS256 = null
);
