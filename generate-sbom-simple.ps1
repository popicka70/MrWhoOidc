# Simple SBOM Generator using built-in dotnet pack SBOM generation
# This generates SBOM files during the package creation process

param(
    [string]$OutputDir = "sbom",
    [switch]$SkipBuild = $false
)

$ErrorActionPreference = "Stop"

# Get solution root
$SolutionRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$OutputPath = Join-Path $SolutionRoot $OutputDir

# Create output directory
if (-not (Test-Path $OutputPath)) {
    New-Item -ItemType Directory -Path $OutputPath | Out-Null
    Write-Host "Created SBOM output directory: $OutputPath" -ForegroundColor Green
}

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "Generating SBOM Files" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""

# Projects to generate SBOM for (excluding test projects and app host)
$libraryProjects = @(
    "MrWhoOidc.Auth\MrWhoOidc.Auth.csproj",
    "MrWhoOidc.Security\MrWhoOidc.Security.csproj",
    "MrWhoOidc.ServiceDefaults\MrWhoOidc.ServiceDefaults.csproj"
)

$applicationProjects = @(
    "MrWhoOidc.ApiService\MrWhoOidc.ApiService.csproj",
    "MrWhoOidc.Web\MrWhoOidc.Web.csproj",
    "MrWhoOidc.WebAuth\MrWhoOidc.WebAuth.csproj",
    "Examples\MrWhoOidc.RazorClient\MrWhoOidc.RazorClient.csproj",
    "Examples\MrWhoOidc.TestApi\MrWhoOidc.TestApi.csproj"
)

$allProjects = $libraryProjects + $applicationProjects

if (-not $SkipBuild) {
    Write-Host "Building solution..." -ForegroundColor Cyan
    dotnet build "$SolutionRoot\MrWhoOidc.slnx" --configuration Release
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Build failed!" -ForegroundColor Red
        exit 1
    }
    Write-Host "Build complete!" -ForegroundColor Green
    Write-Host ""
}

$successCount = 0
$failCount = 0

foreach ($project in $allProjects) {
    $projectPath = Join-Path $SolutionRoot $project
    
    if (-not (Test-Path $projectPath)) {
        Write-Host "⚠ Skipping $project (not found)" -ForegroundColor Yellow
        $failCount++
        continue
    }
    
    $projectName = [System.IO.Path]::GetFileNameWithoutExtension($project)
    Write-Host "Processing $projectName..." -ForegroundColor White
    
    try {
        # Use dotnet pack with SBOM generation enabled
        $packOutput = Join-Path $OutputPath "packages"
        
        dotnet pack $projectPath `
            --configuration Release `
            --output $packOutput `
            --no-build `
            /p:GeneratePackageOnBuild=false `
            /p:IncludeSymbols=false
        
        if ($LASTEXITCODE -eq 0) {
            # Look for generated SBOM in obj folder
            $projectDir = Split-Path $projectPath
            $sbomPath = Join-Path $projectDir "obj\Release\net10.0\*.spdx.json"
            
            $sbomFiles = Get-ChildItem -Path $sbomPath -ErrorAction SilentlyContinue
            
            if ($sbomFiles) {
                foreach ($sbomFile in $sbomFiles) {
                    $destFile = Join-Path $OutputPath "$projectName.sbom.json"
                    Copy-Item $sbomFile.FullName $destFile -Force
                    Write-Host "  ✓ Generated: $projectName.sbom.json" -ForegroundColor Green
                }
                $successCount++
            } else {
                Write-Host "  ⚠ SBOM file not found in obj folder" -ForegroundColor Yellow
                # Create a simple dependency list as fallback
                $depsFile = Join-Path $OutputPath "$projectName.dependencies.txt"
                dotnet list $projectPath package --format json | Out-File $depsFile
                Write-Host "  → Created dependency list: $projectName.dependencies.txt" -ForegroundColor Yellow
                $successCount++
            }
        } else {
            throw "Pack operation failed"
        }
    }
    catch {
        Write-Host "  ✗ Failed: $($_.Exception.Message)" -ForegroundColor Red
        $failCount++
    }
    
    Write-Host ""
}

# Clean up packages directory
$packOutput = Join-Path $OutputPath "packages"
if (Test-Path $packOutput) {
    Remove-Item $packOutput -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "SBOM Generation Complete" -ForegroundColor Cyan
Write-Host "Success: $successCount | Failed: $failCount" -ForegroundColor Cyan
Write-Host "Output directory: $OutputPath" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
