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

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }
}
