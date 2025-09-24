using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.IdentityProviders;
using MrWhoOidc.Auth.Persistence;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using MrWhoOidc.Auth.Services;
using System.Security.Claims;
using System.Net.Http.Headers;
using System.Globalization;

namespace MrWhoOidc.WebAuth.Handlers;

public interface IExternalOidcHandler
{
    Task<IResult> StartAsync(HttpContext http);
    Task<IResult> CallbackAsync(HttpContext http);
    Task<IResult> ConfirmLinkAsync(HttpContext http);
}

public sealed class ExternalOidcHandler(AuthDbContext db, IHttpClientFactory httpFactory, IDataProtectionProvider dp, IJwksCache jwksCache, IClaimMappingService mapper) : IExternalOidcHandler
{
    private readonly IDataProtector _protector = dp.CreateProtector("ext-oidc-state");
    private readonly IDataProtector _confirmProtector = dp.CreateProtector("ext-oidc-confirm");

    public async Task<IResult> StartAsync(HttpContext http)
    {
        var providerName = http.Request.Query["provider"].ToString();
        var returnUrl = http.Request.Query["returnUrl"].ToString(); // original /authorize URL
        var clientId = http.Request.Query["clientId"].ToString(); // for last-used cookie
        if (string.IsNullOrEmpty(providerName) || string.IsNullOrEmpty(returnUrl))
            return Results.BadRequest("provider and returnUrl are required");

        var provider = await db.IdentityProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Name == providerName && p.Enabled);
        if (provider is null || string.IsNullOrWhiteSpace(provider.ConfigJson))
            return Results.BadRequest("Unknown provider");
        if (!OidcProviderConfig.TryParse(provider.ConfigJson!, out var cfg).ok || cfg is null)
            return Results.BadRequest("Invalid provider configuration");

        // Discovery
        var httpc = httpFactory.CreateClient();
        var discoUrl = string.IsNullOrWhiteSpace(cfg.DiscoveryUrl) ? cfg.Authority.TrimEnd('/') + "/.well-known/openid-configuration" : cfg.DiscoveryUrl!;
        using var resp = await httpc.GetAsync(discoUrl, http.RequestAborted);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(http.RequestAborted));
        var root = doc.RootElement;
        var authz = root.GetProperty("authorization_endpoint").GetString()!;
        var parEndpoint = root.TryGetProperty("pushed_authorization_request_endpoint", out var parel) ? parel.GetString() : null;
        var issuerFromDiscovery = root.TryGetProperty("issuer", out var issuerEl) ? issuerEl.GetString() : null;

        // PKCE
        var verifier = Base64Url(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")));
        var challenge = Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));

        var cb = $"{http.Request.Scheme}://{http.Request.Host}/Auth/External/Callback";

        // State (include clientId to set cookie on callback)
        var nonce = Guid.NewGuid().ToString("N");
        var corr = Guid.NewGuid().ToString("N");
        var statePayload = JsonSerializer.Serialize(new StateModel { Provider = providerName, CodeVerifier = verifier, ReturnUrl = returnUrl, Nonce = nonce, ClientId = clientId, CorrelationId = corr });
        var state = Base64Url(_protector.Protect(Encoding.UTF8.GetBytes(statePayload)));

        // Use provider-configured response_type; default to "code"
        var responseType = string.IsNullOrWhiteSpace(cfg.ResponseType) ? "code" : cfg.ResponseType.Trim();

        var query = new Dictionary<string, string?>
        {
            ["response_type"] = responseType,
            ["client_id"] = cfg.ClientId,
            ["redirect_uri"] = cb,
            ["scope"] = string.Join(' ', cfg.Scopes ?? new[] { "openid", "profile", "email" }),
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256"
        };

        // Optional: response_mode and extra params from config
        if (!string.IsNullOrWhiteSpace(cfg.ResponseMode))
        {
            query["response_mode"] = cfg.ResponseMode;
        }
        if (cfg.ExtraAuthParams is { Count: > 0 })
        {
            foreach (var kvp in cfg.ExtraAuthParams)
            {
                if (!string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value))
                {
                    query[kvp.Key] = kvp.Value;
                }
            }
        }

        // Copy hints from returnUrl
        try
        {
            var ru = new Uri(returnUrl, UriKind.RelativeOrAbsolute);
            var qs = System.Web.HttpUtility.ParseQueryString(ru.IsAbsoluteUri ? ru.Query : new Uri("http://local" + returnUrl).Query);
            void TryCopy(string name)
            {
                var val = qs[name];
                if (!string.IsNullOrEmpty(val)) query[name] = val;
            }
            TryCopy("login_hint");
            TryCopy("ui_locales");
            TryCopy("prompt");
            TryCopy("max_age");
            TryCopy("acr_values");
            // Pass-through of resource/audience if present
            TryCopy("resource");
            TryCopy("audience");
        }
        catch { }

        // Build outbound JAR (request object) if configured and signing key available
        string? requestJwt = null;
        if (cfg.UseJAR)
        {
            var key = await db.IdentityProviderKeys.AsNoTracking()
                .Where(k => k.IdentityProviderId == provider.Id && k.Purpose == IdentityProviderKeyPurpose.Signing && k.Active)
                .OrderByDescending(k => k.CreatedAt)
                .FirstOrDefaultAsync(http.RequestAborted);
            if (key is not null)
            {
                try
                {
                    var jsonWebKey = new JsonWebKey(key.Jwk);
                    if (!string.IsNullOrEmpty(key.Kid) && string.IsNullOrEmpty(jsonWebKey.KeyId))
                    {
                        jsonWebKey.KeyId = key.Kid;
                    }
                    var alg = MapAlg(key.Alg);
                    var creds = new SigningCredentials(jsonWebKey, alg);

                    // Per RFC 9101: iss=client_id, aud=AS issuer/authorize endpoint
                    var aud = ((issuerFromDiscovery ?? cfg.Authority).TrimEnd('/')) + "/authorize";

                    var now = DateTimeOffset.UtcNow;
                    var claims = new Dictionary<string, object?>
                    {
                        ["response_type"] = query.GetValueOrDefault("response_type"),
                        ["client_id"] = cfg.ClientId,
                        ["redirect_uri"] = query.GetValueOrDefault("redirect_uri"),
                        ["scope"] = query.GetValueOrDefault("scope"),
                        ["state"] = state,
                        ["nonce"] = nonce,
                        ["code_challenge"] = query.GetValueOrDefault("code_challenge"),
                        ["code_challenge_method"] = query.GetValueOrDefault("code_challenge_method"),
                        ["response_mode"] = query.GetValueOrDefault("response_mode"),
                        ["login_hint"] = query.GetValueOrDefault("login_hint"),
                        ["ui_locales"] = query.GetValueOrDefault("ui_locales"),
                        ["prompt"] = query.GetValueOrDefault("prompt"),
                        ["max_age"] = query.GetValueOrDefault("max_age"),
                        ["acr_values"] = query.GetValueOrDefault("acr_values"),
                        ["resource"] = query.GetValueOrDefault("resource")
                    };

                    // Remove nulls
                    var clean = claims.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value!);

                    var descriptor = new SecurityTokenDescriptor
                    {
                        Issuer = cfg.ClientId,
                        Audience = aud,
                        Claims = clean,
                        NotBefore = now.UtcDateTime.AddMinutes(-1),
                        Expires = now.AddMinutes(5).UtcDateTime,
                        SigningCredentials = creds
                    };

                    var handler = new JwtSecurityTokenHandler();
                    var token = handler.CreateToken(descriptor);
                    requestJwt = handler.WriteToken(token);
                }
                catch
                {
                    // If signing fails, fall back to non-JAR path
                    requestJwt = null;
                }
            }
        }

        // If provider supports PAR, push and then redirect using request_uri.
        if (cfg.UsePAR && !string.IsNullOrEmpty(parEndpoint))
        {
            try
            {
                Dictionary<string, string> body;
                if (!string.IsNullOrEmpty(requestJwt))
                {
                    body = new Dictionary<string, string>
                    {
                        ["client_id"] = cfg.ClientId,
                        ["request"] = requestJwt
                    };
                }
                else
                {
                    body = query.Where(kv => !string.IsNullOrEmpty(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value!);
                }

                using var parReq = new HttpRequestMessage(HttpMethod.Post, parEndpoint)
                {
                    Content = new FormUrlEncodedContent(body)
                };

                // Client authentication at PAR: prefer client_secret_basic when secret is available
                if (!string.IsNullOrEmpty(cfg.ClientSecret))
                {
                    var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{cfg.ClientId}:{cfg.ClientSecret}"));
                    parReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
                }

                using var parResp = await httpc.SendAsync(parReq, http.RequestAborted);
                if (parResp.IsSuccessStatusCode)
                {
                    var parBody = await parResp.Content.ReadAsStringAsync(http.RequestAborted);
                    using var parDoc = JsonDocument.Parse(parBody);
                    var requestUri = parDoc.RootElement.TryGetProperty("request_uri", out var ruriEl) ? ruriEl.GetString() : null;
                    if (!string.IsNullOrEmpty(requestUri))
                    {
                        var ubPar = new UriBuilder(authz);
                        var qPar = System.Web.HttpUtility.ParseQueryString(ubPar.Query);
                        qPar["client_id"] = cfg.ClientId;
                        qPar["request_uri"] = requestUri;
                        ubPar.Query = qPar.ToString();
                        return Results.Redirect(ubPar.ToString());
                    }
                }
            }
            catch
            {
                // Fallback to redirect path below
            }
        }

        // Fallback: standard redirect
        var ub = new UriBuilder(authz);
        var q = System.Web.HttpUtility.ParseQueryString(ub.Query);
        if (!string.IsNullOrEmpty(requestJwt))
        {
            q["client_id"] = cfg.ClientId;
            q["request"] = requestJwt;
        }
        else
        {
            foreach (var kv in query) if (!string.IsNullOrEmpty(kv.Value)) q[kv.Key] = kv.Value;
        }
        ub.Query = q.ToString();
        return Results.Redirect(ub.ToString());
    }

    public async Task<IResult> CallbackAsync(HttpContext http)
    {
        var idTokenFromAuth = http.Request.Query["id_token"].ToString();
        var stateRaw = http.Request.Query["state"].ToString();
        var error = http.Request.Query["error"].ToString();
        var errorDescription = http.Request.Query["error_description"].ToString();
        if (string.IsNullOrEmpty(stateRaw)) return Results.BadRequest("Missing state");

        StateModel state;
        try
        {
            var bytes = _protector.Unprotect(Base64UrlDecode(stateRaw));
            state = JsonSerializer.Deserialize<StateModel>(bytes)!;
        }
        catch { return Results.BadRequest("Invalid state"); }

        // Upstream error/cancel handling -> friendly page with correlation id and reselect link
        if (!string.IsNullOrEmpty(error))
        {
            return FriendlyError(state.ReturnUrl, state.ClientId, state.CorrelationId, $"Upstream error: {error}{(string.IsNullOrEmpty(errorDescription) ? string.Empty : " - " + errorDescription)}");
        }

        var code = http.Request.Query["code"].ToString();
        if (string.IsNullOrEmpty(code))
        {
            return FriendlyError(state.ReturnUrl, state.ClientId, state.CorrelationId, "Missing authorization code from upstream IdP.");
        }

        var provider = await db.IdentityProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Name == state.Provider && p.Enabled);
        if (provider is null || string.IsNullOrWhiteSpace(provider.ConfigJson))
            return FriendlyError(state.ReturnUrl, state.ClientId, state.CorrelationId, "Unknown or disabled provider.");
        if (!OidcProviderConfig.TryParse(provider.ConfigJson!, out var cfg).ok || cfg is null)
            return FriendlyError(state.ReturnUrl, state.ClientId, state.CorrelationId, "Invalid provider configuration.");

        // Discovery
        var httpc = httpFactory.CreateClient();
        var discoUrl = string.IsNullOrWhiteSpace(cfg.DiscoveryUrl) ? cfg.Authority.TrimEnd('/') + "/.well-known/openid-configuration" : cfg.DiscoveryUrl!;
        using var resp = await httpc.GetAsync(discoUrl, http.RequestAborted);
        if (!resp.IsSuccessStatusCode)
        {
            return FriendlyError(state.ReturnUrl, state.ClientId, state.CorrelationId, $"Discovery failed: {(int)resp.StatusCode}");
        }
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(http.RequestAborted));
        var root = doc.RootElement;
        var tokenEndpoint = root.GetProperty("token_endpoint").GetString()!;
        var userinfoEndpoint = root.TryGetProperty("userinfo_endpoint", out var ue) ? ue.GetString() : null;
        var jwksUri = root.GetProperty("jwks_uri").GetString()!;
        var issuerFromDiscovery = root.GetProperty("issuer").GetString();

        // Token exchange
        var form = new Dictionary<string, string?>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = $"{http.Request.Scheme}://{http.Request.Host}/Auth/External/Callback",
            ["client_id"] = cfg.ClientId,
            ["code_verifier"] = state.CodeVerifier
        };
        if (!string.IsNullOrEmpty(cfg.ClientSecret)) form["client_secret"] = cfg.ClientSecret;

        using var msg = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint) { Content = new FormUrlEncodedContent(form) };
        using var tokResp = await httpc.SendAsync(msg, http.RequestAborted);
        var body = await tokResp.Content.ReadAsStringAsync(http.RequestAborted);
        if (!tokResp.IsSuccessStatusCode)
        {
            return FriendlyError(state.ReturnUrl, state.ClientId, state.CorrelationId, $"Token exchange failed: {(int)tokResp.StatusCode} {body}");
        }

        using var tokDoc = JsonDocument.Parse(body);
        var idToken = !string.IsNullOrEmpty(idTokenFromAuth) ? idTokenFromAuth : tokDoc.RootElement.TryGetProperty("id_token", out var idt) ? idt.GetString() : null;

        string? email = null, name = null, sub = null, issuer = null, nonce = state.Nonce;
        string? acr = null; string[] amrs = Array.Empty<string>();

        try
        {
            if (!string.IsNullOrEmpty(idToken))
            {
                // Validate ID token
                var set = await jwksCache.GetAsync(jwksUri, TimeSpan.FromMinutes(15), httpFactory, http.RequestAborted);
                if (set is null) return FriendlyError(state.ReturnUrl, state.ClientId, state.CorrelationId, "JWKS fetch failed");

                var tokenHandler = new JwtSecurityTokenHandler();
                tokenHandler.InboundClaimTypeMap.Clear();

                var parms = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = issuerFromDiscovery,
                    ValidateAudience = true,
                    ValidAudience = cfg.ClientId,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromMinutes(2),
                    RequireSignedTokens = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKeys = set.Keys,
                    NameClaimType = "name",
                    RoleClaimType = "roles"
                };

                var principal = tokenHandler.ValidateToken(idToken!, parms, out var _);
                issuer = principal.FindFirst("iss")?.Value ?? parms.ValidIssuer;
                sub = principal.FindFirst("sub")?.Value ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                name = principal.FindFirst("name")?.Value ?? principal.FindFirst(ClaimTypes.Name)?.Value;
                email = principal.FindFirst("email")?.Value ?? principal.FindFirst(ClaimTypes.Email)?.Value;
                acr = principal.FindFirst("acr")?.Value;
                amrs = principal.Claims.Where(c => c.Type == "amr").Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.Ordinal).ToArray();
                var nonceClaim = principal.FindFirst("nonce")?.Value;
                if (!string.IsNullOrEmpty(nonce) && !string.Equals(nonce, nonceClaim, StringComparison.Ordinal))
                    return FriendlyError(state.ReturnUrl, state.ClientId, state.CorrelationId, "Nonce mismatch");
            }
        }
        catch (Exception ex)
        {
            return FriendlyError(state.ReturnUrl, state.ClientId, state.CorrelationId, "ID token validation failed: " + ex.Message);
        }

        // Fallbacks: when id_token missing or lacks claims
        if (userinfoEndpoint is not null && (string.IsNullOrEmpty(sub) || string.IsNullOrEmpty(email) || string.IsNullOrEmpty(name)))
        {
            try
            {
                var at = tokDoc.RootElement.TryGetProperty("access_token", out var atEl) ? atEl.GetString() : null;
                if (!string.IsNullOrEmpty(at))
                {
                    var uiReq = new HttpRequestMessage(HttpMethod.Get, userinfoEndpoint);
                    uiReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", at);
                    using var uiResp = await httpc.SendAsync(uiReq, http.RequestAborted);
                    var uiBody = await uiResp.Content.ReadAsStringAsync(http.RequestAborted);
                    if (uiResp.IsSuccessStatusCode)
                    {
                        using var uiDoc = JsonDocument.Parse(uiBody);
                        var rootUi = uiDoc.RootElement;
                        sub ??= TryGetAny(rootUi, "sub", "subject", "id", "user_id", "uid", "oid", "sid");
                        email ??= TryGetAny(rootUi, "email", "mail", "upn");
                        name ??= TryGetAny(rootUi, "name", "given_name", "preferred_username", "displayName");
                        issuer ??= issuerFromDiscovery; // set issuer from discovery if no id_token
                    }
                }
            }
            catch { /* ignore, will fail later if still missing */ }
        }

        // Last-resort: extract sub from access token if it's a JWT
        if (string.IsNullOrEmpty(sub))
        {
            var at = tokDoc.RootElement.TryGetProperty("access_token", out var atEl2) ? atEl2.GetString() : null;
            if (!string.IsNullOrEmpty(at) && at.Count(c => c == '.') == 2)
            {
                try
                {
                    var handler = new JwtSecurityTokenHandler();
                    var jwtAt = handler.ReadJwtToken(at);
                    sub = jwtAt.Claims.FirstOrDefault(c => c.Type == "sub")?.Value
                        ?? jwtAt.Claims.FirstOrDefault(c => c.Type == "oid")?.Value
                        ?? jwtAt.Claims.FirstOrDefault(c => c.Type == "uid")?.Value
                        ?? jwtAt.Claims.FirstOrDefault(c => c.Type == "id")?.Value
                        ?? jwtAt.Claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value;
                }
                catch { }
            }
        }

        issuer ??= issuerFromDiscovery; // last resort: issuer from discovery

        if (string.IsNullOrEmpty(sub) || string.IsNullOrEmpty(issuer))
            return FriendlyError(state.ReturnUrl, state.ClientId, state.CorrelationId, "Missing subject/issuer from upstream IdP");

        // Determine client policy from the original returnUrl (client_id)
        var clientPublicId = TryGetClientIdFromReturnUrl(state.ReturnUrl);
        var clientEntity = await (string.IsNullOrWhiteSpace(clientPublicId)
            ? Task.FromResult<Client?>(null)
            : db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == clientPublicId, http.RequestAborted));
        var allowAutoProvision = clientEntity?.AllowExternalAutoProvision ?? true; // back-compat default: true
        var allowEmailLinking = clientEntity?.AllowExternalEmailLinking ?? true;  // default: true
        var requireEmailConfirm = clientEntity?.RequireEmailLinkConfirmation ?? true; // default: true

        // Link/provision local user using ExternalIdentities
        var ext = await db.ExternalIdentities.FirstOrDefaultAsync(x => x.Issuer == issuer && x.Subject == sub, http.RequestAborted);
        Guid userId;
        if (ext is null)
        {
            // Map claims using provider-specific mappings
            var sourceClaims = new Dictionary<string, string?>
            {
                ["sub"] = sub,
                ["iss"] = issuer,
                ["email"] = email,
                ["name"] = name
            };
            var mapped = await mapper.ApplyAsync(provider.Id, sourceClaims!, http.RequestAborted);
            var userEmail = mapped.TryGetValue("email", out var me) ? me : email;
            var userName = mapped.TryGetValue("name", out var mn) ? mn : name;

            // Try email-based linking when allowed
            if (allowEmailLinking && !string.IsNullOrWhiteSpace(userEmail))
            {
                var existingUser = await FindUserByEmailAsync(userEmail!, http.RequestAborted);
                if (existingUser is not null)
                {
                    if (requireEmailConfirm)
                    {
                        // Render confirmation page
                        var token = ProtectConfirm(new ConfirmModel
                        {
                            Provider = state.Provider,
                            Issuer = issuer!,
                            Subject = sub!,
                            TargetUserId = existingUser.Id,
                            ReturnUrl = state.ReturnUrl,
                            ClientId = state.ClientId,
                            CorrelationId = state.CorrelationId,
                            Email = userEmail,
                            Name = userName
                        });
                        return RenderConfirmPage(token, state.ReturnUrl, state.ClientId, state.CorrelationId, userEmail!, existingUser.Name ?? existingUser.Username);
                    }
                    else
                    {
                        // Link immediately
                        userId = existingUser.Id;
                        var newExt = new ExternalIdentity { Issuer = issuer!, Subject = sub!, UserId = userId, ProviderName = state.Provider, ClaimsJson = BuildClaimsJson(userEmail, userName), CreatedAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow };
                        db.ExternalIdentities.Add(newExt);
                        await db.SaveChangesAsync(http.RequestAborted);
                        return await SignInAndRedirectAsync(http, userId, state, name, email, sub, state.Provider, acr, amrs);
                    }
                }
            }

            // Auto-provision new user when allowed
            if (allowAutoProvision)
            {
                var username = !string.IsNullOrEmpty(userEmail) ? userEmail : $"{state.Provider}:{sub}";
                var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username || (!string.IsNullOrEmpty(userEmail) && u.Email == userEmail), http.RequestAborted);
                if (user is null)
                {
                    user = new User { Username = username, Email = userEmail, Name = userName ?? username, PasswordHash = string.Empty, HashAlgorithm = "external" };
                    db.Users.Add(user);
                    await db.SaveChangesAsync(http.RequestAborted);
                }
                userId = user.Id;

                ext = new ExternalIdentity { Issuer = issuer!, Subject = sub!, UserId = userId, ProviderName = state.Provider, ClaimsJson = BuildClaimsJson(userEmail, userName), CreatedAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow };
                db.ExternalIdentities.Add(ext);
                await db.SaveChangesAsync(http.RequestAborted);

                return await SignInAndRedirectAsync(http, userId, state, name, email, sub, state.Provider, acr, amrs);
            }

            // Neither linking nor auto-provision allowed
            return FriendlyError(state.ReturnUrl, state.ClientId, state.CorrelationId, "External sign-in is not allowed by client policy.");
        }
        else
        {
            userId = ext.UserId;
            ext.LastSeenAt = DateTimeOffset.UtcNow;
            ext.ClaimsJson = BuildClaimsJson(email, name);
            await db.SaveChangesAsync(http.RequestAborted);

            return await SignInAndRedirectAsync(http, userId, state, name, email, sub, state.Provider, acr, amrs);
        }
    }

    public async Task<IResult> ConfirmLinkAsync(HttpContext http)
    {
        var t = http.Request.Query["t"].ToString();
        var cancel = http.Request.Query["cancel"].ToString();
        if (!string.IsNullOrEmpty(cancel))
        {
            // Send back to provider picker if available
            return FriendlyError(null, null, null, "Linking canceled by user.");
        }
        if (string.IsNullOrEmpty(t)) return Results.BadRequest("Missing token");
        ConfirmModel model;
        try
        {
            var data = _confirmProtector.Unprotect(Base64UrlDecode(t));
            model = JsonSerializer.Deserialize<ConfirmModel>(data)!;
        }
        catch
        {
            return Results.BadRequest("Invalid token");
        }

        // Ensure not already linked
        var extExisting = await db.ExternalIdentities.AsNoTracking().FirstOrDefaultAsync(e => e.Issuer == model.Issuer && e.Subject == model.Subject);
        if (extExisting is not null)
        {
            // Already linked, sign in
            return await SignInAndRedirectAsync(http, extExisting.UserId, new StateModel { ReturnUrl = model.ReturnUrl, ClientId = model.ClientId, Provider = model.Provider }, model.Name, model.Email, model.Subject, model.Provider, null, Array.Empty<string>());
        }

        // Create linkage
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == model.TargetUserId);
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
        db.ExternalIdentities.Add(ext);
        await db.SaveChangesAsync();

        return await SignInAndRedirectAsync(http, user.Id, new StateModel { ReturnUrl = model.ReturnUrl, ClientId = model.ClientId, Provider = model.Provider }, model.Name, model.Email, model.Subject, model.Provider, null, Array.Empty<string>());
    }

    private async Task<IResult> SignInAndRedirectAsync(HttpContext http, Guid userId, StateModel state, string? name, string? email, string? sub, string? idp, string? acr, string[] amrs)
    {
        // Issue local cookie
        var claims = new List<System.Security.Claims.Claim>
        {
            new(System.Security.Claims.ClaimTypes.NameIdentifier, userId.ToString()),
            new(System.Security.Claims.ClaimTypes.Name, name ?? email ?? $"{state.Provider}:{sub}" ) ,
            new("auth_time", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString())
        };
        if (!string.IsNullOrEmpty(idp)) claims.Add(new("idp", idp));
        if (!string.IsNullOrEmpty(acr)) claims.Add(new("acr", acr));
        if (amrs is { Length: > 0 }) foreach (var v in amrs) claims.Add(new("amr", v));

        var id = new System.Security.Claims.ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal2 = new System.Security.Claims.ClaimsPrincipal(id);
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal2);

        // Remember last provider for this client if present in state
        if (!string.IsNullOrEmpty(state.ClientId))
        {
            var cookieName = ".mrwhooidc.lastidp." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(state.ClientId))).Substring(0, 16);
            http.Response.Cookies.Append(cookieName, state.Provider ?? string.Empty, new Microsoft.AspNetCore.Http.CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddDays(90),
                SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax,
                Secure = true,
                HttpOnly = true,
                IsEssential = true,
                Path = "/"
            });
        }

        var redirect = state.ReturnUrl ?? "/";
        return Results.Redirect(redirect);
    }

    private static string? TryGetClientIdFromReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl)) return null;
        try
        {
            var ru = new Uri(returnUrl, UriKind.RelativeOrAbsolute);
            var qs = System.Web.HttpUtility.ParseQueryString(ru.IsAbsoluteUri ? ru.Query : new Uri("http://local" + returnUrl).Query);
            return qs["client_id"];
        }
        catch { return null; }
    }

    private async Task<User?> FindUserByEmailAsync(string email, CancellationToken ct)
    {
        email = email.Trim();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        if (user is not null) return user;
        // Alternative emails
        var alt = await db.UserAlternativeEmails.AsNoTracking().FirstOrDefaultAsync(a => a.Email == email && a.IsVerified, ct);
        if (alt is not null)
        {
            return await db.Users.FirstOrDefaultAsync(u => u.Id == alt.UserId, ct);
        }
        return null;
    }

    private static string MapAlg(string alg)
        => alg switch
        {
            "RS256" => SecurityAlgorithms.RsaSha256,
            "PS256" => SecurityAlgorithms.RsaSsaPssSha256,
            "ES256" => SecurityAlgorithms.EcdsaSha256,
            "ES384" => SecurityAlgorithms.EcdsaSha384,
            "ES512" => SecurityAlgorithms.EcdsaSha512,
            _ => SecurityAlgorithms.RsaSha256
        };

    private static IResult FriendlyError(string? returnUrl, string? clientId, string? correlationId, string message)
    {
        var corr = correlationId ?? Guid.NewGuid().ToString("N");
        var builder = new StringBuilder();
        builder.Append("<html><head><title>Sign-in error</title>");
        builder.Append("<link rel=\"stylesheet\" href=\"/lib/bootstrap/dist/css/bootstrap.min.css\" />");
        builder.Append("</head><body class=\"container py-4\">");
        builder.Append("<div class=\"alert alert-danger\"><strong>Sign-in failed.</strong> ");
        builder.Append(System.Web.HttpUtility.HtmlEncode(message));
        builder.Append("</div>");
        builder.Append("<div class=\"small text-muted\">Correlation ID: <code>");
        builder.Append(System.Web.HttpUtility.HtmlEncode(corr));
        builder.Append("</code></div>");
        if (!string.IsNullOrEmpty(returnUrl) && !string.IsNullOrEmpty(clientId))
        {
            var sep = returnUrl.Contains("?", StringComparison.Ordinal) ? "&" : "?";
            var picker = $"/Auth/Providers/Select?client_id={Uri.EscapeDataString(clientId)}&ReturnUrl={Uri.EscapeDataString(returnUrl)}";
            builder.Append("<div class=\"mt-3\"><a class=\"btn btn-outline-primary\" href=\"");
            builder.Append(picker);
            builder.Append("\">Choose a different provider</a></div>");
        }
        builder.Append("</body></html>");
        return Results.Content(builder.ToString(), "text/html; charset=utf-8", statusCode: 400);
    }

    private IResult RenderConfirmPage(string token, string? returnUrl, string? clientId, string? correlationId, string email, string targetUserDisplay)
    {
        var builder = new StringBuilder();
        builder.Append("<html><head><title>Confirm account linking</title>");
        builder.Append("<link rel=\"stylesheet\" href=\"/lib/bootstrap/dist/css/bootstrap.min.css\" />");
        builder.Append("</head><body class=\"container py-4\">");
        builder.Append("<div class=\"alert alert-info\"><strong>Confirm account linking</strong></div>");
        builder.Append("<p>We found an existing account for <code>");
        builder.Append(System.Web.HttpUtility.HtmlEncode(email));
        builder.Append("</code> (\");");
        builder.Append(System.Web.HttpUtility.HtmlEncode(targetUserDisplay));
        builder.Append("). Do you want to link this external identity to your existing account?</p>");
        builder.Append("<div class=\"mt-3\">");
        builder.Append($"<a class=\"btn btn-primary me-2\" href=\"/Auth/External/Confirm?t={Uri.EscapeDataString(token)}\">Yes, link and continue</a>");
        builder.Append($"<a class=\"btn btn-secondary\" href=\"/Auth/External/Confirm?t={Uri.EscapeDataString(token)}&cancel=1\">Cancel</a>");
        builder.Append("</div>");
        builder.Append("</body></html>");
        return Results.Content(builder.ToString(), "text/html; charset=utf-8");
    }

    private string ProtectConfirm(ConfirmModel model)
    {
        var json = JsonSerializer.Serialize(model);
        return Base64Url(_confirmProtector.Protect(Encoding.UTF8.GetBytes(json)));
    }

    private static string? TryGetAny(JsonElement root, params string[] names)
    {
        foreach (var n in names)
        {
            if (root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
        }
        return null;
    }

    private static string? BuildClaimsJson(string? email, string? name)
    {
        if (email is null && name is null) return null;
        return JsonSerializer.Serialize(new { email, name });
    }

    private static string Base64Url(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }

    private static string Base64UrlDecodeToString(string s)
        => Encoding.UTF8.GetString(Base64UrlDecode(s));

    private sealed class StateModel
    {
        public string Provider { get; set; } = string.Empty;
        public string CodeVerifier { get; set; } = string.Empty;
        public string? ReturnUrl { get; set; }
        public string? Nonce { get; set; }
        public string? ClientId { get; set; }
        public string? CorrelationId { get; set; }
    }

    private sealed class ConfirmModel
    {
        public string Provider { get; set; } = string.Empty;
        public string? Issuer { get; set; }
        public string? Subject { get; set; }
        public Guid TargetUserId { get; set; }
        public string? ReturnUrl { get; set; }
        public string? ClientId { get; set; }
        public string? CorrelationId { get; set; }
        public string? Email { get; set; }
        public string? Name { get; set; }
    }
}
