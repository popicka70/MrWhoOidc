using System.Text.Json;

namespace MrWhoOidc.WebAuth.Observability;

public interface IAlertPublisher
{
    Task PublishAsync(string type, object payload, CancellationToken ct = default);
}

public sealed class NoopAlertPublisher : IAlertPublisher
{
    public Task PublishAsync(string type, object payload, CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class WebhookAlertPublisher(IHttpClientFactory httpFactory, ILogger<WebhookAlertPublisher> logger, IConfiguration cfg) : IAlertPublisher
{
    public async Task PublishAsync(string type, object payload, CancellationToken ct = default)
    {
        var url = cfg["Backchannel:AlertWebhook"];
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            var http = httpFactory.CreateClient();
            using var content = new StringContent(JsonSerializer.Serialize(new
            {
                type,
                service = "webauth",
                timestamp = DateTimeOffset.UtcNow,
                payload
            }), System.Text.Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync(url, content, ct);
            resp.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish alert of type {Type}", type);
        }
    }
}
