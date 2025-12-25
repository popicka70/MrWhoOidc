using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Seeding;
using MrWhoOidc.Auth.Services;
using System.Text.Json;

namespace MrWhoOidc.WebAuth.Pages.Admin.Providers;

[Authorize(Policy = "tenant-admin")]
public class ImportModel(
    ITenantAccessor tenantAccessor,
    IMultiTenancyOptions multiTenancyOptions,
    IConfigurationImportService importService) : TenantAwarePageModel(tenantAccessor, multiTenancyOptions)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    [BindProperty]
    public IFormFile? ManifestFile { get; set; }

    [BindProperty]
    public string? ManifestJson { get; set; }

    [BindProperty]
    public string ConflictResolution { get; set; } = "skip";

    [BindProperty]
    public bool DryRun { get; set; }

    public ImportPreview? Preview { get; private set; }

    public ImportResult? Result { get; private set; }

    public string? ErrorMessage { get; private set; }

    public string? SuccessMessage { get; private set; }

    /// <summary>
    /// Initial page load.
    /// </summary>
    public IActionResult OnGet()
    {
        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            return NotFound();
        }

        return Page();
    }

    /// <summary>
    /// Handles file upload and preview.
    /// </summary>
    public async Task<IActionResult> OnPostPreviewAsync()
    {
        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            return NotFound();
        }

        var manifestContent = await GetManifestContentAsync();
        if (manifestContent == null)
        {
            ErrorMessage = "Please upload a valid JSON manifest file or paste the JSON content.";
            return Page();
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<ExportManifest>(manifestContent, JsonOptions);
            if (manifest == null)
            {
                ErrorMessage = "Invalid manifest format. The JSON could not be parsed.";
                return Page();
            }

            // Validate manifest has identity provider data
            // Check both standalone providers (data.identityProviders) and tenant-nested providers (data.tenants[].identityProviders)
            var standaloneProviderCount = manifest.Data?.IdentityProviders?.Count ?? 0;
            var nestedProviderCount = manifest.Data?.Tenants?.SelectMany(t => t.IdentityProviders ?? []).Count() ?? 0;
            var providerCount = standaloneProviderCount + nestedProviderCount;
            
            if (providerCount == 0)
            {
                ErrorMessage = "The manifest does not contain any identity provider configurations.";
                return Page();
            }

            var options = new ImportOptions
            {
                ValidateOnly = true,
                DefaultConflictResolution = ParseConflictResolutionEnum(ConflictResolution)
            };

            Preview = await importService.PreviewImportAsync(manifest, options);
            ManifestJson = manifestContent;

            if (!Preview.IsValid)
            {
                ErrorMessage = $"Validation failed with {Preview.ValidationErrors.Count} error(s).";
            }
            else if (Preview.Conflicts.Count > 0)
            {
                ErrorMessage = $"Found {Preview.Conflicts.Count} conflict(s) that require resolution.";
            }

            return Page();
        }
        catch (JsonException ex)
        {
            ErrorMessage = $"Invalid JSON format: {ex.Message}";
            return Page();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Preview failed: {ex.Message}";
            return Page();
        }
    }

    /// <summary>
    /// Handles the import execution.
    /// </summary>
    public async Task<IActionResult> OnPostImportAsync()
    {
        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            return NotFound();
        }

        var manifestContent = await GetManifestContentAsync();
        if (manifestContent == null)
        {
            ErrorMessage = "Please upload a valid JSON manifest file or paste the JSON content.";
            return Page();
        }

        try
        {
            var manifest = JsonSerializer.Deserialize<ExportManifest>(manifestContent, JsonOptions);
            if (manifest == null)
            {
                ErrorMessage = "Invalid manifest format. The JSON could not be parsed.";
                return Page();
            }

            var options = new ImportOptions
            {
                ValidateOnly = DryRun,
                DefaultConflictResolution = ParseConflictResolutionEnum(ConflictResolution),
                TargetTenantId = currentTenantId.Value,
                ImportedBy = User.Identity?.Name ?? "anonymous",
                IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
                UserAgent = Request.Headers.UserAgent.ToString()
            };

            // First do a preview to ensure everything is valid
            Preview = await importService.PreviewImportAsync(manifest, options);

            if (!Preview.IsValid)
            {
                ErrorMessage = $"Import aborted: {Preview.ValidationErrors.Count} validation error(s) found.";
                ManifestJson = manifestContent;
                return Page();
            }

            // Execute the import for identity providers
            Result = await importService.ImportIdentityProviderAsync(manifest, options);
            ManifestJson = manifestContent;

            if (Result.Success)
            {
                if (DryRun)
                {
                    SuccessMessage = $"Dry run completed successfully. Would create {Result.EntitiesCreated} provider(s), update {Result.EntitiesUpdated}, skip {Result.EntitiesSkipped}.";
                }
                else
                {
                    SuccessMessage = $"Import completed successfully. Created {Result.EntitiesCreated} provider(s), updated {Result.EntitiesUpdated}, skipped {Result.EntitiesSkipped}.";
                }
            }
            else
            {
                ErrorMessage = Result.ErrorMessage ?? "Import failed. See errors below.";
            }

            return Page();
        }
        catch (NotImplementedException)
        {
            ErrorMessage = "Identity provider import is not yet fully implemented. Please use tenant-level import to include providers.";
            ManifestJson = manifestContent;
            return Page();
        }
        catch (JsonException ex)
        {
            ErrorMessage = $"Invalid JSON format: {ex.Message}";
            return Page();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Import failed: {ex.Message}";
            return Page();
        }
    }

    private async Task<string?> GetManifestContentAsync()
    {
        // If ManifestJson is provided directly, use it
        if (!string.IsNullOrWhiteSpace(ManifestJson))
        {
            return ManifestJson;
        }

        // Otherwise, try to read from uploaded file
        if (ManifestFile != null && ManifestFile.Length > 0)
        {
            using var reader = new StreamReader(ManifestFile.OpenReadStream());
            return await reader.ReadToEndAsync();
        }

        return null;
    }

    private static ConflictResolution ParseConflictResolutionEnum(string? value)
    {
        return value?.ToLowerInvariant() switch
        {
            "skip" => MrWhoOidc.Auth.Seeding.ConflictResolution.Skip,
            "rename" => MrWhoOidc.Auth.Seeding.ConflictResolution.Rename,
            "merge" => MrWhoOidc.Auth.Seeding.ConflictResolution.Merge,
            "overwrite" => MrWhoOidc.Auth.Seeding.ConflictResolution.Overwrite,
            _ => MrWhoOidc.Auth.Seeding.ConflictResolution.Skip
        };
    }
}
