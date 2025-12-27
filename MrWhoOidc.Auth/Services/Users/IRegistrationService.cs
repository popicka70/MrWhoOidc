using System;
using System.Threading;
using System.Threading.Tasks;

namespace MrWhoOidc.Auth.Services.Users;

/// <summary>
/// Domain service for managing user and tenant registrations.
/// </summary>
public interface IRegistrationService
{
    /// <summary>
    /// Creates a new registration request.
    /// </summary>
    Task<RegistrationResult> CreateRegistrationAsync(RegistrationInput input, CancellationToken cancellationToken = default);

    /// <summary>
    /// Approves an existing registration, creating the user and tenant if necessary.
    /// </summary>
    Task<RegistrationResult> ApproveRegistrationAsync(Guid registrationId, Guid? approvingUserId = null, CancellationToken cancellationToken = default);
}
