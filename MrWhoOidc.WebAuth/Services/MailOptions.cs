namespace MrWhoOidc.WebAuth.Services;

public sealed class MailOptions
{
    public bool Enabled { get; set; } = true;
    public string FromAddress { get; set; } = "no-reply@mrwho.local";
    public string? FromName { get; set; } = "MrWhoOidc";
    public string SmtpHost { get; set; } = "localhost";
    public int SmtpPort { get; set; } = 1025;
    public bool UseSsl { get; set; } = false;
    public string? Username { get; set; }
    public string? Password { get; set; }
}
