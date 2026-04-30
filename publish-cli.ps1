# Publishes MrWhoOidc.Cli to NuGet.org
# Usage: .\publish-cli.ps1
# The API key is read from $env:NUGET_API_KEY if set, otherwise prompted securely.

$ErrorActionPreference = "Stop"

# Resolve API key
if ($env:NUGET_API_KEY) {
    $apiKey = $env:NUGET_API_KEY
} else {
    $secure = Read-Host "NuGet API key" -AsSecureString
    $apiKey = [System.Net.NetworkCredential]::new("", $secure).Password
}

# Pack
Write-Host "Packing..."
dotnet pack MrWhoOidc.Cli/MrWhoOidc.Cli.csproj --configuration Release --output ./nupkg
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Find the freshly packed file
$package = Get-ChildItem ./nupkg/MrWhoOidc.Cli.*.nupkg | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host "Pushing $($package.Name)..."

dotnet nuget push $package.FullName --api-key $apiKey --source https://api.nuget.org/v3/index.json
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Done. Package published: $($package.Name)"
