# Generate ECDSA P-256 private key for license signing
$ecdsa = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve]::NamedCurves.nistP256)
$privateKey = $ecdsa.ExportECPrivateKeyPem()
[System.IO.File]::WriteAllText("$PSScriptRoot\secrets\licensing-private-key.pem", $privateKey)
Write-Host "✅ Generated licensing-private-key.pem" -ForegroundColor Green
