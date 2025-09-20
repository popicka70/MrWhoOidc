namespace MrWhoOidc.Auth.Services;

public sealed class KeyRotationOptions
{
    public bool Enabled { get; set; } = true;
    public TimeSpan RotationInterval { get; set; } = TimeSpan.FromDays(7);
    // Publish retired keys in JWKS for this overlap duration so existing tokens validate
    public TimeSpan Overlap { get; set; } = TimeSpan.FromDays(2);
    // How often to check rotation conditions
    public TimeSpan CheckPeriod { get; set; } = TimeSpan.FromHours(1);
}
