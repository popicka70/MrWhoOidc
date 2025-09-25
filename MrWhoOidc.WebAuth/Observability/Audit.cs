using System.Security.Cryptography;
using System.Text;

namespace MrWhoOidc.WebAuth.Observability;

public sealed class AuditOptions
{
    // Feature flag to enable/disable audit emission
    public bool Enabled { get; set; } = true;
    // Optional pepper for hashing PII like sid/sub
    public string? Pepper { get; set; }
}

public interface IAuditSink
{
    void Emit(string type, object payload);
    string? HashValue(string? value);
}

public sealed class NoopAuditSink : IAuditSink
{
    public void Emit(string type, object payload) { }
    public string? HashValue(string? value) => null;
}

public sealed class LoggerAuditSink(ILogger<LoggerAuditSink> logger, Microsoft.Extensions.Options.IOptions<AuditOptions> options) : IAuditSink
{
    private readonly ILogger<LoggerAuditSink> _logger = logger;
    private readonly string? _pepper = options.Value.Pepper;

    public void Emit(string type, object payload)
    {
        // Single, consistent audit log line with structured properties
        // Avoid logging any raw JWT or secrets in payload objects
        _logger.LogInformation("audit {Type} {@Event}", type, payload);
    }

    public string? HashValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var input = string.IsNullOrEmpty(_pepper) ? value : ($"{_pepper}:{value}");
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch
        {
            return null;
        }
    }
}
