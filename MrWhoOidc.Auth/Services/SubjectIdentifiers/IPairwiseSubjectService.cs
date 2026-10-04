using System;
using System.Threading;
using System.Threading.Tasks;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services.SubjectIdentifiers;

public interface IPairwiseSubjectService
{
    Task<string> GetSubjectAsync(Client client, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Maps a <c>sub</c> this server issued back to the local user id: a public subject is the user id
    /// itself, a pairwise subject is looked up in the persisted pairwise mapping (current tenant).
    /// Returns null when the subject is unknown.
    /// </summary>
    Task<Guid?> ResolveUserIdAsync(string? subject, CancellationToken ct = default);
}
