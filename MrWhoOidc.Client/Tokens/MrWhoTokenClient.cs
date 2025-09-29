using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Client.Discovery;
using MrWhoOidc.Client.Options;
using MrWhoOidc.Security;

namespace MrWhoOidc.Client.Tokens;

internal sealed class MrWhoTokenClient : IMrWhoTokenClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMrWhoDiscoveryClient _discovery;
    private readonly IOptionsMonitor<MrWhoOidcClientOptions> _options;
    private readonly IDPoPProofGenerator _dpop;
    private readonly ILogger<MrWhoTokenClient> _logger;
    private string? _dpopNonce;
    private readonly object _nonceLock = new();

    public MrWhoTokenClient(IHttpClientFactory httpClientFactory, IMrWhoDiscoveryClient discovery, IOptionsMonitor<MrWhoOidcClientOptions> options, IDPoPProofGenerator dpop, ILogger<MrWhoTokenClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _discovery = discovery;
        _options = options;
        _dpop = dpop;
        _logger = logger;
    }

    public ValueTask<TokenResult> ExchangeCodeAsync(string code, Uri redirectUri, string? codeVerifier = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        var parameters = new Dictionary<string, string?>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri.ToString()
        };

        if (!string.IsNullOrEmpty(codeVerifier))
        {
            parameters["code_verifier"] = codeVerifier;
        }

        return SendAsync(parameters, cancellationToken);
    }

    public ValueTask<TokenResult> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        var parameters = new Dictionary<string, string?>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken
        };

        return SendAsync(parameters, cancellationToken);
    }

    public ValueTask<TokenResult> ClientCredentialsAsync(IEnumerable<string>? scopes = null, CancellationToken cancellationToken = default)
    {
        var scopeList = scopes?.ToArray();
        if (scopeList is null || scopeList.Length == 0)
        {
            scopeList = _options.CurrentValue.Scopes.ToArray();
        }

        var request = new ClientCredentialsRequest
        {
            Scopes = scopeList
        };

        return ClientCredentialsAsync(request, cancellationToken);
    }

    public ValueTask<TokenResult> ClientCredentialsAsync(ClientCredentialsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var parameters = new Dictionary<string, string?>
        {
            ["grant_type"] = "client_credentials"
        };

        if (request.Scopes is { Count: > 0 })
        {
            parameters["scope"] = string.Join(' ', request.Scopes);
        }

        if (!string.IsNullOrEmpty(request.Resource))
        {
            parameters["resource"] = request.Resource;
        }

        if (!string.IsNullOrEmpty(request.Audience))
        {
            parameters["audience"] = request.Audience;
        }

        foreach (var kv in request.AdditionalParameters)
        {
            if (!string.IsNullOrWhiteSpace(kv.Key))
            {
                parameters[kv.Key] = kv.Value;
            }
        }

        return SendAsync(parameters, cancellationToken);
    }

    public ValueTask<TokenResult> TokenExchangeAsync(TokenExchangeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SubjectToken);

        var parameters = new Dictionary<string, string?>
        {
            ["grant_type"] = request.GrantType,
            ["subject_token"] = request.SubjectToken,
            ["subject_token_type"] = request.SubjectTokenType
        };

        if (!string.IsNullOrEmpty(request.RequestedTokenType))
        {
            parameters["requested_token_type"] = request.RequestedTokenType;
        }
        if (!string.IsNullOrEmpty(request.Resource))
        {
            parameters["resource"] = request.Resource;
        }
        if (!string.IsNullOrEmpty(request.Audience))
        {
            parameters["audience"] = request.Audience;
        }
        if (!string.IsNullOrEmpty(request.Scope))
        {
            parameters["scope"] = request.Scope;
        }

        foreach (var kv in request.AdditionalParameters)
        {
            if (!string.IsNullOrWhiteSpace(kv.Key))
            {
                parameters[kv.Key] = kv.Value;
            }
        }

        return SendAsync(parameters, cancellationToken);
    }

    private async ValueTask<TokenResult> SendAsync(Dictionary<string, string?> parameters, CancellationToken cancellationToken)
    {
        var opts = _options.CurrentValue;
        var discovery = await _discovery.GetAsync(cancellationToken).ConfigureAwait(false);
        var tokenEndpoint = opts.TokenEndpoint ?? discovery.RequireHttps(discovery.TokenEndpoint, opts.RequireHttpsMetadata);

        var form = new Dictionary<string, string?>(parameters, StringComparer.Ordinal)
        {
            ["client_id"] = opts.ClientId
        };

        var authHeader = ApplyClientAuthentication(form, opts);

        using var content = new FormUrlEncodedContent(form.Where(kv => kv.Value is not null)
            .Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value!)));

        var httpClientName = string.IsNullOrWhiteSpace(opts.HttpClientName)
            ? MrWhoOidcClientDefaults.DefaultHttpClientName
            : opts.HttpClientName;

        var httpClient = _httpClientFactory.CreateClient(httpClientName);
        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = content
        };

        if (authHeader is not null)
        {
            requestMessage.Headers.Authorization = authHeader;
        }

        if (opts.UseDpop)
        {
            string? nonce;
            lock (_nonceLock)
            {
                nonce = _dpopNonce;
            }
            var proof = await _dpop.CreateProofAsync(new DPoPProofRequest(HttpMethod.Post.Method, tokenEndpoint, AccessToken: null, Nonce: nonce), cancellationToken).ConfigureAwait(false);
            requestMessage.Headers.Add("DPoP", proof);
        }

        using var activity = MrWhoOidcClientDefaults.ActivitySource.StartActivity("Token.Request", ActivityKind.Client);
        var sw = Stopwatch.StartNew();
        TokenResult result;
        try
        {
            var response = await httpClient.SendAsync(requestMessage, cancellationToken).ConfigureAwait(false);
            if (response.Headers.TryGetValues("DPoP-Nonce", out var nonceValues))
            {
                var nonce = nonceValues.FirstOrDefault();
                if (!string.IsNullOrEmpty(nonce))
                {
                    lock (_nonceLock)
                    {
                        _dpopNonce = nonce;
                    }
                }
            }
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var error = TryParseError(responseText, out var description);
                _logger.LogWarning("Token endpoint returned {StatusCode} and error {Error}", (int)response.StatusCode, error);
                result = TokenResult.FromError(error, description, responseText);
            }
            else
            {
                var payload = TryParseSuccess(responseText);
                if (payload.AccessToken is null)
                {
                    _logger.LogWarning("Token endpoint response missing access_token");
                    result = TokenResult.FromError("invalid_response", "Token endpoint response missing access_token.", responseText);
                }
                else
                {
                    result = TokenResult.FromSuccess(payload);
                }
            }

            if (activity is not null)
            {
                activity.SetTag("oauth.grant_type", form.TryGetValue("grant_type", out var grant) ? grant : null);
                activity.SetTag("oauth.is_error", result.IsError);
                if (result.IsError)
                {
                    activity.SetTag("oauth.error", result.Error);
                }
            }

            return result;
        }
        finally
        {
            sw.Stop();
            var grant = form.TryGetValue("grant_type", out var grantType) ? grantType ?? "unknown" : "unknown";
            MrWhoOidcClientDefaults.TokenLatency.Record(sw.Elapsed.TotalMilliseconds);
            MrWhoOidcClientDefaults.TokenRequests.Add(1, KeyValuePair.Create<string, object?>("grant_type", grant));
        }
    }

    private AuthenticationHeaderValue? ApplyClientAuthentication(Dictionary<string, string?> form, MrWhoOidcClientOptions opts)
    {
        if (opts.PublicClient)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(opts.ClientAssertion))
        {
            form["client_assertion_type"] = opts.ClientAssertionType;
            form["client_assertion"] = opts.ClientAssertion;
            return null;
        }

        if (!string.IsNullOrEmpty(opts.ClientSecret))
        {
            // RFC 6749 recommends client authentication via Basic header.
            var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes(opts.ClientId + ":" + opts.ClientSecret));
            return new AuthenticationHeaderValue("Basic", credentials);
        }

        return null;
    }

    private static TokenResponsePayload TryParseSuccess(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            string? Get(string name) => root.TryGetProperty(name, out var value) ? value.GetString() : null;
            long? GetLong(string name) => root.TryGetProperty(name, out var value) && value.TryGetInt64(out var l) ? l : null;

            return new TokenResponsePayload(
                Get("access_token"),
                Get("refresh_token"),
                Get("token_type"),
                GetLong("expires_in"),
                Get("id_token"),
                Get("scope"),
                json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Failed to parse token response JSON.", ex);
        }
    }

    private static string TryParseError(string json, out string? description)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            description = root.TryGetProperty("error_description", out var desc) ? desc.GetString() : null;
            return root.TryGetProperty("error", out var err) ? err.GetString() ?? "invalid_response" : "invalid_response";
        }
        catch (JsonException)
        {
            description = null;
            return "invalid_response";
        }
    }
}
