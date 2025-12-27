using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MrWhoOidc.Auth.Services.Authorization;

public sealed class ConsentProcessor(IConsentService consents, IClientStore clients) : IConsentProcessor
{
    public async Task<ConsentDecision> EvaluateAsync(Guid userId, string clientId, string[] requestedScopes, CancellationToken ct = default)
    {
        var client = await clients.FindByClientIdAsync(clientId, ct).ConfigureAwait(false);
        if (client == null)
        {
            return new ConsentDecision(false, false);
        }

        if (!client.RequireConsent)
        {
            return new ConsentDecision(false, true);
        }

        var hasConsent = await consents.HasConsentAsync(userId, clientId, requestedScopes, ct).ConfigureAwait(false);
        if (hasConsent)
        {
            return new ConsentDecision(true, true);
        }

        return new ConsentDecision(true, false, requestedScopes);
    }
}
