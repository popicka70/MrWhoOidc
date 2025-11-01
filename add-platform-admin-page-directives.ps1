$platformAdminPages = @{
    "MrWhoOidc.WebAuth\Pages\PlatformAdmin\Index.cshtml" = "/platform-admin"
    "MrWhoOidc.WebAuth\Pages\PlatformAdmin\Impersonation.cshtml" = "/platform-admin/impersonation"
    "MrWhoOidc.WebAuth\Pages\PlatformAdmin\Tenants\Index.cshtml" = "/platform-admin/tenants"
    "MrWhoOidc.WebAuth\Pages\PlatformAdmin\Tenants\Edit.cshtml" = "/platform-admin/tenants/edit"
    "MrWhoOidc.WebAuth\Pages\PlatformAdmin\Tenants\Create.cshtml" = "/platform-admin/tenants/create"
    "MrWhoOidc.WebAuth\Pages\PlatformAdmin\ImpersonationHistory\Index.cshtml" = "/platform-admin/impersonation-history"
}

$updated = 0
$skipped = 0
$errors = 0

foreach ($page in $platformAdminPages.GetEnumerator()) {
    $filePath = Join-Path $PSScriptRoot $page.Key
    $route = $page.Value
    
    if (Test-Path $filePath) {
        try {
            $content = Get-Content $filePath -Raw -Encoding UTF8
            
            # Check if already has @page directive with route
            if ($content -match '@page\s+"[^"]+"') {
                Write-Host "Already has @page directive: $($page.Key)"
                $skipped++
            }
            # Check if has plain @page (replace it)
            elseif ($content -match '^@page\r?\n') {
                $newContent = $content -replace '^@page\r?\n', "@page `"$route`"`r`n"
                Set-Content -Path $filePath -Value $newContent -Encoding UTF8 -NoNewline
                Write-Host "Updated: $($page.Key) -> $route"
                $updated++
            }
            # No @page directive (add it at the top)
            else {
                $newContent = "@page `"$route`"`r`n" + $content
                Set-Content -Path $filePath -Value $newContent -Encoding UTF8 -NoNewline
                Write-Host "Added: $($page.Key) -> $route"
                $updated++
            }
        }
        catch {
            Write-Host "Error processing $($page.Key): $_"
            $errors++
        }
    } else {
        Write-Host "File not found: $($page.Key)"
        $errors++
    }
}

Write-Host ""
Write-Host "Summary:"
Write-Host "  Updated: $updated"
Write-Host "  Skipped: $skipped"
Write-Host "  Errors: $errors"
