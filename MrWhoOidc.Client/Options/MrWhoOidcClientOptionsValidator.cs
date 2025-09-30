using System;
using Microsoft.Extensions.Options;

namespace MrWhoOidc.Client.Options;

internal sealed class MrWhoOidcClientOptionsValidator : IValidateOptions<MrWhoOidcClientOptions>
{
    public ValidateOptionsResult Validate(string? name, MrWhoOidcClientOptions options)
    {
        if (options is null)
        {
            return ValidateOptionsResult.Fail("Options instance is null.");
        }

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Issuer))
        {
            failures.Add("Issuer must be provided.");
        }
        else if (!Uri.TryCreate(options.Issuer, UriKind.Absolute, out var issuerUri) || (options.RequireHttpsMetadata && !string.Equals(issuerUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            failures.Add("Issuer must be an absolute HTTPS URI.");
        }

        if (options.DiscoveryUri is not null)
        {
            if (!options.DiscoveryUri.IsAbsoluteUri)
            {
                failures.Add("DiscoveryUri must be absolute.");
            }
            else if (options.RequireHttpsMetadata && !string.Equals(options.DiscoveryUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add("DiscoveryUri must be HTTPS when RequireHttpsMetadata is true.");
            }
        }

        if (string.IsNullOrWhiteSpace(options.ClientId))
        {
            failures.Add("ClientId must be provided.");
        }

        var hasSecret = !string.IsNullOrWhiteSpace(options.ClientSecret);
        var hasAssertion = !string.IsNullOrWhiteSpace(options.ClientAssertion);

        if (options.PublicClient)
        {
            if (hasSecret)
            {
                failures.Add("Public clients must not configure ClientSecret.");
            }
            if (hasAssertion)
            {
                failures.Add("Public clients must not configure ClientAssertion.");
            }
        }
        else if (!hasSecret && !hasAssertion)
        {
            failures.Add("Confidential clients must configure either ClientSecret or ClientAssertion.");
        }

        if (hasAssertion && string.IsNullOrWhiteSpace(options.ClientAssertionType))
        {
            failures.Add("ClientAssertionType must be provided when ClientAssertion is set.");
        }

        if (!options.Scopes.Any())
        {
            failures.Add("At least one scope must be configured.");
        }

        if (options.MetadataRefreshInterval < TimeSpan.FromSeconds(10))
        {
            failures.Add("MetadataRefreshInterval must be at least 10 seconds.");
        }

        if (options.BackchannelTimeout < TimeSpan.FromSeconds(5))
        {
            failures.Add("BackchannelTimeout must be at least 5 seconds.");
        }

        foreach (var registration in options.OnBehalfOf)
        {
            if (registration.Value is null)
            {
                failures.Add($"On-behalf-of registration '{registration.Key}' must be defined.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(registration.Value.SubjectTokenType))
            {
                failures.Add($"On-behalf-of registration '{registration.Key}' must specify SubjectTokenType.");
            }

            if (string.IsNullOrWhiteSpace(registration.Value.Scope) && string.IsNullOrWhiteSpace(registration.Value.Resource) && string.IsNullOrWhiteSpace(registration.Value.Audience))
            {
                failures.Add($"On-behalf-of registration '{registration.Key}' must configure Scope, Resource, or Audience.");
            }

            if (registration.Value.CacheLifetime is TimeSpan cacheLifetime && cacheLifetime <= TimeSpan.Zero)
            {
                failures.Add($"On-behalf-of registration '{registration.Key}' CacheLifetime must be positive when specified.");
            }
        }

        foreach (var registration in options.ClientCredentials)
        {
            if (registration.Value is null)
            {
                failures.Add($"Client credentials registration '{registration.Key}' must be defined.");
                continue;
            }

            var hasScopes = registration.Value.Scopes.Count > 0;
            if (!hasScopes && string.IsNullOrWhiteSpace(registration.Value.Resource) && string.IsNullOrWhiteSpace(registration.Value.Audience))
            {
                failures.Add($"Client credentials registration '{registration.Key}' must configure Scopes, Resource, or Audience.");
            }

            if (registration.Value.CacheLifetime is TimeSpan cacheLifetime && cacheLifetime <= TimeSpan.Zero)
            {
                failures.Add($"Client credentials registration '{registration.Key}' CacheLifetime must be positive when specified.");
            }
        }

        if (options.Jar.Enabled)
        {
            if (options.Jar.Lifetime <= TimeSpan.Zero)
            {
                failures.Add("Jar.Lifetime must be positive when enabled.");
            }

            if (string.IsNullOrWhiteSpace(options.Jar.SigningAlgorithm))
            {
                failures.Add("Jar.SigningAlgorithm must be provided when JAR is enabled.");
            }

            if (options.Jar.SigningCredentialsResolver is null && string.IsNullOrWhiteSpace(options.ClientSecret))
            {
                failures.Add("Jar requires either ClientSecret or Jar.SigningCredentialsResolver to supply signing credentials.");
            }
        }

        if (options.Jarm.Enabled)
        {
            if (!string.Equals(options.Jarm.ResponseMode, "query.jwt", StringComparison.Ordinal) &&
                !string.Equals(options.Jarm.ResponseMode, "form_post.jwt", StringComparison.Ordinal))
            {
                failures.Add("Jarm.ResponseMode must be 'query.jwt' or 'form_post.jwt'.");
            }
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }
}
