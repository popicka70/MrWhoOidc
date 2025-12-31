using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services.Authorization;

public sealed class ProviderSelectionService(AuthDbContext db, IClientStore clients) : IProviderSelectionService
{
    public async Task<ProviderSelectionResult> EvaluateAsync(
        string clientId, 
        string? idpParam = null, 
        string? idpHint = null, 
        string? lastUsedIdp = null,
        bool forceAccountSelection = false,
        CancellationToken ct = default)
    {
        var client = await clients.FindByClientIdAsync(clientId, ct).ConfigureAwait(false);
        if (client == null)
        {
            return new ProviderSelectionResult(false);
        }

        bool allowLocal = client.AllowLocalLogin;
        bool allowExternal = client.AllowExternalIdp;
        bool allowQr = client.AllowQrLogin;

        // If explicit idp is provided and allowed, auto-redirect to it
        if (!string.IsNullOrEmpty(idpParam) && allowExternal)
        {
            return new ProviderSelectionResult(false, AutoRedirectProvider: idpParam, AllowLocal: allowLocal, AllowExternal: allowExternal, AllowQr: allowQr);
        }

        // Load available providers for this client
        var providerOptions = new List<ProviderOption>();
        if (allowExternal)
        {
            providerOptions = await db.ClientIdentityProviders.AsNoTracking()
                .Where(m => m.ClientId == client.Id && m.Enabled)
                .Join(db.IdentityProviders.AsNoTracking().Where(p => p.Enabled), m => m.IdentityProviderId, p => p.Id, (m, p) => new {
                    Name = p.Name,
                    DisplayName = p.DisplayName ?? p.Name,
                    IsDefault = m.IsDefaultForClient,
                    AutoRedirect = m.AutoRedirectIfSingle
                })
                .OrderBy(x => x.Name)
                .Select(x => new ProviderOption(x.Name, x.DisplayName, x.IsDefault, x.AutoRedirect))
                .ToListAsync(ct)
                .ConfigureAwait(false);
        }

        // If idp_hint matches an available provider and account selection not forced, use it
        if (!string.IsNullOrEmpty(idpHint) && !forceAccountSelection && !allowLocal && providerOptions.Any(pl => string.Equals(pl.Name, idpHint, StringComparison.Ordinal)))
        {
            return new ProviderSelectionResult(false, AutoRedirectProvider: idpHint, AllowLocal: allowLocal, AllowExternal: allowExternal, AllowQr: allowQr);
        }

        // If single provider and local not allowed, auto-redirect
        if (providerOptions.Count == 1 && providerOptions[0].AutoRedirectIfSingle && !allowLocal && !allowQr && !forceAccountSelection)
        {
            return new ProviderSelectionResult(false, AutoRedirectProvider: providerOptions[0].Name, AllowLocal: allowLocal, AllowExternal: allowExternal, AllowQr: allowQr);
        }

        // If multiple providers, look for last-used cookie and prefer it when not forcing account selection
        if (!string.IsNullOrEmpty(lastUsedIdp) && providerOptions.Any(pl => string.Equals(pl.Name, lastUsedIdp, StringComparison.Ordinal)) && !forceAccountSelection && !allowLocal && !allowQr)
        {
            return new ProviderSelectionResult(false, AutoRedirectProvider: lastUsedIdp, AllowLocal: allowLocal, AllowExternal: allowExternal, AllowQr: allowQr);
        }

        // Decide whether to show provider picker: if we have external providers OR QR is enabled
        bool shouldShowPicker = providerOptions.Count > 0 || allowQr;

        return new ProviderSelectionResult(
            RequiresSelection: shouldShowPicker,
            AvailableProviders: providerOptions,
            AllowLocal: allowLocal,
            AllowExternal: allowExternal,
            AllowQr: allowQr
        );
    }
}
