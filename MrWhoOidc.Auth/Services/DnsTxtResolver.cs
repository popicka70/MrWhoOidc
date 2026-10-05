using DnsClient;
using Microsoft.Extensions.Logging;

namespace MrWhoOidc.Auth.Services;

/// <summary>Looks up DNS TXT records; injectable so domain-claim verification can be tested without DNS.</summary>
public interface IDnsTxtResolver
{
    /// <summary>
    /// Returns the TXT records at <paramref name="name"/>, each with its character-strings concatenated. A name that
    /// does not resolve, or a lookup that fails, yields an empty list.
    /// </summary>
    Task<IReadOnlyList<string>> ResolveTxtAsync(string name, CancellationToken ct = default);
}

internal sealed class DnsClientTxtResolver(ILogger<DnsClientTxtResolver> logger) : IDnsTxtResolver
{
    // No answer caching: an admin who has just published the record retries verification right away.
    private readonly LookupClient _client = new(new LookupClientOptions
    {
        UseCache = false,
        Timeout = TimeSpan.FromSeconds(5),
        Retries = 2,
        ThrowDnsErrors = false
    });

    public async Task<IReadOnlyList<string>> ResolveTxtAsync(string name, CancellationToken ct = default)
    {
        try
        {
            var response = await _client.QueryAsync(name, QueryType.TXT, QueryClass.IN, ct).ConfigureAwait(false);
            if (response.HasError)
            {
                logger.LogInformation("TXT lookup for {Name} returned {Error}", name, response.ErrorMessage);
                return [];
            }

            return response.Answers.TxtRecords()
                .Select(r => string.Concat(r.Text))
                .ToList();
        }
        catch (DnsResponseException ex)
        {
            logger.LogWarning(ex, "TXT lookup for {Name} failed", name);
            return [];
        }
    }
}
