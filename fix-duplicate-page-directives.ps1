$files = @(
    "MrWhoOidc.WebAuth\Pages\Admin\License\History.cshtml",
    "MrWhoOidc.WebAuth\Pages\Admin\Clients\Add.cshtml",
    "MrWhoOidc.WebAuth\Pages\Admin\License\Index.cshtml",
    "MrWhoOidc.WebAuth\Pages\Admin\Realms\Add.cshtml",
    "MrWhoOidc.WebAuth\Pages\Admin\Roles\Add.cshtml",
    "MrWhoOidc.WebAuth\Pages\Admin\Scopes\Add.cshtml",
    "MrWhoOidc.WebAuth\Pages\Admin\Scopes\Index.cshtml",
    "MrWhoOidc.WebAuth\Pages\Admin\Clients\Index.cshtml",
    "MrWhoOidc.WebAuth\Pages\Admin\ProviderMappings\Index.cshtml",
    "MrWhoOidc.WebAuth\Pages\Admin\Roles\Index.cshtml",
    "MrWhoOidc.WebAuth\Pages\Admin\License\Install.cshtml",
    "MrWhoOidc.WebAuth\Pages\Admin\Providers\Add.cshtml",
    "MrWhoOidc.WebAuth\Pages\Admin\Backchannel\Index.cshtml",
    "MrWhoOidc.WebAuth\Pages\Admin\Providers\Index.cshtml",
    "MrWhoOidc.WebAuth\Pages\Admin\Users\Index.cshtml",
    "MrWhoOidc.WebAuth\Pages\Admin\Users\Add.cshtml",
    "MrWhoOidc.WebAuth\Pages\Admin\Realms\Index.cshtml",
    "MrWhoOidc.WebAuth\Pages\Admin\Registrations\Index.cshtml"
)

$fixed = 0
$errors = 0

foreach ($file in $files) {
    $fullPath = Join-Path $PSScriptRoot $file
    
    if (Test-Path $fullPath) {
        try {
            $content = Get-Content $fullPath -Raw -Encoding UTF8
            
            # Replace first two lines (kebab-case @page + plain @page) with just kebab-case @page
            # Pattern: @page "/admin/..."<newline>@page<newline>
            $pattern = '(@page\s+"[^"]+"\r?\n)@page\r?\n'
            
            if ($content -match $pattern) {
                $newContent = $content -replace $pattern, '$1'
                Set-Content -Path $fullPath -Value $newContent -Encoding UTF8 -NoNewline
                Write-Host "Fixed: $file"
                $fixed++
            } else {
                Write-Host "No duplicate found (skipped): $file"
            }
        }
        catch {
            Write-Host "Error processing $file : $_"
            $errors++
        }
    } else {
        Write-Host "File not found: $file"
        $errors++
    }
}

Write-Host ""
Write-Host "Summary:"
Write-Host "  Fixed: $fixed"
Write-Host "  Errors: $errors"
