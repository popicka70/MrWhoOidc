using Microsoft.AspNetCore.Authorization;
using MrWhoOidc.WebAuth.Security.Admin;

namespace MrWhoOidc.UnitTests.TestDoubles;

/// <summary>
/// For host tests that wave the tenant-admin policy through: the per-endpoint operation marker is enforced as its own
/// requirement, so it has to be satisfied too.
/// </summary>
public sealed class SatisfyTenantAdminOperationHandler : AuthorizationHandler<TenantAdminOperationRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, TenantAdminOperationRequirement requirement)
    {
        context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
