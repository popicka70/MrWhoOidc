using System;
using System.Threading;
using System.Threading.Tasks;

namespace MrWhoOidc.Auth.Services.Authorization;

/// <summary>
/// Manages the assignment of users to clients, ensuring that a user is authorized to use a specific client.
/// </summary>
public interface IUserClientAssignmentService
{
    /// <summary>
    /// Ensures that the user is assigned to the client, potentially backfilling the assignment if auto-approval is enabled.
    /// </summary>
    /// <param name="userId">The unique identifier of the user.</param>
    /// <param name="clientId">The client identifier.</param>
    /// <param name="idp">The identity provider used for authentication.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A tuple indicating if the user is assigned and an error message if not.</returns>
    Task<(bool assigned, string? error)> EnsureAssignedAsync(Guid userId, string clientId, string? idp, CancellationToken ct = default);
}
