using System.Threading;
using System.Threading.Tasks;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services.SubjectIdentifiers;

public interface ISectorIdentifierResolver
{
    Task<string> ResolveSectorIdentifierAsync(Client client, CancellationToken ct = default);
}
