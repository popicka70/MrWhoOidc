using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services.Authorization;

internal static class ClientAuthenticationClassifier
{
    public static async Task<bool> IsPublicClientAsync(Client client, IClientStore clientStore, CancellationToken ct = default)
    {
        if (string.Equals(client.TokenEndpointAuthMethod, "none", StringComparison.Ordinal))
        {
            return true;
        }

#pragma warning disable CS0618 // ClientSecretHash is legacy storage and still indicates a confidential client.
        if (!string.IsNullOrEmpty(client.ClientSecretHash))
        {
            return false;
        }
#pragma warning restore CS0618

        if (string.Equals(client.TokenEndpointAuthMethod, "private_key_jwt", StringComparison.Ordinal) ||
            string.Equals(client.TokenEndpointAuthMethod, "self_signed_tls_client_auth", StringComparison.Ordinal) ||
            string.Equals(client.TokenEndpointAuthMethod, "tls_client_auth", StringComparison.Ordinal))
        {
            return false;
        }

        var activeSecrets = await clientStore.GetActiveSecretsAsync(client.Id, ct).ConfigureAwait(false);
        return activeSecrets.Count == 0;
    }
}