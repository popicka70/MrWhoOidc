using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MrWhoOidc.WebAuth.Pages.Admin.Users;

/// <summary>
/// Base class for user admin Razor Pages providing a consistent user heading (username + optional friendly name).
/// </summary>
public abstract class UserPageModelBase : PageModel
{
    public string UserHeading { get; private set; } = string.Empty;

    protected void SetHeading(string username, string? name)
    {
        UserHeading = string.IsNullOrWhiteSpace(name) || string.Equals(username, name, StringComparison.OrdinalIgnoreCase)
            ? username
            : $"{username} ({name})";
        ViewData["UserHeading"] = UserHeading;
    }
}
