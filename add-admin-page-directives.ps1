# Add kebab-case @page directives to Admin Razor Pages
# Usage: .\add-admin-page-directives.ps1

$ErrorActionPreference = "Stop"

# Map of file paths to kebab-case URLs
$pageMap = @{
    "Pages\Admin\Index.cshtml" = "/admin"
    "Pages\Admin\Branding.cshtml" = "/admin/branding"
    "Pages\Admin\Settings.cshtml" = "/admin/settings"
    "Pages\Admin\Clients\Index.cshtml" = "/admin/clients"
    "Pages\Admin\Clients\Add.cshtml" = "/admin/clients/add"
    "Pages\Admin\Clients\Edit.cshtml" = "/admin/clients/edit"
    "Pages\Admin\Scopes\Index.cshtml" = "/admin/scopes"
    "Pages\Admin\Scopes\Add.cshtml" = "/admin/scopes/add"
    "Pages\Admin\Scopes\Edit.cshtml" = "/admin/scopes/edit"
    "Pages\Admin\Roles\Index.cshtml" = "/admin/roles"
    "Pages\Admin\Roles\Add.cshtml" = "/admin/roles/add"
    "Pages\Admin\Roles\Edit.cshtml" = "/admin/roles/edit"
    "Pages\Admin\Users\Index.cshtml" = "/admin/users"
    "Pages\Admin\Users\Add.cshtml" = "/admin/users/add"
    "Pages\Admin\Users\Edit.cshtml" = "/admin/users/edit"
    "Pages\Admin\Users\Roles\Index.cshtml" = "/admin/users/roles"
    "Pages\Admin\Users\Clients\Index.cshtml" = "/admin/users/clients"
    "Pages\Admin\Users\Linked\Index.cshtml" = "/admin/users/linked"
    "Pages\Admin\Users\Emails\Index.cshtml" = "/admin/users/emails"
    "Pages\Admin\Providers\Index.cshtml" = "/admin/providers"
    "Pages\Admin\Providers\Add.cshtml" = "/admin/providers/add"
    "Pages\Admin\Providers\Edit.cshtml" = "/admin/providers/edit"
    "Pages\Admin\Providers\Delete.cshtml" = "/admin/providers/delete"
    "Pages\Admin\Providers\Details.cshtml" = "/admin/providers/details"
    "Pages\Admin\Providers\ClaimMappings.cshtml" = "/admin/providers/claim-mappings"
    "Pages\Admin\ProviderMappings\Index.cshtml" = "/admin/provider-mappings"
    "Pages\Admin\ProviderClaimMappings\Index.cshtml" = "/admin/provider-claim-mappings"
    "Pages\Admin\ProviderClaimMappings\Edit.cshtml" = "/admin/provider-claim-mappings/edit"
    "Pages\Admin\ProviderKeys\Index.cshtml" = "/admin/provider-keys"
    "Pages\Admin\ClientKeys\Index.cshtml" = "/admin/client-keys"
    "Pages\Admin\Realms\Index.cshtml" = "/admin/realms"
    "Pages\Admin\Realms\Add.cshtml" = "/admin/realms/add"
    "Pages\Admin\Realms\Edit.cshtml" = "/admin/realms/edit"
    "Pages\Admin\Registrations\Index.cshtml" = "/admin/registrations"
    "Pages\Admin\Backchannel\Index.cshtml" = "/admin/backchannel"
    "Pages\Admin\License\Index.cshtml" = "/admin/license"
    "Pages\Admin\License\Install.cshtml" = "/admin/license/install"
    "Pages\Admin\License\History.cshtml" = "/admin/license/history"
}

$baseDir = "C:\Users\rum2c\source\repos\MrWhoOidc\MrWhoOidc.WebAuth"
$updated = 0
$skipped = 0
$errors = 0

foreach ($relativePath in $pageMap.Keys) {
    $filePath = Join-Path $baseDir $relativePath
    $kebabUrl = $pageMap[$relativePath]
    
    if (-not (Test-Path $filePath)) {
        Write-Host "⚠️  File not found: $relativePath" -ForegroundColor Yellow
        $skipped++
        continue
    }
    
    try {
        $content = Get-Content $filePath -Raw -Encoding UTF8
        
        # Check if @page directive already exists
        if ($content -match '@page\s+"[^"]*"') {
            Write-Host "⏭️  Already has @page directive: $relativePath" -ForegroundColor Cyan
            $skipped++
            continue
        }
        
        # Check if it's a plain @page (without route)
        if ($content -match '^@page\s*$') {
            # Replace plain @page with kebab-case @page directive
            $newContent = $content -replace '^@page\s*$', "@page `"$kebabUrl`""
            Set-Content -Path $filePath -Value $newContent -Encoding UTF8 -NoNewline
            Write-Host "✅ Updated: $relativePath → $kebabUrl" -ForegroundColor Green
            $updated++
        }
        else {
            # Add @page directive as first line
            $newContent = "@page `"$kebabUrl`"`r`n" + $content
            Set-Content -Path $filePath -Value $newContent -Encoding UTF8 -NoNewline
            Write-Host "✅ Added: $relativePath → $kebabUrl" -ForegroundColor Green
            $updated++
        }
    }
    catch {
        Write-Host "❌ Error processing $relativePath : $_" -ForegroundColor Red
        $errors++
    }
}

Write-Host ""
Write-Host "Summary:" -ForegroundColor Cyan
Write-Host "  Updated: $updated" -ForegroundColor Green
Write-Host "  Skipped: $skipped" -ForegroundColor Yellow
Write-Host "  Errors:  $errors" -ForegroundColor $(if ($errors -gt 0) { "Red" } else { "Green" })
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "  1. Run dotnet build" -ForegroundColor White
Write-Host "  2. Verify no compilation errors" -ForegroundColor White
Write-Host "  3. Update navigation links" -ForegroundColor White
