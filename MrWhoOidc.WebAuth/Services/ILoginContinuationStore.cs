using System.Threading;
using System.Threading.Tasks;

namespace MrWhoOidc.WebAuth.Services;

public interface ILoginContinuationStore
{
    Task<string> StoreAsync(string continuation, CancellationToken cancellationToken);

    Task<string?> TryGetAsync(string key, CancellationToken cancellationToken);

    Task RemoveAsync(string key, CancellationToken cancellationToken);
}
