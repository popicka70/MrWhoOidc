# Generate SBOM (Software Bill of Materials) for all projects in the solution
# Uses CycloneDX format via dotnet CLI

param(
    [string]$OutputDir = "sbom",
    [string]$Format = "json" # json or xml
)

$ErrorActionPreference = "Stop"

# Get solution root
$SolutionRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$OutputPath = Join-Path $SolutionRoot $OutputDir

# Create output directory if it doesn't exist
if (-not (Test-Path $OutputPath)) {
    New-Item -ItemType Directory -Path $OutputPath | Out-Null
    Write-Host "Created SBOM output directory: $OutputPath" -ForegroundColor Green
}

# List of projects to generate SBOM for
$projects = @(
    "MrWhoOidc.ApiService\MrWhoOidc.ApiService.csproj",
    "MrWhoOidc.AppHost\MrWhoOidc.AppHost.csproj",
    "MrWhoOidc.Auth\MrWhoOidc.Auth.csproj",
    "MrWhoOidc.Client\MrWhoOidc.Client.csproj",
    "MrWhoOidc.Security\MrWhoOidc.Security.csproj",
    "MrWhoOidc.ServiceDefaults\MrWhoOidc.ServiceDefaults.csproj",
    "MrWhoOidc.UnitTests\MrWhoOidc.UnitTests.csproj",
    "MrWhoOidc.Web\MrWhoOidc.Web.csproj",
    "MrWhoOidc.WebAuth\MrWhoOidc.WebAuth.csproj",
    "Examples\MrWhoOidc.RazorClient\MrWhoOidc.RazorClient.csproj",
    "Examples\MrWhoOidc.TestApi\MrWhoOidc.TestApi.csproj"
)

Write-Host "Generating SBOM files for $($projects.Count) projects..." -ForegroundColor Cyan
Write-Host "Format: $Format" -ForegroundColor Cyan
Write-Host ""

$successCount = 0
$failCount = 0

foreach ($project in $projects) {
    $projectPath = Join-Path $SolutionRoot $project
    
    if (-not (Test-Path $projectPath)) {
        Write-Host "⚠ Skipping $project (not found)" -ForegroundColor Yellow
        $failCount++
        continue
    }
    
    $projectName = [System.IO.Path]::GetFileNameWithoutExtension($project)
    $sbomFileName = "$projectName.sbom.$Format"
    $sbomFilePath = Join-Path $OutputPath $sbomFileName
    
    Write-Host "Generating SBOM for $projectName..." -ForegroundColor White
    
    try {
        # Generate SBOM using dotnet sbom tool
        # Note: This requires the Microsoft.Sbom.DotNetTool to be installed
        # Install with: dotnet tool install --global Microsoft.Sbom.DotNetTool
        
        # Build the project first to ensure all dependencies are restored
        dotnet build $projectPath --configuration Release --no-incremental --verbosity quiet
        
        if ($LASTEXITCODE -ne 0) {
            throw "Build failed for $projectName"
        }
        
        # Generate SBOM using the Microsoft SBOM tool
        $buildOutputPath = Join-Path (Split-Path $projectPath) "bin\Release\net9.0"
        
        dotnet sbom-tool generate `
            -b $buildOutputPath `
            -bc $projectPath `
            -pn $projectName `
            -pv "1.0.0" `
            -ps "MrWhoOidc" `
            -nsb "https://mrwhooidc.dev" `
            -m $OutputPath `
            -pm Sbom `
            -v Warning
        
        if ($LASTEXITCODE -eq 0) {
            # Move/rename the generated SBOM to a cleaner name
            $generatedSbomPath = Join-Path $OutputPath "_manifest\spdx_2.2\manifest.spdx.json"
            if (Test-Path $generatedSbomPath) {
                Move-Item $generatedSbomPath $sbomFilePath -Force
                # Clean up the _manifest directory
                $manifestDir = Join-Path $OutputPath "_manifest"
                if (Test-Path $manifestDir) {
                    Remove-Item $manifestDir -Recurse -Force
                }
            }
            
            Write-Host "  ✓ Generated: $sbomFileName" -ForegroundColor Green
            $successCount++
        } else {
            throw "SBOM generation failed"
        }
    }
    catch {
        Write-Host "  ✗ Failed to generate SBOM for $projectName" -ForegroundColor Red
        Write-Host "    Error: $($_.Exception.Message)" -ForegroundColor Red
        $failCount++
    }
    
    Write-Host ""
}

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "SBOM Generation Complete" -ForegroundColor Cyan
Write-Host "Success: $successCount | Failed: $failCount" -ForegroundColor Cyan
Write-Host "Output directory: $OutputPath" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan

if ($failCount -gt 0) {
    Write-Host ""
    Write-Host "Note: If you see errors about 'dotnet sbom-tool', install it with:" -ForegroundColor Yellow
    Write-Host "  dotnet tool install --global Microsoft.Sbom.DotNetTool" -ForegroundColor Yellow
}
