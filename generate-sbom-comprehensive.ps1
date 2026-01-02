# Comprehensive SBOM Generator for MrWhoOidc Solution
# Generates SBOM files in multiple formats with dependency information

param(
    [string]$OutputDir = "sbom",
    [ValidateSet("json", "xml", "both")]
    [string]$Format = "json",
    [switch]$Detailed = $false
)

$ErrorActionPreference = "Stop"
$SolutionRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$OutputPath = Join-Path $SolutionRoot $OutputDir

# Create output directory structure
$directories = @(
    $OutputPath,
    (Join-Path $OutputPath "projects"),
    (Join-Path $OutputPath "dependencies")
)

foreach ($dir in $directories) {
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir | Out-Null
    }
}

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "MrWhoOidc SBOM Generator" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""

# All projects in solution
$projects = @(
    @{Path="MrWhoOidc.ApiService\MrWhoOidc.ApiService.csproj"; Type="Application"},
    @{Path="MrWhoOidc.AppHost\MrWhoOidc.AppHost.csproj"; Type="Application"},
    @{Path="MrWhoOidc.Auth\MrWhoOidc.Auth.csproj"; Type="Library"},
    @{Path="MrWhoOidc.Security\MrWhoOidc.Security.csproj"; Type="Library"},
    @{Path="MrWhoOidc.ServiceDefaults\MrWhoOidc.ServiceDefaults.csproj"; Type="Library"},
    @{Path="MrWhoOidc.UnitTests\MrWhoOidc.UnitTests.csproj"; Type="Test"},
    @{Path="MrWhoOidc.Web\MrWhoOidc.Web.csproj"; Type="Application"},
    @{Path="MrWhoOidc.WebAuth\MrWhoOidc.WebAuth.csproj"; Type="Application"},
    @{Path="Examples\MrWhoOidc.RazorClient\MrWhoOidc.RazorClient.csproj"; Type="Example"},
    @{Path="Examples\MrWhoOidc.TestApi\MrWhoOidc.TestApi.csproj"; Type="Example"}
)

Write-Host "Step 1: Restoring and building projects..." -ForegroundColor Cyan
dotnet restore "$SolutionRoot\MrWhoOidc.slnx" --verbosity quiet
dotnet build "$SolutionRoot\MrWhoOidc.slnx" --configuration Release --no-incremental --verbosity quiet

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}
Write-Host "  [OK] Build complete" -ForegroundColor Green
Write-Host ""

# Generate dependency reports
Write-Host "Step 2: Generating dependency reports..." -ForegroundColor Cyan

$allDependencies = @{}
$successCount = 0
$failCount = 0

foreach ($projectInfo in $projects) {
    $project = $projectInfo.Path
    $projectPath = Join-Path $SolutionRoot $project
    
    if (-not (Test-Path $projectPath)) {
        Write-Host "  [SKIP] Skipping $project (not found)" -ForegroundColor Yellow
        $failCount++
        continue
    }
    
    $projectName = [System.IO.Path]::GetFileNameWithoutExtension($project)
    $projectType = $projectInfo.Type
    
    try {
        Write-Host "  Processing $projectName [$projectType]..." -ForegroundColor White
        
        # Get package dependencies
        $depsJson = dotnet list $projectPath package --format json | ConvertFrom-Json
        
        # Get project references
        $projectXml = [xml](Get-Content $projectPath)
        $projectRefs = $projectXml.Project.ItemGroup.ProjectReference.Include
        
        # Create dependency report
        $report = @{
            ProjectName = $projectName
            ProjectType = $projectType
            ProjectPath = $project
            TargetFramework = "net10.0"
            Timestamp = (Get-Date).ToString("o")
            NuGetPackages = @()
            ProjectReferences = @()
        }
        
        if ($depsJson.projects) {
            foreach ($proj in $depsJson.projects) {
                if ($proj.frameworks) {
                    foreach ($framework in $proj.frameworks) {
                        if ($framework.topLevelPackages) {
                            foreach ($pkg in $framework.topLevelPackages) {
                                $report.NuGetPackages += @{
                                    Id = $pkg.id
                                    RequestedVersion = $pkg.requestedVersion
                                    ResolvedVersion = $pkg.resolvedVersion
                                }
                            }
                        }
                    }
                }
            }
        }
        
        if ($projectRefs) {
            $report.ProjectReferences = @($projectRefs)
        }
        
        # Save individual project SBOM
        $projectSbomPath = Join-Path $OutputPath "projects\$projectName.sbom.json"
        $report | ConvertTo-Json -Depth 10 | Out-File $projectSbomPath -Encoding UTF8
        
        $allDependencies[$projectName] = $report
        
        # Create human-readable dependency list
        $depsListPath = Join-Path $OutputPath "dependencies\$projectName.txt"
        $depsList = @"
Project: $projectName
Type: $projectType
Target Framework: net10.0
Generated: $($report.Timestamp)

NuGet Packages ($($report.NuGetPackages.Count)):
$($report.NuGetPackages | ForEach-Object { "  - $($_.Id) $($_.ResolvedVersion)" } | Out-String)
Project References ($($report.ProjectReferences.Count)):
$($report.ProjectReferences | ForEach-Object { "  - $_" } | Out-String)
"@
        $depsList | Out-File $depsListPath -Encoding UTF8
        
        Write-Host "    [OK] Generated SBOM and dependency list" -ForegroundColor Green
        $successCount++
    }
    catch {
        Write-Host "    [FAIL] Failed: $($_.Exception.Message)" -ForegroundColor Red
        $failCount++
    }
}

Write-Host ""
Write-Host "Step 3: Generating solution-level SBOM..." -ForegroundColor Cyan

# Create solution-level SBOM
$solutionSbom = @{
    SolutionName = "MrWhoOidc"
    Version = "1.0.0"
    Description = "OpenID Connect Provider Implementation"
    Timestamp = (Get-Date).ToString("o")
    Projects = $allDependencies.Values
    Summary = @{
        TotalProjects = $projects.Count
        SuccessfulProjects = $successCount
        FailedProjects = $failCount
        TotalNuGetPackages = ($allDependencies.Values | ForEach-Object { $_.NuGetPackages.Count } | Measure-Object -Sum).Sum
    }
}

$solutionSbomPath = Join-Path $OutputPath "MrWhoOidc.Solution.sbom.json"
$solutionSbom | ConvertTo-Json -Depth 10 | Out-File $solutionSbomPath -Encoding UTF8
Write-Host "  [OK] Solution SBOM created" -ForegroundColor Green

# Generate consolidated dependency list
$consolidatedDepsPath = Join-Path $OutputPath "all-dependencies.txt"
$allPackages = $allDependencies.Values | 
    ForEach-Object { $_.NuGetPackages } | 
    Select-Object Id, ResolvedVersion -Unique | 
    Sort-Object Id

$consolidatedList = @"
MrWhoOidc Solution - Consolidated Dependencies
Generated: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")

Total Unique NuGet Packages: $($allPackages.Count)

Packages:
$($allPackages | ForEach-Object { "  - $($_.Id) $($_.ResolvedVersion)" } | Out-String)
"@

$consolidatedList | Out-File $consolidatedDepsPath -Encoding UTF8
Write-Host "  [OK] Consolidated dependency list created" -ForegroundColor Green

Write-Host ""
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "SBOM Generation Complete" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "Success: $successCount | Failed: $failCount" -ForegroundColor Cyan
Write-Host ""
Write-Host "Generated Files:" -ForegroundColor White
Write-Host "  - Solution SBOM: MrWhoOidc.Solution.sbom.json" -ForegroundColor White
Write-Host "  - Consolidated Deps: all-dependencies.txt" -ForegroundColor White
Write-Host "  - Per-Project SBOMs: $OutputPath\projects\" -ForegroundColor White
Write-Host "  - Per-Project Deps: $OutputPath\dependencies\" -ForegroundColor White
Write-Host ""
Write-Host "Output directory: $OutputPath" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
