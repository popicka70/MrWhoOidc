using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.IdentityProviders;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Services;
using MrWhoOidc.WebAuth.Handlers.External;
using MrWhoOidc.WebAuth.Observability;
using MrWhoOidc.WebAuth.Extensions;

namespace MrWhoOidc.WebAuth.Handlers;

public interface IExternalOidcHandler
{
    Task<IResult> StartAsync(HttpContext http);
    Task<IResult> CallbackAsync(HttpContext http);
    Task<IResult> ConfirmLinkAsync(HttpContext http);
}

/// <summary>
/// Main orchestrator for external OIDC authentication flows.
/// Delegates to specialized services for specific responsibilities.
/// </summary>
public sealed class ExternalOidcHandler : IExternalOidcHandler
{
    private readonly AuthDbContext _db;
    private readonly IClaimMappingService _mapper;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly ILogger<ExternalOidcHandler> _logger;

    // Specialized services
    private readonly IExternalOidcStateManager _stateManager;
    private readonly IExternalOidcCorrelationManager _correlationManager;
    private readonly IExternalOidcDiscoveryService _discoveryService;
    private readonly IExternalOidcRequestBuilder _requestBuilder;
    private readonly IExternalOidcTokenExchangeService _tokenExchangeService;
    private readonly IExternalOidcTokenValidator _tokenValidator;
    private readonly IExternalOidcUserProvisioner _userProvisioner;
    private readonly IExternalOidcSessionManager _sessionManager;
    private readonly IExternalOidcErrorHandler _errorHandler;
    private readonly IExternalOidcMetricsRecorder _metricsRecorder;

    public ExternalOidcHandler(
        AuthDbContext db,
        IClaimMappingService mapper,
        ITenantAccessor tenantAccessor,
        IExternalOidcStateManager stateManager,
        IExternalOidcCorrelationManager correlationManager,
        IExternalOidcDiscoveryService discoveryService,
        IExternalOidcRequestBuilder requestBuilder,
        IExternalOidcTokenExchangeService tokenExchangeService,
        IExternalOidcTokenValidator tokenValidator,
        IExternalOidcUserProvisioner userProvisioner,
        IExternalOidcSessionManager sessionManager,
        IExternalOidcErrorHandler errorHandler,
        IExternalOidcMetricsRecorder metricsRecorder,
        ILogger<ExternalOidcHandler> logger)
    {
        _db = db;
        _mapper = mapper;
        _tenantAccessor = tenantAccessor;
        _stateManager = stateManager;
        _correlationManager = correlationManager;
        _discoveryService = discoveryService;
        _requestBuilder = requestBuilder;
        _tokenExchangeService = tokenExchangeService;
        _tokenValidator = tokenValidator;
        _userProvisioner = userProvisioner;
        _sessionManager = sessionManager;
        _errorHandler = errorHandler;
        _metricsRecorder = metricsRecorder;
        _logger = logger;
    }

    public async Task<IResult> StartAsync(HttpContext http)
    {
        var startTs = DateTime.UtcNow;
        _metricsRecorder.RecordStartRequest();

        var providerName = http.Request.Query["provider"].ToString();
        var returnUrl = http.Request.Query["returnUrl"].ToString();
        var clientId = http.Request.Query["clientId"].ToString();
        var requestedHandle = http.Request.Query["cid_ref"].ToString();
        var isLinking = http.Request.Query["link"].ToString().ToLowerInvariant() == "true";
        var isPlatformProvider = http.Request.Query["platform"].ToString().Equals("true", StringComparison.OrdinalIgnoreCase);

        var correlation = await _correlationManager.EnsureCorrelationAsync(http, null, requestedHandle);
        using var scope = CorrelationLogging.BeginScope(_logger, correlation.CorrelationId, providerName, clientId);

        if (string.IsNullOrEmpty(providerName) || string.IsNullOrEmpty(returnUrl))
        {
            _logger.LogWarning("External start rejected due to missing parameters. providerPresent={HasProvider} returnUrlPresent={HasReturnUrl}",
                !string.IsNullOrEmpty(providerName),
                !string.IsNullOrEmpty(returnUrl));
            _metricsRecorder.RecordStartOutcome(false, startTs, providerName, clientId, "missing_params");
            return _errorHandler.CreateFriendlyError(returnUrl, clientId, correlation.Handle, "Missing required parameters", "missing_params");
        }

        var provider = await ResolveProviderForStartAsync(providerName, clientId, isLinking, isPlatformProvider, http.RequestAborted);

        if (provider is null || string.IsNullOrWhiteSpace(provider.ConfigJson))
        {
            _logger.LogWarning("External start unknown provider {Provider}", providerName);
            _metricsRecorder.RecordStartOutcome(false, startTs, providerName, clientId, "unknown_provider");
            return _errorHandler.CreateFriendlyError(returnUrl, clientId, correlation.Handle, "Unknown provider", "unknown_provider");
        }

        if (!OidcProviderConfig.TryParse(provider.ConfigJson!, out var cfg).ok || cfg is null)
        {
            _logger.LogError("External start invalid configuration for provider {Provider}", providerName);
            _metricsRecorder.RecordStartOutcome(false, startTs, providerName, clientId, "invalid_provider_config");
            return _errorHandler.CreateFriendlyError(returnUrl, clientId, correlation.Handle, "Invalid provider configuration", "invalid_provider_config");
        }

        _logger.LogInformation("External OIDC start: initiating discovery and redirect.");
        returnUrl = ExternalOidcUrlHelpers.EnsureCidRef(returnUrl, correlation.Handle);

        var discovery = await _discoveryService.DiscoverAsync(cfg.Authority, cfg.DiscoveryUrl, http.RequestAborted);
        if (!discovery.Success)
        {
            _logger.LogWarning("Discovery failed: {Error}", discovery.ErrorMessage);
            _metricsRecorder.RecordStartOutcome(false, startTs, providerName, clientId, discovery.ErrorCode ?? "discovery_failed");
            return _errorHandler.CreateFriendlyError(returnUrl, clientId, correlation.Handle, discovery.ErrorMessage!, discovery.ErrorCode);
        }

        var (verifier, challenge) = ExternalOidcRequestBuilder.GeneratePkce();
        var nonce = Guid.NewGuid().ToString("N");

        var stateModel = new StateModel
        {
            Provider = providerName,
            ProviderId = provider.Id,
            TenantId = provider.TenantId,
            IsPlatformProvider = isPlatformProvider,
            CodeVerifier = verifier,
            ReturnUrl = returnUrl,
            Nonce = nonce,
            ClientId = clientId,
            CorrelationHandle = correlation.Handle,
            IsLinking = isLinking,
            TargetUserId = (http.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value is string sub && Guid.TryParse(sub, out var uid)) ? uid : null,
            Version = 2
        };

        var state = _stateManager.ProtectState(stateModel);

        var authRequest = await _requestBuilder.BuildAuthorizationRequestAsync(
            http, provider, cfg, discovery.Response!, state, nonce, challenge, returnUrl);

        _logger.LogInformation("Redirecting to authorization endpoint ({Mechanism})", authRequest.Mechanism);
        _metricsRecorder.RecordStartOutcome(true, startTs, providerName, clientId, authRequest.Mechanism);

        return Results.Redirect(authRequest.RedirectUrl);
    }

    public async Task<IResult> CallbackAsync(HttpContext http)
    {
        var cbStart = DateTime.UtcNow;
        _metricsRecorder.RecordCallbackRequest();

        _logger.LogInformation("OAuth callback received. Path: {Path}", http.Request.Path);

        var idTokenFromAuth = http.Request.Query["id_token"].ToString();
        var stateRaw = http.Request.Query["state"].ToString();
        var error = http.Request.Query["error"].ToString();
        var errorDescription = http.Request.Query["error_description"].ToString();

        if (string.IsNullOrEmpty(stateRaw))
            return Results.BadRequest("Missing state");

        var state = _stateManager.UnprotectState(stateRaw);
        if (state is null)
            return Results.BadRequest("Invalid state");

        var correlationResolution = await _correlationManager.ResolveCorrelationAsync(http, state);
        if (!correlationResolution.Success)
        {
            // Handle stale/invalid correlation by generating a new one
            var newCorrelation = await _correlationManager.EnsureCorrelationAsync(http, null, null);
            state.CorrelationId = newCorrelation.CorrelationId;
            state.CorrelationHandle = newCorrelation.Handle;
            if (!string.IsNullOrEmpty(state.ReturnUrl))
            {
                state.ReturnUrl = ExternalOidcUrlHelpers.EnsureCidRef(state.ReturnUrl, newCorrelation.Handle);
            }

            _logger.LogWarning("Correlation handle stale during callback handleHash={Handle}",
                ExternalOidcCorrelationManager.HashHandleForLog(correlationResolution.Handle));
            _metricsRecorder.RecordCallbackOutcome(false, cbStart, state.Provider, state.ClientId,
                "cid_ref_stale", correlationPresent: false, handleStale: true);
            return _errorHandler.CreateFriendlyError(state.ReturnUrl, state.ClientId, newCorrelation.Handle,
                "Your sign-in session expired. Please start again.", "cid_ref_stale");
        }

        using var scope = CorrelationLogging.BeginScope(_logger, correlationResolution.CorrelationId, state.Provider, state.ClientId);
        var correlationPresent = !string.IsNullOrEmpty(state.CorrelationHandle);
        bool? handleStaleMarker = correlationResolution.FromHandle ? correlationResolution.HandleStale : (bool?)null;

        if (!string.IsNullOrEmpty(error))
        {
            _logger.LogWarning("External callback contained error from IdP: {Error} - {Description}", error, errorDescription);
            _metricsRecorder.RecordCallbackOutcome(false, cbStart, state.Provider, state.ClientId, "upstream_error", correlationPresent, handleStaleMarker);
            return _errorHandler.CreateFriendlyError(state.ReturnUrl, state.ClientId, state.CorrelationHandle,
                $"Upstream error: {error}{(string.IsNullOrEmpty(errorDescription) ? string.Empty : " - " + errorDescription)}", "upstream_error");
        }

        var code = http.Request.Query["code"].ToString();
        if (string.IsNullOrEmpty(code))
        {
            _logger.LogWarning("External callback missing authorization code");
            _metricsRecorder.RecordCallbackOutcome(false, cbStart, state.Provider, state.ClientId, "missing_code", correlationPresent, handleStaleMarker);
            return _errorHandler.CreateFriendlyError(state.ReturnUrl, state.ClientId, state.CorrelationHandle,
                "Missing authorization code from upstream IdP.", "missing_code");
        }

        var provider = await ResolveProviderForCallbackAsync(state, http.RequestAborted);

        if (provider is null || string.IsNullOrWhiteSpace(provider.ConfigJson))
        {
            _metricsRecorder.RecordCallbackOutcome(false, cbStart, state.Provider, state.ClientId, "unknown_provider", correlationPresent, handleStaleMarker);
            return _errorHandler.CreateFriendlyError(state.ReturnUrl, state.ClientId, state.CorrelationHandle,
                "Unknown or disabled provider.", "unknown_provider");
        }

            if (!state.IsLinking && !await IsProviderAllowedForClientAsync(provider.Id, provider.TenantId, state.ClientId, http.RequestAborted))
            {
                _metricsRecorder.RecordCallbackOutcome(false, cbStart, state.Provider, state.ClientId, "provider_not_allowed", correlationPresent, handleStaleMarker);
                return _errorHandler.CreateFriendlyError(state.ReturnUrl, state.ClientId, state.CorrelationHandle,
                "Provider is not allowed for this client.", "provider_not_allowed");
            }

        if (!OidcProviderConfig.TryParse(provider.ConfigJson!, out var cfg).ok || cfg is null)
        {
            _metricsRecorder.RecordCallbackOutcome(false, cbStart, state.Provider, state.ClientId, "invalid_config", correlationPresent, handleStaleMarker);
            return _errorHandler.CreateFriendlyError(state.ReturnUrl, state.ClientId, state.CorrelationHandle,
                "Invalid provider configuration.", "invalid_config");
        }

        var discovery = await _discoveryService.DiscoverAsync(cfg.Authority, cfg.DiscoveryUrl, http.RequestAborted);
        if (!discovery.Success)
        {
            _logger.LogWarning("Callback discovery failed: {Error}", discovery.ErrorMessage);
            _metricsRecorder.RecordCallbackOutcome(false, cbStart, state.Provider, state.ClientId,
                discovery.ErrorCode ?? "discovery_failed", correlationPresent, handleStaleMarker);
            return _errorHandler.CreateFriendlyError(state.ReturnUrl, state.ClientId, state.CorrelationHandle,
                discovery.ErrorMessage!, discovery.ErrorCode);
        }

        var redirectUri = (state.IsPlatformProvider ? http.GetPlatformIssuer() : http.GetIssuer()) + "/auth/external/callback";

        // Never log any part of the upstream authorization code — it is a bearer credential that
        // can be exchanged for tokens at the upstream IdP.
        _logger.LogInformation("External authorization code received; tokenEndpoint={TokenEndpoint}, redirectUri={RedirectUri}, clientId={ClientId}",
            discovery.Response!.TokenEndpoint,
            redirectUri,
            cfg.ClientId);

        var tokenResult = await _tokenExchangeService.ExchangeCodeForTokensAsync(
            code, discovery.Response!.TokenEndpoint, redirectUri, cfg.ClientId, cfg.ClientSecret,
            state.CodeVerifier, http.RequestAborted);

        if (!tokenResult.Success)
        {
            _metricsRecorder.RecordCallbackOutcome(false, cbStart, state.Provider, state.ClientId,
                tokenResult.ErrorCode!, correlationPresent, handleStaleMarker);
            return _errorHandler.CreateFriendlyError(state.ReturnUrl, state.ClientId, state.CorrelationHandle,
                tokenResult.ErrorMessage!, tokenResult.ErrorCode);
        }

        var idToken = !string.IsNullOrEmpty(idTokenFromAuth) ? idTokenFromAuth : tokenResult.IdToken;
        if (!string.IsNullOrEmpty(idToken) && !http.Items.ContainsKey("external.id_token"))
        {
            http.Items["external.id_token"] = idToken;
        }

        var userInfo = new UserInfo
        {
            Issuer = discovery.Response!.Issuer
        };

        if (!string.IsNullOrEmpty(idToken))
        {
            var validationResult = await _tokenValidator.ValidateIdTokenAsync(
                idToken, discovery.Response.JwksUri, discovery.Response.Issuer, cfg.ClientId, state.Nonce, http.RequestAborted);

            if (!validationResult.Success)
            {
                _logger.LogWarning("ID token validation failed: {Error}", validationResult.ErrorMessage);
                _metricsRecorder.RecordCallbackOutcome(false, cbStart, state.Provider, state.ClientId,
                    validationResult.ErrorCode!, correlationPresent, handleStaleMarker);
                return _errorHandler.CreateFriendlyError(state.ReturnUrl, state.ClientId, state.CorrelationHandle,
                    validationResult.ErrorMessage!, validationResult.ErrorCode);
            }

            userInfo.Subject = validationResult.Subject;
            userInfo.Issuer = validationResult.Issuer;
            userInfo.Email = validationResult.Email;
            userInfo.Name = validationResult.Name;
            userInfo.Acr = validationResult.Acr;
            userInfo.Amrs = validationResult.Amrs;
        }

        userInfo = await _tokenExchangeService.EnrichUserInfoAsync(
            userInfo, tokenResult.AccessToken, discovery.Response.UserinfoEndpoint, http.RequestAborted);

        if (string.IsNullOrEmpty(userInfo.Subject) || string.IsNullOrEmpty(userInfo.Issuer))
        {
            _metricsRecorder.RecordCallbackOutcome(false, cbStart, state.Provider, state.ClientId,
                "missing_sub_or_issuer", correlationPresent, handleStaleMarker);
            return _errorHandler.CreateFriendlyError(state.ReturnUrl, state.ClientId, state.CorrelationHandle,
                "Missing subject/issuer from upstream IdP", "missing_sub_or_issuer");
        }

        var sourceClaims = new Dictionary<string, string?>
        {
            ["sub"] = userInfo.Subject,
            ["iss"] = userInfo.Issuer,
            ["email"] = userInfo.Email,
            ["name"] = userInfo.Name,
            ["acr"] = userInfo.Acr,
            ["amr"] = userInfo.Amrs is { Length: > 0 } ? string.Join(' ', userInfo.Amrs) : null
        };

        var mapped = await _mapper.ApplyAsync(provider.Id, sourceClaims!, http.RequestAborted);

        var provisioningResult = await _userProvisioner.ProvisionOrLinkUserAsync(
            state.Provider, userInfo.Issuer!, userInfo.Subject!, userInfo.Email, userInfo.Name,
            state.ReturnUrl, state.ClientId, correlationResolution.CorrelationId, state.CorrelationHandle,
            mapped, state.IsLinking, state.TargetUserId, state.IsPlatformProvider, http.RequestAborted);

        if (!provisioningResult.Success)
        {
            _metricsRecorder.RecordCallbackOutcome(false, cbStart, state.Provider, state.ClientId,
                provisioningResult.Outcome!, correlationPresent, handleStaleMarker);
            return _errorHandler.CreateFriendlyError(state.ReturnUrl, state.ClientId, state.CorrelationHandle,
                provisioningResult.ErrorMessage!, provisioningResult.ErrorCode);
        }

        if (provisioningResult.RequiresConfirmation)
        {
            var token = _stateManager.ProtectConfirm(provisioningResult.ConfirmationModel!);
            var existingUser = await _db.Users.FindAsync(provisioningResult.ConfirmationModel!.TargetUserId);
            return _errorHandler.CreateConfirmPage(token, state.ReturnUrl, state.ClientId,
                correlationResolution.CorrelationId, provisioningResult.ConfirmationModel.Email!,
                existingUser?.Name ?? existingUser?.Username ?? "User");
        }

        // MFA gate: mirror password login. If the user has TOTP enabled or the tenant requires MFA,
        // do NOT issue the auth cookie yet — issue the short-lived preauth cookie and send the user
        // to the TOTP challenge (or MFA enrollment) first.
        var mfaRedirect = await _sessionManager.GetMfaRedirectIfRequiredAsync(
            http, provisioningResult.UserId!.Value, userInfo.Name, userInfo.Email, state.ReturnUrl);
        if (!string.IsNullOrEmpty(mfaRedirect))
        {
            _metricsRecorder.RecordCallbackOutcome(false, cbStart, state.Provider, state.ClientId,
                "mfa_required", correlationPresent, handleStaleMarker);
            return Results.Redirect(mfaRedirect);
        }

        await _sessionManager.SignInAsync(http, provisioningResult.UserId!.Value, userInfo.Name, userInfo.Email,
            state.Provider, userInfo.Acr, userInfo.Amrs, mapped, idToken);

        _sessionManager.SetLastProviderCookie(http, state.Provider, state.ClientId);

        _logger.LogInformation("External sign-in successful; redirecting to returnUrl");
        _metricsRecorder.RecordCallbackOutcome(true, cbStart, state.Provider, state.ClientId,
            provisioningResult.Outcome!, correlationPresent, handleStaleMarker);

        return Results.Redirect(AuthorizeReturnUrlHelper.ConsumePromptValues(state.ReturnUrl, "login", "select_account") ?? "/");
    }

    private async Task<IdentityProvider?> ResolveProviderForStartAsync(string providerName, string? clientId, bool isLinking, bool isPlatformProvider, CancellationToken ct)
    {
        var providerQuery = _db.IdentityProviders.AsNoTracking()
            .Where(p => p.Name == providerName && p.Enabled);

        if (isPlatformProvider)
        {
            if (!string.IsNullOrWhiteSpace(clientId) || isLinking)
            {
                return null;
            }

            providerQuery = providerQuery.Where(p => p.TenantId == null);
        }
        else if (GetCurrentTenantIdForProviderScope() is { } tenantId)
        {
            providerQuery = providerQuery.Where(p => p.TenantId == tenantId);
        }

        if (!isLinking && !string.IsNullOrWhiteSpace(clientId))
        {
            return await providerQuery
                .Join(
                    _db.ClientIdentityProviders.AsNoTracking().Where(m => m.Enabled),
                    p => p.Id,
                    m => m.IdentityProviderId,
                    (p, m) => new { Provider = p, Mapping = m })
                .Join(
                    _db.Clients.AsNoTracking().Where(c => c.ClientId == clientId && c.AllowExternalIdp),
                    pm => pm.Mapping.ClientId,
                    c => c.Id,
                    (pm, c) => new { pm.Provider, Client = c })
                .Where(x => x.Provider.TenantId == x.Client.TenantId)
                .Select(x => x.Provider)
                .FirstOrDefaultAsync(ct);
        }

        var matches = await providerQuery.Take(2).ToListAsync(ct);
        return matches.Count == 1 ? matches[0] : null;
    }

    private async Task<IdentityProvider?> ResolveProviderForCallbackAsync(StateModel state, CancellationToken ct)
    {
        var providerQuery = _db.IdentityProviders.AsNoTracking().Where(p => p.Enabled);

        if (state.ProviderId is { } providerId)
        {
            providerQuery = providerQuery.Where(p => p.Id == providerId);
        }
        else
        {
            providerQuery = providerQuery.Where(p => p.Name == state.Provider);
        }

        if (state.IsPlatformProvider)
        {
            providerQuery = providerQuery.Where(p => p.TenantId == null);
        }
        else if (state.TenantId is { } stateTenantId)
        {
            providerQuery = providerQuery.Where(p => p.TenantId == stateTenantId);
        }
        else if (GetCurrentTenantIdForProviderScope() is { } currentTenantId)
        {
            providerQuery = providerQuery.Where(p => p.TenantId == currentTenantId);
        }

        var matches = await providerQuery.Take(2).ToListAsync(ct);
        return matches.Count == 1 ? matches[0] : null;
    }

    private async Task<bool> IsProviderAllowedForClientAsync(Guid providerId, Guid? providerTenantId, string? clientId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return true;
        }

        if (!providerTenantId.HasValue)
        {
            return false;
        }

        return await _db.ClientIdentityProviders.AsNoTracking()
            .Where(m => m.IdentityProviderId == providerId && m.Enabled)
            .Join(
                _db.Clients.AsNoTracking().Where(c => c.ClientId == clientId && c.AllowExternalIdp && c.TenantId == providerTenantId),
                m => m.ClientId,
                c => c.Id,
                (m, c) => c.Id)
            .AnyAsync(ct);
    }

    private Guid? GetCurrentTenantIdForProviderScope()
    {
        var currentTenant = _tenantAccessor.CurrentTenant;
        return currentTenant?.IsMultiTenantMode == true ? currentTenant.TenantId : null;
    }

    public async Task<IResult> ConfirmLinkAsync(HttpContext http)
    {
        var t = http.Request.Query["t"].ToString();
        var cancel = http.Request.Query["cancel"].ToString();

        if (string.IsNullOrEmpty(t))
            return Results.BadRequest("Missing token");

        var model = _stateManager.UnprotectConfirm(t);
        if (model is null)
            return Results.BadRequest("Invalid token");

        if (!string.IsNullOrEmpty(cancel))
        {
            var picker = $"/auth/providers/select?client_id={Uri.EscapeDataString(model.ClientId ?? string.Empty)}&ReturnUrl={Uri.EscapeDataString(model.ReturnUrl ?? "/")}&info={Uri.EscapeDataString("Linking canceled. Choose a different provider.")}{(string.IsNullOrEmpty(model.CorrelationId) ? string.Empty : "&cid=" + Uri.EscapeDataString(model.CorrelationId))}";
            return Results.Redirect(picker);
        }

        var extExisting = await _db.ExternalIdentities.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Issuer == model.Issuer && e.Subject == model.Subject);

        if (extExisting is not null)
        {
            // MFA gate: external users with TOTP (or a tenant requiring MFA) must complete the TOTP
            // challenge before receiving the auth cookie.
            var mfaRedirect = await _sessionManager.GetMfaRedirectIfRequiredAsync(
                http, extExisting.UserId, model.Name, model.Email, model.ReturnUrl);
            if (!string.IsNullOrEmpty(mfaRedirect))
                return Results.Redirect(mfaRedirect);

            await _sessionManager.SignInAsync(http, extExisting.UserId, model.Name, model.Email,
                model.Provider, null, Array.Empty<string>(), new Dictionary<string, string>(), null);

            _sessionManager.SetLastProviderCookie(http, model.Provider, model.ClientId);

            return Results.Redirect(AuthorizeReturnUrlHelper.ConsumePromptValues(model.ReturnUrl, "login", "select_account") ?? "/");
        }

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == model.TargetUserId);
        if (user is null)
            return Results.BadRequest("User not found");

        var ext = new ExternalIdentity
        {
            Issuer = model.Issuer!,
            Subject = model.Subject!,
            UserId = user.Id,
            ProviderName = model.Provider,
            ClaimsJson = BuildClaimsJson(model.Email, model.Name),
            CreatedAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow
        };
        _db.ExternalIdentities.Add(ext);
        await _db.SaveChangesAsync();

        // MFA gate: external users with TOTP (or a tenant requiring MFA) must complete the TOTP
        // challenge before receiving the auth cookie.
        var linkMfaRedirect = await _sessionManager.GetMfaRedirectIfRequiredAsync(
            http, user.Id, model.Name, model.Email, model.ReturnUrl);
        if (!string.IsNullOrEmpty(linkMfaRedirect))
            return Results.Redirect(linkMfaRedirect);

        await _sessionManager.SignInAsync(http, user.Id, model.Name, model.Email, model.Provider,
            null, Array.Empty<string>(), new Dictionary<string, string>(), null);

        _sessionManager.SetLastProviderCookie(http, model.Provider, model.ClientId);

        _metricsRecorder.RecordCallbackOutcome(true, DateTime.UtcNow, model.Provider, model.ClientId, "confirm_link_success");

        return Results.Redirect(AuthorizeReturnUrlHelper.ConsumePromptValues(model.ReturnUrl, "login", "select_account") ?? "/");
    }

    private static string? BuildClaimsJson(string? email, string? name)
    {
        if (email is null && name is null)
            return null;
        return System.Text.Json.JsonSerializer.Serialize(new { email, name });
    }
}
