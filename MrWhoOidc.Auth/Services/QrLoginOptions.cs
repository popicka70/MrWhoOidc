using System.ComponentModel.DataAnnotations;

namespace MrWhoOidc.Auth.Services;

public sealed class QrLoginOptions
{
    public bool Enabled { get; set; } = false;

    [Range(60, 600)]
    public int SessionLifetimeSeconds { get; set; } = 300;

    [Range(1, 20)]
    public int QrCodePixelsPerModule { get; set; } = 10;

    public string QrCodeErrorCorrectionLevel { get; set; } = "M";

    [Range(1, 10)]
    public int PollIntervalSeconds { get; set; } = 2;

    [Range(1, 300)]
    public int MaxPollAttempts { get; set; } = 150;

    public bool AllowMultipleScans { get; set; } = false;

    [Range(30, 300)]
    public int CleanupIntervalSeconds { get; set; } = 60;

    [Range(60, 3600)]
    public int CleanupGracePeriodSeconds { get; set; } = 600;

    public string? BaseUrl { get; set; }
}
