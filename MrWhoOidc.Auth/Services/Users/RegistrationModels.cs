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
    Guid? TargetTenantId = null
);

public record TenantCreationInput(
    string Slug,
    string Name,
    string? Description
);

public record RegistrationResult(
    Guid RegistrationId,
    string State,
    Guid? CreatedUserId = null,
    Guid? CreatedTenantId = null
);
