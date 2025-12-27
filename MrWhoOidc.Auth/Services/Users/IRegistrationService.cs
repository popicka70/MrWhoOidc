using System;
using System.Threading;
using System.Threading.Tasks;

namespace MrWhoOidc.Auth.Services.Users;

public interface IRegistrationService
{
    Task<RegistrationResult> CreateRegistrationAsync(RegistrationInput input, CancellationToken cancellationToken = default);
    Task<RegistrationResult> ApproveRegistrationAsync(Guid registrationId, Guid? approvingUserId = null, CancellationToken cancellationToken = default);
}
