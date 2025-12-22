using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.UnitTests.Helpers;

internal sealed class NoopTenantsClaimService : ITenantsClaimService
{
    public Task<string> BuildTenantsClaimJsonAsync(Guid userId, CancellationToken ct = default)
        => Task.FromResult("[]");
}
