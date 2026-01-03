using System;
using System.Threading;
using System.Threading.Tasks;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services.SubjectIdentifiers;

public interface IPairwiseSubjectService
{
    Task<string> GetSubjectAsync(Client client, Guid userId, CancellationToken ct = default);
}
