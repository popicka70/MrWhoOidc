namespace MrWhoOidc.Auth.Services;

public sealed class EmailConfirmationOptions
{
    public int TokenLifetimeHours { get; set; } = 48;
    public int MaxPendingPerUser { get; set; } = 5;
}
