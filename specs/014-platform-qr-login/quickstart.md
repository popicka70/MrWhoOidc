# Quickstart: Platform QR Login at DiscoverTenant

**Feature**: 014-platform-qr-login  
**Date**: 2025-12-26

## Overview

This guide provides a fast path to implementing platform QR login at the DiscoverTenant page.

## Prerequisites

- .NET 9 SDK installed
- PostgreSQL running (via Aspire or Docker)
- Existing QR login infrastructure working (`QrLogin:Enabled: true` in appsettings)
- Platform admin user exists in default tenant

## Implementation Steps

### Step 1: Create PlatformSettings Entity

**File**: `MrWhoOidc.Auth/Persistence/PlatformSettings.cs`

```csharp
using System.ComponentModel.DataAnnotations;

namespace MrWhoOidc.Auth.Persistence;

/// <summary>
/// System-wide platform settings that apply across all tenants.
/// Single-row table pattern.
/// </summary>
public class PlatformSettings
{
    public Guid Id { get; set; } = GuidHelper.NewId();

    /// <summary>
    /// Enable QR login option on the /DiscoverTenant page.
    /// </summary>
    public bool QrLoginAtDiscoveryEnabled { get; set; } = false;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [MaxLength(256)]
    public string? UpdatedBy { get; set; }
}
```

### Step 2: Add DbSet to AuthDbContext

**File**: `MrWhoOidc.Auth/Persistence/AuthDbContext.cs`

```csharp
// Add near other DbSet declarations
public DbSet<PlatformSettings> PlatformSettings => Set<PlatformSettings>();
```

### Step 3: Generate EF Migration

```bash
dotnet ef migrations add AddPlatformSettings \
  --project MrWhoOidc.Auth \
  --startup-project MrWhoOidc.WebAuth \
  --output-dir Persistence/Migrations
```

### Step 4: Create IPlatformSettingsService

**File**: `MrWhoOidc.Auth/Services/IPlatformSettingsService.cs`

```csharp
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

public interface IPlatformSettingsService
{
    Task<PlatformSettings> GetSettingsAsync();
    Task UpdateSettingsAsync(PlatformSettings settings, string? updatedBy);
    Task<bool> IsQrLoginAtDiscoveryEnabledAsync();
}
```

### Step 5: Create PlatformSettingsService

**File**: `MrWhoOidc.Auth/Services/PlatformSettingsService.cs`

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

public class PlatformSettingsService : IPlatformSettingsService
{
    private readonly AuthDbContext _db;
    private readonly HybridCache _cache;
    private const string CacheKey = "platform:settings";

    public PlatformSettingsService(AuthDbContext db, HybridCache cache)
    {
        _db = db;
        _cache = cache;
    }

    public async Task<PlatformSettings> GetSettingsAsync()
    {
        return await _cache.GetOrCreateAsync(
            CacheKey,
            async cancel =>
            {
                var settings = await _db.PlatformSettings.FirstOrDefaultAsync(cancel);
                if (settings == null)
                {
                    settings = new PlatformSettings();
                    _db.PlatformSettings.Add(settings);
                    await _db.SaveChangesAsync(cancel);
                }
                return settings;
            },
            new HybridCacheEntryOptions
            {
                Expiration = TimeSpan.FromHours(1),
                LocalCacheExpiration = TimeSpan.FromMinutes(15)
            },
            ["platform-settings"]
        );
    }

    public async Task UpdateSettingsAsync(PlatformSettings settings, string? updatedBy)
    {
        settings.UpdatedAt = DateTimeOffset.UtcNow;
        settings.UpdatedBy = updatedBy;
        _db.PlatformSettings.Update(settings);
        await _db.SaveChangesAsync();
        await _cache.RemoveAsync(CacheKey);
    }

    public async Task<bool> IsQrLoginAtDiscoveryEnabledAsync()
    {
        var settings = await GetSettingsAsync();
        return settings.QrLoginAtDiscoveryEnabled;
    }
}
```

### Step 6: Register Service in DI

**File**: `MrWhoOidc.WebAuth/Program.cs`

```csharp
// Add near other service registrations
builder.Services.AddScoped<IPlatformSettingsService, PlatformSettingsService>();
```

### Step 7: Create Platform Settings Page

**File**: `MrWhoOidc.WebAuth/Pages/PlatformAdmin/Settings.cshtml.cs`

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Security.Admin;

namespace MrWhoOidc.WebAuth.Pages.PlatformAdmin;

[Authorize(Policy = "platform-admin")]
[RequireDefaultTenantContext]
public class SettingsModel : PageModel
{
    private readonly IPlatformSettingsService _settingsService;

    public SettingsModel(IPlatformSettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    [BindProperty]
    public bool QrLoginAtDiscoveryEnabled { get; set; }

    public string? SuccessMessage { get; set; }

    public async Task OnGetAsync()
    {
        var settings = await _settingsService.GetSettingsAsync();
        QrLoginAtDiscoveryEnabled = settings.QrLoginAtDiscoveryEnabled;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var settings = await _settingsService.GetSettingsAsync();
        settings.QrLoginAtDiscoveryEnabled = QrLoginAtDiscoveryEnabled;
        await _settingsService.UpdateSettingsAsync(settings, User.Identity?.Name);
        
        TempData["SuccessMessage"] = "Platform settings saved successfully.";
        return RedirectToPage();
    }
}
```

**File**: `MrWhoOidc.WebAuth/Pages/PlatformAdmin/Settings.cshtml`

```html
@page "/platform-admin/settings"
@model MrWhoOidc.WebAuth.Pages.PlatformAdmin.SettingsModel
@{
    ViewData["Title"] = "Platform Settings";
    Layout = "_AdminLayout";
}

<div class="page-header">
    <h1><i class="bi bi-gear me-2"></i>Platform Settings</h1>
    <p class="text-muted">System-wide settings that apply across all tenants</p>
</div>

@if (TempData["SuccessMessage"] != null)
{
    <div class="alert alert-success">
        <i class="bi bi-check-circle me-2"></i>@TempData["SuccessMessage"]
    </div>
}

<div class="card">
    <div class="card-header">
        <h5 class="mb-0">Authentication Options</h5>
    </div>
    <div class="card-body">
        <form method="post">
            <div class="form-check form-switch mb-3">
                <input class="form-check-input" type="checkbox" 
                       asp-for="QrLoginAtDiscoveryEnabled" id="qrLoginToggle">
                <label class="form-check-label" for="qrLoginToggle">
                    Enable QR Login at Discovery Page
                </label>
                <div class="form-text">
                    When enabled, users can sign in by scanning a QR code on the 
                    /DiscoverTenant page before selecting their organization.
                </div>
            </div>
            
            <button type="submit" class="btn btn-primary">
                <i class="bi bi-save me-2"></i>Save Settings
            </button>
        </form>
    </div>
</div>
```

### Step 8: Update DiscoverTenant Page

**File**: `MrWhoOidc.WebAuth/Pages/DiscoverTenant.cshtml.cs`

Add to constructor:

```csharp
private readonly IPlatformSettingsService _platformSettings;

// Add to constructor parameters and assign
```

Add property:

```csharp
public bool ShowQrLogin { get; set; }
```

Add to OnGetAsync:

```csharp
ShowQrLogin = await _platformSettings.IsQrLoginAtDiscoveryEnabledAsync();
```

**File**: `MrWhoOidc.WebAuth/Pages/DiscoverTenant.cshtml`

Add QR login button (after external IdP list, before email form):

```html
@if (Model.ShowQrLogin)
{
    <div class="mb-4">
        @{
            var qrReturnUrl = Model.ReturnUrl ?? "/";
            var qrLoginUrl = $"/auth/qr?returnUrl={Uri.EscapeDataString(qrReturnUrl)}";
        }
        <a href="@qrLoginUrl" class="btn btn-outline-primary w-100 d-flex align-items-center justify-content-center">
            <i class="bi bi-qr-code me-2" style="font-size: 1.25rem;"></i>
            Sign in with QR Code
        </a>
    </div>
    
    <div class="d-flex align-items-center my-3">
        <hr class="flex-grow-1" />
        <span class="mx-3 text-muted small">or</span>
        <hr class="flex-grow-1" />
    </div>
}
```

### Step 9: Add Navigation Link

**File**: `MrWhoOidc.WebAuth/Pages/Shared/_AdminLayout.cshtml`

Add under PlatformAdmin nav section:

```html
<a class="nav-link" asp-page="/PlatformAdmin/Settings">
    <i class="bi bi-gear me-2"></i>Platform Settings
</a>
```

## Testing

### Manual Testing

1. Run Aspire host: `dotnet run --project MrWhoOidc.AppHost`
2. Navigate to `https://localhost:8443/platform-admin/settings`
3. Login as platform admin
4. Toggle "Enable QR Login at Discovery Page" ON
5. Save settings
6. Navigate to `https://localhost:8443/DiscoverTenant`
7. Verify QR login button appears
8. Click QR login, verify QR code displays
9. Toggle setting OFF, verify button disappears

### Unit Test Example

```csharp
[TestClass]
public class PlatformSettingsServiceTests
{
    [TestMethod]
    public async Task GetSettingsAsync_CreatesDefault_WhenNotExists()
    {
        // Arrange
        using var db = CreateTestDbContext();
        var cache = CreateTestCache();
        var service = new PlatformSettingsService(db, cache);

        // Act
        var settings = await service.GetSettingsAsync();

        // Assert
        Assert.IsNotNull(settings);
        Assert.IsFalse(settings.QrLoginAtDiscoveryEnabled);
    }
}
```

## Verification Checklist

- [ ] Migration applied successfully
- [ ] Platform Settings page accessible to platform admins only
- [ ] QR login toggle persists correctly
- [ ] DiscoverTenant shows QR button when enabled
- [ ] DiscoverTenant hides QR button when disabled
- [ ] QR login flow works from DiscoverTenant
- [ ] Settings changes take effect immediately (cache invalidated)
