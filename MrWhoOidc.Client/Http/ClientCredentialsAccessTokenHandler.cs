using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Client.Tokens;

namespace MrWhoOidc.Client.Http;

internal sealed class ClientCredentialsAccessTokenHandler : DelegatingHandler
{
    private readonly IMrWhoClientCredentialsManager _manager;
    private readonly string _registrationName;
    private readonly ILogger<ClientCredentialsAccessTokenHandler> _logger;

    public ClientCredentialsAccessTokenHandler(IMrWhoClientCredentialsManager manager, string registrationName, ILogger<ClientCredentialsAccessTokenHandler> logger)
    {
        _manager = manager;
        _registrationName = registrationName;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var result = await _manager.AcquireTokenAsync(_registrationName, cancellationToken).ConfigureAwait(false);
        if (result.IsError || string.IsNullOrEmpty(result.AccessToken))
        {
            _logger.LogWarning("Failed to acquire client-credentials access token for registration {Registration}. Error: {Error}", _registrationName, result.Error);
            throw new InvalidOperationException($"Failed to acquire client-credentials token for '{_registrationName}'.");
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", result.AccessToken);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
