# Test script to verify icon upload works without PostgreSQL execution strategy errors

Write-Host "Testing tenant icon upload functionality..."

# Create a simple 1x1 PNG image as test data
$pngBytes = [System.Convert]::FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChAGAiQGMLUkAAAAASUVORK5CYII=")

# Test tenant ID from logs
$tenantId = "068f26e1-f2aa-7000-8bcc-9b55a4a5af56"

Write-Host "Attempting to upload icon for tenant: $tenantId"

# Create a multipart form data for the upload
$boundary = [System.Guid]::NewGuid().ToString()
$LF = "`r`n"

$bodyLines = (
    "--$boundary",
    "Content-Disposition: form-data; name=`"file`"; filename=`"test-icon.png`"",
    "Content-Type: image/png$LF",
    [System.Text.Encoding]::GetEncoding("iso-8859-1").GetString($pngBytes),
    "--$boundary--$LF"
) -join $LF

try {
    $response = Invoke-RestMethod -Uri "https://localhost:8443/t/default/admin/api/tenants/$tenantId/icon" `
        -Method Post `
        -ContentType "multipart/form-data; boundary=$boundary" `
        -Body $bodyLines `
        -SkipCertificateCheck
    
    Write-Host "✅ Icon upload successful!" -ForegroundColor Green
    Write-Host "Response: $response"
} catch {
    Write-Host "❌ Icon upload failed: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "Response: $($_.Exception.Response)"
}

Write-Host "`nChecking Docker logs for any execution strategy errors..."
docker logs mrwhooidc-webauth-1 --tail 10 | Select-String -Pattern "execution strategy|Failed to upload icon" | ForEach-Object { 
    if ($_ -match "execution strategy") {
        Write-Host "Error: $($_)" -ForegroundColor Red
    } else {
        Write-Host "Info: $($_)" -ForegroundColor Yellow
    }
}