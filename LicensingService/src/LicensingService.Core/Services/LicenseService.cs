using System.Text.Json;
using LicensingService.Core.Entities;
using LicensingService.Core.Stores;
using Microsoft.Extensions.Logging;

namespace LicensingService.Core.Services;

/// <summary>
/// Implementation of ILicenseService for complete license lifecycle management.
/// </summary>
public class LicenseService : ILicenseService
{
    private readonly ILicenseStore _licenseStore;
    private readonly IProductStore _productStore;
    private readonly ICustomerStore _customerStore;
    private readonly ILicenseTokenGenerator _tokenGenerator;
    private readonly ILogger<LicenseService> _logger;

    /// <summary>
    /// Overlap period in days for license renewals.
    /// </summary>
    private const int RenewalOverlapDays = 60;

    public LicenseService(
        ILicenseStore licenseStore,
        IProductStore productStore,
        ICustomerStore customerStore,
        ILicenseTokenGenerator tokenGenerator,
        ILogger<LicenseService> logger)
    {
        _licenseStore = licenseStore;
        _productStore = productStore;
        _customerStore = customerStore;
        _tokenGenerator = tokenGenerator;
        _logger = logger;
    }

    public async Task<IssueLicenseResult> IssueLicenseAsync(
        IssueLicenseRequest request,
        string issuedBy,
        CancellationToken cancellationToken = default)
    {
        // Validate customer exists
        var customer = await _customerStore.GetByIdAsync(request.CustomerId, cancellationToken);
        if (customer == null)
        {
            return IssueLicenseResult.Failed("Customer not found", "customer_not_found");
        }

        if (customer.Status != "Active")
        {
            return IssueLicenseResult.Failed("Customer is not active", "customer_inactive");
        }

        // Validate product exists
        var product = await _productStore.GetByIdAsync(request.ProductId, cancellationToken);
        if (product == null)
        {
            return IssueLicenseResult.Failed("Product not found", "product_not_found");
        }

        if (product.Status != "Active")
        {
            return IssueLicenseResult.Failed("Product is not active", "product_inactive");
        }

        // Validate options against product option definitions
        var optionValidationErrors = ValidateOptions(request.Options, product.OptionDefinitions);
        if (optionValidationErrors.Count > 0)
        {
            return IssueLicenseResult.Failed(
                "Invalid options provided",
                "invalid_options",
                optionValidationErrors);
        }

        // Validate tier
        if (string.IsNullOrWhiteSpace(request.Tier))
        {
            return IssueLicenseResult.Failed("Tier is required", "tier_required");
        }

        // Calculate validity dates
        var validFrom = request.ValidFrom ?? DateTimeOffset.UtcNow;
        var validUntil = request.ValidUntil;

        if (validUntil <= validFrom)
        {
            return IssueLicenseResult.Failed(
                "ValidUntil must be after ValidFrom",
                "invalid_validity_period");
        }

        try
        {
            // Generate the license token
            var tokenRequest = new GenerateLicenseTokenRequest
            {
                CustomerIdentifier = customer.Identifier,
                ProductIdentifier = product.Identifier,
                Tier = request.Tier,
                Scope = request.Scope,
                ValidFrom = validFrom,
                ValidUntil = validUntil,
                Options = request.Options
            };

            var tokenResult = await _tokenGenerator.GenerateAsync(tokenRequest, cancellationToken);

            // Create the license entity
            var license = new License
            {
                Id = GuidHelper.NewId(),
                TokenId = tokenResult.TokenId,
                SignedToken = tokenResult.Token,
                SigningKeyId = tokenResult.Kid,
                CustomerId = request.CustomerId,
                ProductId = request.ProductId,
                Tier = request.Tier,
                Scope = request.Scope,
                ValidFrom = validFrom,
                ValidUntil = validUntil,
                Options = request.Options != null ? JsonSerializer.Serialize(request.Options) : null,
                Status = LicenseStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedBy = issuedBy
            };

            // Persist the license
            var createdLicense = await _licenseStore.CreateAsync(license, issuedBy, cancellationToken);

            _logger.LogInformation(
                "License {LicenseId} issued for customer {CustomerId} product {ProductId} by {IssuedBy}",
                createdLicense.Id, request.CustomerId, request.ProductId, issuedBy);

            return IssueLicenseResult.Succeeded(createdLicense, tokenResult.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to issue license for customer {CustomerId} product {ProductId}",
                request.CustomerId, request.ProductId);
            return IssueLicenseResult.Failed("Failed to issue license", "issuance_failed");
        }
    }

    public async Task<IssueLicenseResult> RenewLicenseAsync(
        RenewLicenseRequest request,
        string renewedBy,
        CancellationToken cancellationToken = default)
    {
        var originalLicense = await _licenseStore.GetByIdAsync(request.LicenseId, cancellationToken);
        if (originalLicense == null)
        {
            return IssueLicenseResult.Failed("License not found", "license_not_found");
        }

        if (originalLicense.Status == LicenseStatus.Revoked)
        {
            return IssueLicenseResult.Failed("Cannot renew a revoked license", "license_revoked");
        }

        if (originalLicense.Status == LicenseStatus.Renewed)
        {
            return IssueLicenseResult.Failed("License has already been renewed", "already_renewed");
        }

        // Get customer and product for token generation
        var customer = await _customerStore.GetByIdAsync(originalLicense.CustomerId, cancellationToken);
        var product = await _productStore.GetByIdAsync(originalLicense.ProductId, cancellationToken);

        if (customer == null || product == null)
        {
            return IssueLicenseResult.Failed("Customer or product not found", "reference_not_found");
        }

        // Calculate new validity period with 60-day overlap
        var newValidFrom = originalLicense.ValidUntil.AddDays(-RenewalOverlapDays);
        if (newValidFrom < DateTimeOffset.UtcNow)
        {
            newValidFrom = DateTimeOffset.UtcNow;
        }

        // Merge options if updates provided
        var options = MergeOptions(originalLicense.Options, request.OptionUpdates);

        // Validate merged options
        var optionValidationErrors = ValidateOptions(options, product.OptionDefinitions);
        if (optionValidationErrors.Count > 0)
        {
            return IssueLicenseResult.Failed(
                "Invalid options provided",
                "invalid_options",
                optionValidationErrors);
        }

        try
        {
            // Generate new token
            var tokenRequest = new GenerateLicenseTokenRequest
            {
                CustomerIdentifier = customer.Identifier,
                ProductIdentifier = product.Identifier,
                Tier = originalLicense.Tier,
                Scope = originalLicense.Scope,
                ValidFrom = newValidFrom,
                ValidUntil = request.NewValidUntil,
                Options = options
            };

            var tokenResult = await _tokenGenerator.GenerateAsync(tokenRequest, cancellationToken);

            // Create new license
            var newLicense = new License
            {
                Id = GuidHelper.NewId(),
                TokenId = tokenResult.TokenId,
                SignedToken = tokenResult.Token,
                SigningKeyId = tokenResult.Kid,
                CustomerId = originalLicense.CustomerId,
                ProductId = originalLicense.ProductId,
                Tier = originalLicense.Tier,
                Scope = originalLicense.Scope,
                ValidFrom = newValidFrom,
                ValidUntil = request.NewValidUntil,
                Options = options != null ? JsonSerializer.Serialize(options) : null,
                Status = LicenseStatus.Active,
                ParentLicenseId = originalLicense.Id,
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedBy = renewedBy
            };

            var createdLicense = await _licenseStore.CreateAsync(newLicense, renewedBy, cancellationToken);

            // Mark original as renewed
            originalLicense.Status = LicenseStatus.Renewed;
            await _licenseStore.UpdateAsync(originalLicense, cancellationToken);

            // Add events
            await _licenseStore.AddEventAsync(originalLicense.Id, LicenseEventType.Renewed, renewedBy,
                JsonSerializer.Serialize(new { NewLicenseId = createdLicense.Id }), cancellationToken);
            await _licenseStore.AddEventAsync(createdLicense.Id, LicenseEventType.Created, renewedBy,
                JsonSerializer.Serialize(new { RenewedFrom = originalLicense.Id }), cancellationToken);

            _logger.LogInformation(
                "License {OriginalLicenseId} renewed to {NewLicenseId} by {RenewedBy}",
                originalLicense.Id, createdLicense.Id, renewedBy);

            return IssueLicenseResult.Succeeded(createdLicense, tokenResult.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to renew license {LicenseId}", request.LicenseId);
            return IssueLicenseResult.Failed("Failed to renew license", "renewal_failed");
        }
    }

    public async Task<IssueLicenseResult> RevokeLicenseAsync(
        RevokeLicenseRequest request,
        string revokedBy,
        CancellationToken cancellationToken = default)
    {
        var license = await _licenseStore.GetByIdAsync(request.LicenseId, cancellationToken);
        if (license == null)
        {
            return IssueLicenseResult.Failed("License not found", "license_not_found");
        }

        if (license.Status == LicenseStatus.Revoked)
        {
            return IssueLicenseResult.Failed("License is already revoked", "already_revoked");
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return IssueLicenseResult.Failed("Revocation reason is required", "reason_required");
        }

        try
        {
            var revokedLicense = await _licenseStore.RevokeAsync(
                request.LicenseId,
                revokedBy,
                request.Reason,
                cancellationToken);

            _logger.LogInformation(
                "License {LicenseId} revoked by {RevokedBy}: {Reason}",
                request.LicenseId, revokedBy, request.Reason);

            return IssueLicenseResult.Succeeded(revokedLicense, revokedLicense.SignedToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to revoke license {LicenseId}", request.LicenseId);
            return IssueLicenseResult.Failed("Failed to revoke license", "revocation_failed");
        }
    }

    public async Task<IssueLicenseResult> UpgradeLicenseAsync(
        ChangeTierRequest request,
        string upgradedBy,
        CancellationToken cancellationToken = default)
    {
        return await ChangeTierAsync(request, upgradedBy, LicenseStatus.Upgraded, cancellationToken);
    }

    public async Task<IssueLicenseResult> DowngradeLicenseAsync(
        ChangeTierRequest request,
        string downgradedBy,
        CancellationToken cancellationToken = default)
    {
        return await ChangeTierAsync(request, downgradedBy, LicenseStatus.Downgraded, cancellationToken);
    }

    public async Task<License?> GetLicenseAsync(Guid licenseId, CancellationToken cancellationToken = default)
    {
        return await _licenseStore.GetByIdAsync(licenseId, cancellationToken);
    }

    public async Task<IReadOnlyList<License>> GetCustomerLicensesAsync(
        Guid customerId,
        LicenseStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        return await _licenseStore.GetByCustomerIdAsync(customerId, status, cancellationToken);
    }

    private async Task<IssueLicenseResult> ChangeTierAsync(
        ChangeTierRequest request,
        string changedBy,
        LicenseStatus newStatus,
        CancellationToken cancellationToken)
    {
        var originalLicense = await _licenseStore.GetByIdAsync(request.LicenseId, cancellationToken);
        if (originalLicense == null)
        {
            return IssueLicenseResult.Failed("License not found", "license_not_found");
        }

        if (originalLicense.Status == LicenseStatus.Revoked)
        {
            return IssueLicenseResult.Failed("Cannot change tier of a revoked license", "license_revoked");
        }

        if (originalLicense.Tier == request.NewTier)
        {
            return IssueLicenseResult.Failed("New tier is the same as current tier", "same_tier");
        }

        var customer = await _customerStore.GetByIdAsync(originalLicense.CustomerId, cancellationToken);
        var product = await _productStore.GetByIdAsync(originalLicense.ProductId, cancellationToken);

        if (customer == null || product == null)
        {
            return IssueLicenseResult.Failed("Customer or product not found", "reference_not_found");
        }

        // Merge options
        var options = MergeOptions(originalLicense.Options, request.OptionUpdates);

        // Validate options
        var optionValidationErrors = ValidateOptions(options, product.OptionDefinitions);
        if (optionValidationErrors.Count > 0)
        {
            return IssueLicenseResult.Failed(
                "Invalid options provided",
                "invalid_options",
                optionValidationErrors);
        }

        try
        {
            // Generate new token with new tier
            var tokenRequest = new GenerateLicenseTokenRequest
            {
                CustomerIdentifier = customer.Identifier,
                ProductIdentifier = product.Identifier,
                Tier = request.NewTier,
                Scope = originalLicense.Scope,
                ValidFrom = DateTimeOffset.UtcNow,
                ValidUntil = originalLicense.ValidUntil,
                Options = options
            };

            var tokenResult = await _tokenGenerator.GenerateAsync(tokenRequest, cancellationToken);

            // Create new license
            var newLicense = new License
            {
                Id = GuidHelper.NewId(),
                TokenId = tokenResult.TokenId,
                SignedToken = tokenResult.Token,
                SigningKeyId = tokenResult.Kid,
                CustomerId = originalLicense.CustomerId,
                ProductId = originalLicense.ProductId,
                Tier = request.NewTier,
                Scope = originalLicense.Scope,
                ValidFrom = DateTimeOffset.UtcNow,
                ValidUntil = originalLicense.ValidUntil,
                Options = options != null ? JsonSerializer.Serialize(options) : null,
                Status = LicenseStatus.Active,
                ParentLicenseId = originalLicense.Id,
                CreatedAt = DateTimeOffset.UtcNow,
                CreatedBy = changedBy
            };

            var createdLicense = await _licenseStore.CreateAsync(newLicense, changedBy, cancellationToken);

            // Update original license status
            originalLicense.Status = newStatus;
            await _licenseStore.UpdateAsync(originalLicense, cancellationToken);

            // Add events
            var eventType = newStatus == LicenseStatus.Upgraded
                ? LicenseEventType.Upgraded
                : LicenseEventType.Downgraded;

            await _licenseStore.AddEventAsync(originalLicense.Id, eventType, changedBy,
                JsonSerializer.Serialize(new { NewLicenseId = createdLicense.Id, NewTier = request.NewTier }), cancellationToken);
            await _licenseStore.AddEventAsync(createdLicense.Id, LicenseEventType.Created, changedBy,
                JsonSerializer.Serialize(new { ChangedFrom = originalLicense.Id, PreviousTier = originalLicense.Tier }), cancellationToken);

            _logger.LogInformation(
                "License {OriginalLicenseId} {Action} to tier {NewTier} as {NewLicenseId} by {ChangedBy}",
                originalLicense.Id, newStatus.ToString().ToLower(), request.NewTier, createdLicense.Id, changedBy);

            return IssueLicenseResult.Succeeded(createdLicense, tokenResult.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to change tier for license {LicenseId}", request.LicenseId);
            return IssueLicenseResult.Failed("Failed to change license tier", "tier_change_failed");
        }
    }

    private static Dictionary<string, string[]> ValidateOptions(
        Dictionary<string, object>? options,
        ICollection<ProductOptionDefinition>? definitions)
    {
        var errors = new Dictionary<string, string[]>();

        if (options == null || options.Count == 0)
        {
            return errors;
        }

        if (definitions == null || definitions.Count == 0)
        {
            // No definitions means no options are allowed
            errors["options"] = ["Product does not support any options"];
            return errors;
        }

        var definitionLookup = definitions.ToDictionary(d => d.OptionKey, d => d);

        foreach (var (key, value) in options)
        {
            if (!definitionLookup.TryGetValue(key, out var definition))
            {
                errors[key] = [$"Unknown option key: {key}"];
                continue;
            }

            // Validate data type
            var typeError = ValidateOptionType(value, definition.DataType);
            if (typeError != null)
            {
                errors[key] = [typeError];
            }
        }

        return errors;
    }

    private static string? ValidateOptionType(object value, OptionDataType expectedType)
    {
        return expectedType switch
        {
            OptionDataType.String when value is not string && value is not JsonElement { ValueKind: JsonValueKind.String } =>
                "Expected string value",
            OptionDataType.Number when !IsNumericValue(value) =>
                "Expected numeric value",
            OptionDataType.Boolean when value is not bool && value is not JsonElement { ValueKind: JsonValueKind.True or JsonValueKind.False } =>
                "Expected boolean value",
            _ => null
        };
    }

    private static bool IsNumericValue(object value)
    {
        return value is int or long or float or double or decimal
            || (value is JsonElement je && (je.ValueKind == JsonValueKind.Number));
    }

    private static Dictionary<string, object>? MergeOptions(string? originalOptionsJson, Dictionary<string, object>? updates)
    {
        Dictionary<string, object>? original = null;

        if (!string.IsNullOrEmpty(originalOptionsJson))
        {
            try
            {
                original = JsonSerializer.Deserialize<Dictionary<string, object>>(originalOptionsJson);
            }
            catch
            {
                // If we can't deserialize, start fresh
            }
        }

        if (updates == null || updates.Count == 0)
        {
            return original;
        }

        original ??= new Dictionary<string, object>();

        foreach (var (key, value) in updates)
        {
            original[key] = value;
        }

        return original;
    }
}
