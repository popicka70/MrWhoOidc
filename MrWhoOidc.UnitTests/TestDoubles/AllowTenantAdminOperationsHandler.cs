using Microsoft.AspNetCore.Authorization;
using MrWhoOidc.WebAuth.Security.Admin;

namespace MrWhoOidc.UnitTests.TestDoubles;

/// <summary>
/// For integration tests that replace the "tenant-admin" policy with an always-true assertion: endpoint operation
/// markers (TenantAdminOperationRequirement) are enforced on top of the policy, so such tests pass them too.
/// </summary>
public sealed class AllowTenantAdminOperationsHandler : AuthorizationHandler<TenantAdminOperationRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, TenantAdminOperationRequirement requirement)
    {
        context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
