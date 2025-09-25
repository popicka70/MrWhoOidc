namespace MrWhoOidc.Web.Backchannel;

public sealed class BackchannelOptions
{
    public bool Enabled { get; set; } = true;
    public string Authority { get; set; } = string.Empty; // Issuer base URL with trailing slash
    public string ClientId { get; set; } = string.Empty; // RP client id for aud check
    public TimeSpan AllowedClockSkew { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan JtiTtl { get; set; } = TimeSpan.FromMinutes(10);
    public TimeSpan SidTtl { get; set; } = TimeSpan.FromHours(12);
    public TimeSpan JwksTtl { get; set; } = TimeSpan.FromMinutes(10);
}
