# Test script to upload a tenant icon and verify login page display

Write-Host "Testing tenant icon upload and login page display..." -ForegroundColor Green

# Create a simple 100x100 PNG image as test data (blue square)
$pngBase64 = @"
iVBORw0KGgoAAAANSUhEUgAAAGQAAABkCAYAAABw4pVUAAAABHNCSVQICAgIfAhkiAAAAAlwSFlzAAAAdgAAAHYBTnsmCAAAABl0RVh0U29mdHdhcmUAd3d3Lmlua3NjYXBlLm9yZ5vuPBoAAAFFSURBVHic7doxAQAACMOg+TdtYQmBIE27qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqpX7VeQAAlL2jfwAAAABJRU5ErkJggg==
"@

try {
    # Convert base64 to bytes
    $pngBytes = [System.Convert]::FromBase64String($pngBase64)
    
    Write-Host "PNG data size: $($pngBytes.Length) bytes" -ForegroundColor Blue

    # Test tenant ID from logs
    $tenantId = "068f26e1-f2aa-7000-8bcc-9b55a4a5af56"

    Write-Host "Uploading icon for tenant: $tenantId" -ForegroundColor Yellow

    # Create multipart form data for file upload
    $boundary = [System.Guid]::NewGuid().ToString()
    $contentType = "multipart/form-data; boundary=$boundary"
    
    $bodyLines = @()
    $bodyLines += "--$boundary"
    $bodyLines += 'Content-Disposition: form-data; name="file"; filename="test-icon.png"'
    $bodyLines += "Content-Type: image/png"
    $bodyLines += ""
    # Add the binary data
    $bodyLines += [System.Text.Encoding]::GetEncoding("iso-8859-1").GetString($pngBytes)
    $bodyLines += "--$boundary--"
    
    $body = $bodyLines -join "`r`n"
    
    Write-Host "Attempting upload..." -ForegroundColor Yellow
    
    $response = Invoke-WebRequest -Uri "https://localhost:8443/t/default/admin/api/tenants/$tenantId/icon" `
        -Method Post `
        -ContentType $contentType `
        -Body $body `
        -SkipCertificateCheck:$false `
        -ErrorAction SilentlyContinue
    
    if ($response.StatusCode -eq 200) {
        Write-Host "Success: Icon upload successful!" -ForegroundColor Green
        Write-Host "Response: $($response.Content)"
        
        Write-Host "Testing login page..." -ForegroundColor Green
        Write-Host "Please check: https://localhost:8443/t/default/login"
        Write-Host "Expected: Tenant icon should be displayed instead of the default login icon"
        
    } else {
        Write-Host "Error: Icon upload failed with status: $($response.StatusCode)" -ForegroundColor Red
        Write-Host "Response: $($response.Content)"
    }
} catch {
    Write-Host "Error during icon upload: $($_.Exception.Message)" -ForegroundColor Red
    if ($_.Exception.Response) {
        $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
        Write-Host "Response: $($reader.ReadToEnd())" -ForegroundColor Red
    }
}

Write-Host "Checking Docker logs for any errors..." -ForegroundColor Blue
$logs = docker logs mrwhooidc-webauth-1 --tail 10 2>&1
if ($logs -match "error|exception|fail") {
    Write-Host "Warning: Found potential issues in logs:" -ForegroundColor Yellow
    $logs | Select-String -Pattern "error|exception|fail" -CaseSensitive:$false | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
} else {
    Write-Host "Success: No errors found in recent logs" -ForegroundColor Green
}