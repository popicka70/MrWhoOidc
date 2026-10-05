using Microsoft.AspNetCore.Authorization;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Security.Admin;

/// <summary>
/// Defines the kinds of tenant admin operations that can be authorized.
/// </summary>
public enum TenantAdminOperationKind
{
    /// <summary>
    /// Read-only operations (e.g., listing, querying).
    /// </summary>
    Read,

    /// <summary>
    /// Write operations (e.g., creating, updating, deleting).
    /// </summary>
    Write,

    /// <summary>
    /// Highly sensitive write operations (e.g., secret management, role changes).
    /// </summary>
    SecuritySensitiveWrite,

    /// <summary>
    /// Read operations that disclose credentials or bulk configuration (secret inventories, exports). Not a write,
    /// but read-only support sessions are denied it: support access is for troubleshooting, not for lifting a
    /// tenant's credential material.
    /// </summary>
    SecuritySensitiveRead
}

/// <summary>
/// Endpoint-builder helper for attaching a <see cref="TenantAdminOperationRequirement"/> marker. Only use it on
/// endpoints guarded by the tenant-admin policy: the marker is evaluated as a tenant-admin requirement.
/// </summary>
public static class TenantAdminOperationEndpointExtensions
{
    public static TBuilder WithTenantAdminOperation<TBuilder>(this TBuilder builder, TenantAdminOperationKind kind)
        where TBuilder : IEndpointConventionBuilder
        => builder.WithMetadata(new TenantAdminOperationRequirement { Kind = kind });
}

/// <summary>
/// Authorization requirement for a specific tenant admin operation kind.
/// Attached per-endpoint to enforce read-only support access restrictions.
/// It is also <see cref="IAuthorizationRequirementData"/>: the authorization middleware only adds endpoint metadata
/// of that type to the endpoint's policy, so as a bare requirement the marker was never evaluated and read-only
/// support sessions were classified by HTTP method alone (R12).
/// </summary>
public sealed record TenantAdminOperationRequirement : IAuthorizationRequirement, IAuthorizationRequirementData
{
    public TenantAdminOperationKind Kind;

    public IEnumerable<IAuthorizationRequirement> GetRequirements()
    {
        yield return this;
    }
}
