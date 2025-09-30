using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.IdentityModel.Tokens;

namespace MrWhoOidc.Client.Options;

public sealed class JarOptions
{
    public bool Enabled { get; set; }

    public TimeSpan Lifetime { get; set; } = TimeSpan.FromMinutes(5);

    public string SigningAlgorithm { get; set; } = SecurityAlgorithms.HmacSha256;

    public string? SigningKeyId { get; set; }

    public string? Audience { get; set; }

    public Func<CancellationToken, ValueTask<SigningCredentials?>>? SigningCredentialsResolver { get; set; }
}
