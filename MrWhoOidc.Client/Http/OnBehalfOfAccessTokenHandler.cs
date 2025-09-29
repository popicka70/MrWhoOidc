using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Client.Tokens;

namespace MrWhoOidc.Client.Http;

internal sealed class OnBehalfOfAccessTokenHandler : DelegatingHandler
{
    private readonly IMrWhoOnBehalfOfManager _manager;
    private readonly string _registrationName;
    private readonly Func<CancellationToken, ValueTask<string?>> _subjectTokenAccessor;
    private readonly ILogger<OnBehalfOfAccessTokenHandler> _logger;

    public OnBehalfOfAccessTokenHandler(IMrWhoOnBehalfOfManager manager, string registrationName, Func<CancellationToken, ValueTask<string?>> subjectTokenAccessor, ILogger<OnBehalfOfAccessTokenHandler> logger)
    {
        _manager = manager;
        _registrationName = registrationName;
        _subjectTokenAccessor = subjectTokenAccessor;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var subjectToken = await _subjectTokenAccessor(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(subjectToken))
        {
            _logger.LogDebug("Subject token unavailable for on-behalf-of registration {Registration}; skipping token attachment.", _registrationName);
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var result = await _manager.AcquireTokenAsync(_registrationName, subjectToken!, cancellationToken).ConfigureAwait(false);
        if (result.IsError || string.IsNullOrEmpty(result.AccessToken))
        {
            _logger.LogWarning("Failed to acquire on-behalf-of access token for registration {Registration}. Error: {Error}", _registrationName, result.Error);
            throw new InvalidOperationException($"Failed to acquire on-behalf-of token for '{_registrationName}'.");
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", result.AccessToken);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
