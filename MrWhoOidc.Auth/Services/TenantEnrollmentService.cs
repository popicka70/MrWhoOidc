using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Utils;

namespace MrWhoOidc.Auth.Services;

public interface ITenantEnrollmentService
{
    Task<TenantInvitationCreateResult> CreateInvitationAsync(
        Guid tenantId,
        string email,
        string? displayName,
        bool isTenantAdmin,
        TimeSpan validFor,
        Guid? invitedByUserId,
        string? invitedByUsername,
        CancellationToken ct = default);

    Task<IReadOnlyList<TenantInvitationListItem>> ListInvitationsAsync(Guid tenantId, CancellationToken ct = default);

    Task<TenantInvitationDetails?> GetInvitationAsync(string token, CancellationToken ct = default);

    Task<TenantInvitationAcceptResult> AcceptInvitationAsync(string token, Guid userAccountId, CancellationToken ct = default);

    Task<TenantInvitationAcceptResult> AcceptInvitationForUserAsync(string token, Guid userId, CancellationToken ct = default);

    Task<bool> RevokeInvitationAsync(Guid tenantId, Guid invitationId, Guid? revokedByUserId, string? reason, CancellationToken ct = default);
}

public sealed record TenantInvitationCreateResult(TenantInvitation Invitation, string Token);

public sealed record TenantInvitationListItem(
    Guid Id,
    string Email,
    string? DisplayName,
    TenantInvitationStatus Status,
    bool IsTenantAdmin,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? AcceptedAt,
    DateTimeOffset? RevokedAt,
    string? InvitedByUsername);

public sealed record TenantInvitationDetails(
    Guid Id,
    Guid TenantId,
    string TenantSlug,
    string TenantName,
    string Email,
    string NormalizedEmail,
    string? DisplayName,
    TenantInvitationStatus Status,
    bool IsTenantAdmin,
    DateTimeOffset ExpiresAt,
    bool IsAcceptable);

public sealed record TenantInvitationAcceptResult(
    bool Success,
    string? ErrorCode,
    string? ErrorMessage,
    Guid? UserId,
    Guid? UserAccountId,
    Guid? TenantId,
    string? TenantSlug);

internal sealed class TenantEnrollmentService(AuthDbContext db, ILogger<TenantEnrollmentService> logger) : ITenantEnrollmentService
{
    public async Task<TenantInvitationCreateResult> CreateInvitationAsync(
        Guid tenantId,
        string email,
        string? displayName,
        bool isTenantAdmin,
        TimeSpan validFor,
        Guid? invitedByUserId,
        string? invitedByUsername,
        CancellationToken ct = default)
    {
        var formattedEmail = EmailNormalizer.FormatForStorage(email, required: true, out var normalizedEmail)
            ?? throw new ArgumentException("Invitation email is required.", nameof(email));

        if (string.IsNullOrWhiteSpace(normalizedEmail))
        {
            throw new ArgumentException("Invitation email is invalid.", nameof(email));
        }

        var tenantExists = await db.Tenants.AsNoTracking()
            .AnyAsync(t => t.Id == tenantId && t.Status == TenantStatus.Active, ct)
            .ConfigureAwait(false);
        if (!tenantExists)
        {
            throw new InvalidOperationException("Tenant is not available for invitations.");
        }

        var defaultRealmId = await db.Realms.AsNoTracking()
            .Where(r => r.TenantId == tenantId)
            .OrderByDescending(r => r.Name == "default")
            .ThenBy(r => r.Name)
            .Select(r => (Guid?)r.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        var token = GenerateToken();
        var invitation = new TenantInvitation
        {
            TenantId = tenantId,
            Email = formattedEmail,
            NormalizedEmail = normalizedEmail,
            TokenHash = HashToken(token),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
            IsTenantAdmin = isTenantAdmin,
            DefaultRealmId = defaultRealmId,
            InvitedByUserId = invitedByUserId,
            InvitedByUsername = string.IsNullOrWhiteSpace(invitedByUsername) ? null : invitedByUsername.Trim(),
            ExpiresAt = DateTimeOffset.UtcNow.Add(validFor <= TimeSpan.Zero ? TimeSpan.FromDays(7) : validFor)
        };

        db.TenantInvitations.Add(invitation);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation("Created tenant invitation {InvitationId} for tenant {TenantId}", invitation.Id, tenantId);
        return new TenantInvitationCreateResult(invitation, token);
    }

    public async Task<IReadOnlyList<TenantInvitationListItem>> ListInvitationsAsync(Guid tenantId, CancellationToken ct = default)
    {
        return await db.TenantInvitations.AsNoTracking()
            .Where(i => i.TenantId == tenantId)
            .OrderByDescending(i => i.CreatedAt)
            .Select(i => new TenantInvitationListItem(
                i.Id,
                i.Email,
                i.DisplayName,
                EffectiveStatus(i.Status, i.ExpiresAt),
                i.IsTenantAdmin,
                i.CreatedAt,
                i.ExpiresAt,
                i.AcceptedAt,
                i.RevokedAt,
                i.InvitedByUsername))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<TenantInvitationDetails?> GetInvitationAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var hash = HashToken(token.Trim());
        var invitation = await db.TenantInvitations.AsNoTracking()
            .Include(i => i.Tenant)
            .FirstOrDefaultAsync(i => i.TokenHash == hash, ct)
            .ConfigureAwait(false);

        if (invitation is null)
        {
            return null;
        }

        var status = EffectiveStatus(invitation.Status, invitation.ExpiresAt);
        return new TenantInvitationDetails(
            invitation.Id,
            invitation.TenantId,
            invitation.Tenant.Slug,
            invitation.Tenant.Name,
            invitation.Email,
            invitation.NormalizedEmail,
            invitation.DisplayName,
            status,
            invitation.IsTenantAdmin,
            invitation.ExpiresAt,
            status == TenantInvitationStatus.Pending);
    }

    public async Task<TenantInvitationAcceptResult> AcceptInvitationAsync(string token, Guid userAccountId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return Failure("invalid_invitation", "Invitation link is invalid.");
        }

        var hash = HashToken(token.Trim());
        var invitation = await db.TenantInvitations
            .Include(i => i.Tenant)
            .FirstOrDefaultAsync(i => i.TokenHash == hash, ct)
            .ConfigureAwait(false);

        if (invitation is null)
        {
            return Failure("invalid_invitation", "Invitation link is invalid.");
        }

        if (invitation.Status != TenantInvitationStatus.Pending)
        {
            return Failure("invitation_not_pending", "Invitation is no longer available.");
        }

        if (invitation.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            invitation.Status = TenantInvitationStatus.Expired;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Failure("invitation_expired", "Invitation has expired.");
        }

        var account = await db.UserAccounts.FirstOrDefaultAsync(a => a.Id == userAccountId, ct).ConfigureAwait(false);
        if (account is null)
        {
            return Failure("account_not_found", "Your account could not be found.");
        }

        var accountEmail = EmailNormalizer.NormalizeForLookup(account.NormalizedEmail ?? account.Email);
        if (!string.Equals(accountEmail, invitation.NormalizedEmail, StringComparison.Ordinal))
        {
            return Failure("email_mismatch", "Sign in with the email address this invitation was sent to.");
        }

        var user = await EnsureTenantUserAsync(account, invitation, ct).ConfigureAwait(false);
        await EnsureMembershipAsync(account, invitation, ct).ConfigureAwait(false);
        await EnsureTenantAdminRoleAsync(user, invitation, ct).ConfigureAwait(false);

        invitation.Status = TenantInvitationStatus.Accepted;
        invitation.AcceptedAt = DateTimeOffset.UtcNow;
        invitation.AcceptedByUserAccountId = account.Id;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation("Accepted tenant invitation {InvitationId} for tenant {TenantId}", invitation.Id, invitation.TenantId);
        return new TenantInvitationAcceptResult(true, null, null, user.Id, account.Id, invitation.TenantId, invitation.Tenant.Slug);
    }

    public async Task<TenantInvitationAcceptResult> AcceptInvitationForUserAsync(string token, Guid userId, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct).ConfigureAwait(false);
        if (user is null)
        {
            return Failure("user_not_found", "User could not be found.");
        }

        UserAccount? account = null;
        var normalizedEmail = EmailNormalizer.NormalizeForLookup(user.NormalizedEmail ?? user.Email);
        if (!string.IsNullOrWhiteSpace(normalizedEmail))
        {
            account = await db.UserAccounts.FirstOrDefaultAsync(a => a.NormalizedEmail == normalizedEmail, ct).ConfigureAwait(false);
        }

        account ??= await db.UserAccounts.FirstOrDefaultAsync(a => a.Id == user.Id, ct).ConfigureAwait(false);
        if (account is null)
        {
            return Failure("account_not_found", "User account could not be found.");
        }

        return await AcceptInvitationAsync(token, account.Id, ct).ConfigureAwait(false);
    }

    public async Task<bool> RevokeInvitationAsync(Guid tenantId, Guid invitationId, Guid? revokedByUserId, string? reason, CancellationToken ct = default)
    {
        var invitation = await db.TenantInvitations
            .FirstOrDefaultAsync(i => i.Id == invitationId && i.TenantId == tenantId, ct)
            .ConfigureAwait(false);
        if (invitation is null || invitation.Status != TenantInvitationStatus.Pending)
        {
            return false;
        }

        invitation.Status = TenantInvitationStatus.Revoked;
        invitation.RevokedAt = DateTimeOffset.UtcNow;
        invitation.RevokedByUserId = revokedByUserId;
        invitation.RevocationReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    private async Task<User> EnsureTenantUserAsync(UserAccount account, TenantInvitation invitation, CancellationToken ct)
    {
        var normalizedEmail = invitation.NormalizedEmail;
        var user = await db.Users.FirstOrDefaultAsync(
            u => u.TenantId == invitation.TenantId && u.NormalizedEmail == normalizedEmail,
            ct).ConfigureAwait(false);

        if (user is not null)
        {
            return user;
        }

        var username = await BuildTenantUsernameAsync(account, invitation.TenantId, normalizedEmail, ct).ConfigureAwait(false);
        user = new User
        {
            TenantId = invitation.TenantId,
            Username = username,
            Email = account.Email ?? invitation.Email,
            EmailVerified = account.EmailVerified,
            EmailVerifiedAt = account.EmailVerifiedAt,
            Name = invitation.DisplayName ?? account.Name
        };

        db.Users.Add(user);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return user;
    }

    private async Task EnsureMembershipAsync(UserAccount account, TenantInvitation invitation, CancellationToken ct)
    {
        var membership = await db.UserTenantMemberships.FirstOrDefaultAsync(
            m => m.UserAccountId == account.Id && m.TenantId == invitation.TenantId,
            ct).ConfigureAwait(false);

        if (membership is null)
        {
            db.UserTenantMemberships.Add(new UserTenantMembership
            {
                UserAccountId = account.Id,
                TenantId = invitation.TenantId,
                DefaultRealmId = invitation.DefaultRealmId,
                DisplayName = invitation.DisplayName ?? account.Name ?? account.Username,
                IsTenantAdmin = invitation.IsTenantAdmin,
                Status = TenantMembershipStatus.Active
            });
            return;
        }

        membership.Status = TenantMembershipStatus.Active;
        membership.SuspendedAt = null;
        membership.ExpiresAt = null;
        membership.DefaultRealmId ??= invitation.DefaultRealmId;
        membership.DisplayName ??= invitation.DisplayName ?? account.Name ?? account.Username;
        membership.IsTenantAdmin = membership.IsTenantAdmin || invitation.IsTenantAdmin;
    }

    private async Task EnsureTenantAdminRoleAsync(User user, TenantInvitation invitation, CancellationToken ct)
    {
        if (!invitation.IsTenantAdmin)
        {
            return;
        }

        var tenantAdminRole = await db.Roles.FirstOrDefaultAsync(
            r => r.TenantId == invitation.TenantId && r.Name == "tenant-admin" && r.IsActive,
            ct).ConfigureAwait(false);
        if (tenantAdminRole is null)
        {
            return;
        }

        var exists = await db.UserRealmRoleAssignments.AnyAsync(
            a => a.UserId == user.Id && a.RoleId == tenantAdminRole.Id && a.RealmId == tenantAdminRole.RealmId && a.IsActive,
            ct).ConfigureAwait(false);
        if (exists)
        {
            return;
        }

        db.UserRealmRoleAssignments.Add(new UserRealmRoleAssignment
        {
            UserId = user.Id,
            RoleId = tenantAdminRole.Id,
            RealmId = tenantAdminRole.RealmId,
            IsActive = true
        });
    }

    private async Task<string> BuildTenantUsernameAsync(UserAccount account, Guid tenantId, string normalizedEmail, CancellationToken ct)
    {
        var candidate = !string.IsNullOrWhiteSpace(account.Username)
            ? account.Username.Trim()
            : normalizedEmail;

        if (candidate.Length > 200)
        {
            candidate = candidate[..200];
        }

        var exists = await db.Users.AsNoTracking()
            .AnyAsync(u => u.TenantId == tenantId && u.Username == candidate, ct)
            .ConfigureAwait(false);
        if (!exists)
        {
            return candidate;
        }

        var suffix = $"-{account.Id:N}";
        var maxBaseLength = Math.Max(1, 200 - suffix.Length);
        var baseName = normalizedEmail.Length <= maxBaseLength ? normalizedEmail : normalizedEmail[..maxBaseLength];
        return baseName + suffix;
    }

    private static TenantInvitationAcceptResult Failure(string code, string message)
        => new(false, code, message, null, null, null, null);

    private static TenantInvitationStatus EffectiveStatus(TenantInvitationStatus status, DateTimeOffset expiresAt)
        => status == TenantInvitationStatus.Pending && expiresAt <= DateTimeOffset.UtcNow
            ? TenantInvitationStatus.Expired
            : status;

    private static string GenerateToken()
        => "inv_" + Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    private static string HashToken(string token)
        => CryptoHelper.ComputeSha256Hex(token);
}