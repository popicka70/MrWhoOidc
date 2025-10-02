using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Utils;
using System.ComponentModel.DataAnnotations;

namespace MrWhoOidc.WebAuth.Services;

/// <summary>
/// Service for handling user registration creation and approval workflows.
/// </summary>
public interface IRegistrationService
{
    /// <summary>
    /// Creates a new user registration and optionally auto-approves it based on client settings.
    /// </summary>
    /// <param name="email">User's email address</param>
    /// <param name="firstName">Optional first name</param>
    /// <param name="lastName">Optional last name</param>
    /// <param name="clientId">Associated client ID</param>
    /// <param name="passwordHash">Optional password hash (for local registrations)</param>
    /// <param name="isExternalIdp">Whether registration comes from external IdP</param>
    /// <param name="autoApprove">Whether to immediately approve based on client policy</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The created user ID if approved, null if pending manual approval</returns>
    Task<Guid?> CreateAndMaybeApproveRegistrationAsync(
        string email,
        string? firstName,
        string? lastName,
        Guid? clientId,
        string? passwordHash,
        bool isExternalIdp,
        bool autoApprove,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Approves a pending registration and creates the user account.
    /// </summary>
    Task<Guid> ApproveRegistrationAsync(Registration registration, Guid? approvingUserId = null, CancellationToken cancellationToken = default);
}

internal sealed class RegistrationService : IRegistrationService
{
    private readonly AuthDbContext _db;
    private readonly ILogger<RegistrationService> _logger;

    public RegistrationService(AuthDbContext db, ILogger<RegistrationService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<Guid?> CreateAndMaybeApproveRegistrationAsync(
        string email,
        string? firstName,
        string? lastName,
        Guid? clientId,
        string? passwordHash,
        bool isExternalIdp,
        bool autoApprove,
        CancellationToken cancellationToken = default)
    {
        var normalized = EmailNormalizer.NormalizeForLookup(email);
        if (string.IsNullOrEmpty(normalized))
        {
            throw new ValidationException("Invalid email address.");
        }

        // Check if user already exists
        var userExists = await _db.Users.AsNoTracking()
            .AnyAsync(u => u.NormalizedEmail == normalized, cancellationToken);
        if (userExists)
        {
            throw new InvalidOperationException("A user with this email already exists.");
        }

        // Check for existing pending registration
        var pendingReg = await _db.Set<Registration>()
            .FirstOrDefaultAsync(r => r.NormalizedEmail == normalized && r.State == "pending", cancellationToken);
        if (pendingReg is not null)
        {
            _logger.LogInformation("Pending registration already exists for {Email}", email);
            return null; // Existing pending registration
        }

        var registration = new Registration
        {
            Email = email,
            NormalizedEmail = normalized,
            FirstName = firstName,
            LastName = lastName,
            ClientId = clientId,
            PasswordHash = passwordHash,
            State = "pending",
            CreatedAt = DateTimeOffset.UtcNow
        };

        _db.Set<Registration>().Add(registration);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Registration created for {Email}, AutoApprove={AutoApprove}, IsExternalIdp={IsExternalIdp}",
            email, autoApprove, isExternalIdp);

        if (autoApprove)
        {
            return await ApproveRegistrationAsync(registration, null, cancellationToken);
        }

        return null; // Pending manual approval
    }

    public async Task<Guid> ApproveRegistrationAsync(
        Registration registration,
        Guid? approvingUserId = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(registration.State, "pending", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Registration {registration.Id} is not in pending state.");
        }

        var normalized = registration.NormalizedEmail ?? EmailNormalizer.NormalizeForLookup(registration.Email) ?? string.Empty;
        if (string.IsNullOrEmpty(normalized))
        {
            throw new ValidationException("Registration rejected because the email is invalid.");
        }

        string emailForUser;
        try
        {
            emailForUser = EmailNormalizer.FormatForStorage(registration.Email, required: true, out var normalizedFromFormat)
                ?? throw new ValidationException("Email is required.");
            normalized = normalizedFromFormat ?? normalized;
        }
        catch (ValidationException ex)
        {
            registration.State = "rejected";
            registration.RejectedAt = DateTimeOffset.UtcNow;
            registration.RejectedByUserId = approvingUserId;
            await _db.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException($"Registration rejected: {ex.Message}", ex);
        }

        var existing = await _db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, cancellationToken);
        if (existing is not null)
        {
            registration.State = "rejected";
            registration.RejectedAt = DateTimeOffset.UtcNow;
            registration.RejectedByUserId = approvingUserId;
            await _db.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException("Registration rejected because a user with this email already exists.");
        }

        // Create user
        var user = new User
        {
            Username = normalized,
            Email = emailForUser,
            EmailVerified = false,
            Name = string.Join(' ', new[] { registration.FirstName, registration.LastName }.Where(s => !string.IsNullOrWhiteSpace(s))),
            HashAlgorithm = "argon2id",
            PasswordHash = string.IsNullOrEmpty(registration.PasswordHash) ? string.Empty : registration.PasswordHash
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync(cancellationToken);

        // Optional assign to client
        if (registration.ClientId is Guid clientId)
        {
            var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clientId, cancellationToken);
            if (client is not null)
            {
                var exists = await _db.UserClientAssignments.AnyAsync(
                    a => a.UserId == user.Id && a.ClientId == client.Id && a.RealmId == client.RealmId, 
                    cancellationToken);
                if (!exists)
                {
                    _db.UserClientAssignments.Add(new UserClientAssignment
                    {
                        UserId = user.Id,
                        ClientId = client.Id,
                        RealmId = client.RealmId,
                        IsActive = true
                    });
                    await _db.SaveChangesAsync(cancellationToken);
                }
            }
        }

        registration.State = "approved";
        registration.ApprovedAt = DateTimeOffset.UtcNow;
        registration.ApprovedByUserId = approvingUserId;
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Registration {RegistrationId} approved, User {UserId} created", registration.Id, user.Id);

        return user.Id;
    }
}
