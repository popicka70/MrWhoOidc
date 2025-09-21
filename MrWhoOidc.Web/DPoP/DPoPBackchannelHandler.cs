using System.Net.Http.Headers;
using System.Text;
using MrWhoOidc.Web.DPoP;

namespace MrWhoOidc.Web;

public sealed class DPoPBackchannelHandler : DelegatingHandler
{
    private readonly DPoPKeyStore _keyStore;

    public DPoPBackchannelHandler(DPoPKeyStore keyStore, HttpMessageHandler inner)
        : base(inner)
    {
        _keyStore = keyStore;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await AttachDpopAsync(request, nonce: null, cancellationToken);
        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized &&
            response.Headers.TryGetValues("DPoP-Nonce", out var nonces))
        {
            var nonce = nonces.FirstOrDefault();
            if (!string.IsNullOrEmpty(nonce))
            {
                // Retry once with nonce
                response.Dispose();
                using var retry = await CloneRequestAsync(request);
                await AttachDpopAsync(retry, nonce, cancellationToken);
                return await base.SendAsync(retry, cancellationToken);
            }
        }
        return response;
    }

    private async Task AttachDpopAsync(HttpRequestMessage request, string? nonce, CancellationToken ct)
    {
        var (key, jwk) = _keyStore.GetOrCreateKey();
        var method = request.Method.Method;
        var url = request.RequestUri!.ToString();

        string? ath = null;
        // Compute ath when Bearer token is present (e.g., userinfo)
        if (request.Headers.Authorization is AuthenticationHeaderValue auth &&
            string.Equals(auth.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(auth.Parameter))
        {
            ath = DPoPProof.ComputeAth(auth.Parameter);
        }

        var proof = DPoPProof.Create(key, jwk, method, url, ath, nonce);
        request.Headers.Remove("DPoP");
        request.Headers.Add("DPoP", proof);
        await Task.CompletedTask;
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage original)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri);

        // Copy headers
        foreach (var header in original.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        // Copy content if any
        if (original.Content is not null)
        {
            var ms = new MemoryStream();
            await original.Content.CopyToAsync(ms);
            ms.Position = 0;
            var contentClone = new StreamContent(ms);
            foreach (var h in original.Content.Headers)
                contentClone.Headers.TryAddWithoutValidation(h.Key, h.Value);
            clone.Content = contentClone;
        }

        // Copy properties/options
        foreach (var prop in original.Options)
            clone.Options.Set(new(prop.Key), prop.Value);

        return clone;
    }
}
