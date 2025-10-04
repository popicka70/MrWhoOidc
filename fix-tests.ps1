$testDir = "c:\Users\rum2c\source\repos\MrWhoOidc\MrWhoOidc.UnitTests"
$filesToFix = @(
    "JwtServiceTests.cs",
    "KeyStoreTests.cs",
    "LogoutHandlerTests.cs",
    "LogoutPromptFlowTests.cs",
    "MultiRealmRoleTests.cs",
    "RefreshTokenRevocationTests.cs",
    "RefreshTokenServiceTests.cs",
    "RevocationServiceTests.cs",
    "SecurityBoundaryTests.cs",
    "SeedUsageExamples.cs",
    "TokenExchangePolicyTests.cs",
    "TokenExchangeTests.cs",
    "TokenRoleEmissionTests.cs",
    "TokenServiceTests.cs",
    "TokenValidatorTests.cs"
)

$fixed = 0
foreach ($fileName in $filesToFix) {
    $filePath = Join-Path $testDir $fileName
    if (-not (Test-Path $filePath)) { continue }
    
    $content = Get-Content $filePath -Raw
    $originalContent = $content
    
    # Add using statement if not present
    if ($content -notmatch 'using MrWhoOidc\.UnitTests\.Helpers') {
        $content = $content -replace '(using .*?;\s+)(namespace )', "`$1using MrWhoOidc.UnitTests.Helpers;`n`n`$2"
    }
    
    # Pattern 1: new KeyStore(db) -> add tenantAccessor
    $content = $content -replace '([ \t]*)var (ks|keyStore) = new KeyStore\(db\);',  "`$1var `$2 = new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant());"
    
    # Pattern 2: new KeyStore(db)) for inline usage
    $content = $content -replace 'new KeyStore\(db\)\)', 'new KeyStore(db, MockTenantAccessor.CreateWithDefaultTenant()))'
    
    # Pattern 3: new RefreshTokenService(db)
    $content = $content -replace '([ \t]*)var (refresh|service|rtSvc) = new RefreshTokenService\(db\);', "`$1var `$2 = new RefreshTokenService(db, MockTenantAccessor.CreateWithDefaultTenant());"
    $content = $content -replace 'new RefreshTokenService\(db\)', 'new RefreshTokenService(db, MockTenantAccessor.CreateWithDefaultTenant())'
    
    # Pattern 4: new RevocationService(db)
    $content = $content -replace '([ \t]*)var (service|svc|revocationService) = new RevocationService\(db\);', "`$1var `$2 = new RevocationService(db, MockTenantAccessor.CreateWithDefaultTenant());"
    
    # Pattern 5: new AuthorizationCodeService(db, meta)
    $content = $content -replace '([ \t]*)var (svc|codeSvc|acSvc) = new AuthorizationCodeService\(db, meta\);', "`$1var `$2 = new AuthorizationCodeService(db, meta, MockTenantAccessor.CreateWithDefaultTenant());"
    
    if ($content -ne $originalContent) {
        Set-Content -Path $filePath -Value $content -NoNewline
        $fixed++
        Write-Host "Fixed: $fileName"
    }
}

Write-Host "`nTotal files fixed: $fixed"
