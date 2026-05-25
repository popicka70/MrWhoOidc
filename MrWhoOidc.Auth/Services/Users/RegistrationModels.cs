using System;

namespace MrWhoOidc.Auth.Services.Users;

public record RegistrationInput(
    string Email,
    string? FirstName,
    string? LastName,
    Guid? ClientId,
    string? PasswordHash,
    bool AutoApprove = false,
    bool IsExternalIdp = false,
    TenantCreationInput? TenantCreation = null,
    Guid? TargetTenantId = null,
    bool IsPlatformRegistration = false
);

public record TenantCreationInput(
    string Slug,
    string Name,
    string? Description
);

public enum RegistrationOutcome
{
    PendingCreated,
    PendingExisting,
    Approved,
    ExistingUser
}

public record RegistrationResult(
    Guid? RegistrationId,
    string State,
    RegistrationOutcome Outcome,
    Guid? CreatedUserId = null,
    Guid? ExistingUserId = null,
    Guid? CreatedTenantId = null
);
