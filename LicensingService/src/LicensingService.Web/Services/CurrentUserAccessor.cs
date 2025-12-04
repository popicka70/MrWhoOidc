using System.Security.Claims;

namespace LicensingService.Web.Services;

/// <summary>
/// Service to access the current authenticated user's claims.
/// </summary>
public interface ICurrentUserAccessor
{
    /// <summary>Gets the current user's unique identifier (sub claim).</summary>
    string? UserId { get; }
    
    /// <summary>Gets the current user's display name.</summary>
    string? UserName { get; }
    
    /// <summary>Gets whether the current user is authenticated.</summary>
    bool IsAuthenticated { get; }
    
    /// <summary>Gets all claims for the current user.</summary>
    IEnumerable<Claim> Claims { get; }
}

/// <summary>
/// Default implementation using IHttpContextAccessor.
/// </summary>
public class CurrentUserAccessor : ICurrentUserAccessor
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentUserAccessor(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    private ClaimsPrincipal? User => _httpContextAccessor.HttpContext?.User;

    public string? UserId => User?.FindFirstValue(ClaimTypes.NameIdentifier) 
                            ?? User?.FindFirstValue("sub");

    public string? UserName => User?.FindFirstValue(ClaimTypes.Name) 
                              ?? User?.FindFirstValue("name")
                              ?? User?.FindFirstValue("preferred_username");

    public bool IsAuthenticated => User?.Identity?.IsAuthenticated ?? false;

    public IEnumerable<Claim> Claims => User?.Claims ?? Enumerable.Empty<Claim>();
}
