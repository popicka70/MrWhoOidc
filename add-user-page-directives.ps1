$userPages = @{
    # Auth pages
    "MrWhoOidc.WebAuth\Pages\Auth\Qr.cshtml" = "/auth/qr"
    "MrWhoOidc.WebAuth\Pages\Auth\QrConfirm.cshtml" = "/auth/qr-confirm"
    "MrWhoOidc.WebAuth\Pages\Auth\QrMobile.cshtml" = "/auth/qr-mobile"
    "MrWhoOidc.WebAuth\Pages\Auth\WebAuthn.cshtml" = "/auth/webauthn"
    "MrWhoOidc.WebAuth\Pages\Auth\Providers\Select.cshtml" = "/auth/providers/select"
    
    # Account pages
    "MrWhoOidc.WebAuth\Pages\Account\Index.cshtml" = "/account"
    "MrWhoOidc.WebAuth\Pages\Account\Profile.cshtml" = "/account/profile"
    "MrWhoOidc.WebAuth\Pages\Account\Emails.cshtml" = "/account/emails"
    "MrWhoOidc.WebAuth\Pages\Account\LinkedAccounts.cshtml" = "/account/linked-accounts"
    "MrWhoOidc.WebAuth\Pages\Account\Sessions.cshtml" = "/account/sessions"
    "MrWhoOidc.WebAuth\Pages\Account\Consents.cshtml" = "/account/consents"
    "MrWhoOidc.WebAuth\Pages\Account\WebAuthn.cshtml" = "/account/webauthn"
    "MrWhoOidc.WebAuth\Pages\Account\AccessDenied.cshtml" = "/account/access-denied"
    "MrWhoOidc.WebAuth\Pages\Account\ConfirmEmail.cshtml" = "/account/confirm-email"
    
    # Mfa pages
    "MrWhoOidc.WebAuth\Pages\Mfa\Index.cshtml" = "/mfa"
    
    # Password pages
    "MrWhoOidc.WebAuth\Pages\Password\Index.cshtml" = "/password"
    
    # Logout pages
    "MrWhoOidc.WebAuth\Pages\Logout\Prompt\Index.cshtml" = "/logout/prompt"
    "MrWhoOidc.WebAuth\Pages\Logout\FederatedSignedOut.cshtml" = "/logout/federated-signed-out"
    "MrWhoOidc.WebAuth\Pages\Logout\FederatedCallbackError.cshtml" = "/logout/federated-callback-error"
}

$updated = 0
$skipped = 0
$errors = 0

foreach ($page in $userPages.GetEnumerator()) {
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
