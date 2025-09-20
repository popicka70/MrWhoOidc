namespace MrWhoOidc.Auth.Services;

public sealed class AuthOptions
{
    public string[] ApiAudiences { get; set; } = ["api"]; // default
}
