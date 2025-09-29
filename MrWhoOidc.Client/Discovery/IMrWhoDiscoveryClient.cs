namespace MrWhoOidc.Client.Discovery;

public interface IMrWhoDiscoveryClient
{
    ValueTask<MrWhoDiscoveryDocument> GetAsync(CancellationToken cancellationToken = default);
}
