# SBOM Generation for MrWhoOidc

This directory contains scripts to generate Software Bill of Materials (SBOM) files for the MrWhoOidc solution.

## Quick Start

### Recommended: Comprehensive SBOM Generator

This is the easiest and most reliable option:

```powershell
.\generate-sbom-comprehensive.ps1
```

This will generate:
- Solution-wide SBOM with all dependencies
- Individual project SBOMs
- Human-readable dependency lists
- Consolidated dependency report

**Output:** All files are generated in the `sbom/` directory.

## Available Scripts

### 1. `generate-sbom-comprehensive.ps1` (Recommended)

**Best for:** Complete dependency tracking and human-readable reports

```powershell
# Basic usage
.\generate-sbom-comprehensive.ps1

# Custom output directory
.\generate-sbom-comprehensive.ps1 -OutputDir "my-sboms"
```

**Generates:**
- ✅ Solution-level SBOM (`MrWhoOidc.Solution.sbom.json`)
- ✅ Per-project SBOMs in `projects/` subdirectory
- ✅ Human-readable dependency lists in `dependencies/` subdirectory
- ✅ Consolidated dependency report (`all-dependencies.txt`)

**Format:** JSON with comprehensive project metadata

### 2. `generate-sbom.ps1` (Industry Standard)

**Best for:** Standards-compliant SPDX format using Microsoft SBOM Tool

```powershell
# First, install the Microsoft SBOM Tool
dotnet tool install --global Microsoft.Sbom.DotNetTool

# Generate SBOMs
.\generate-sbom.ps1

# Specify format
.\generate-sbom.ps1 -Format json
.\generate-sbom.ps1 -Format xml
```

**Generates:** SPDX 2.2 format SBOMs (industry standard)

**Requirements:** `Microsoft.Sbom.DotNetTool` must be installed globally

### 3. `generate-sbom-simple.ps1` (Quick)

**Best for:** Quick generation using built-in dotnet pack features

```powershell
.\generate-sbom-simple.ps1

# Skip build if already built
.\generate-sbom-simple.ps1 -SkipBuild
```

**Generates:** SBOMs from dotnet pack operation + dependency fallback

## Output Structure

After running `generate-sbom-comprehensive.ps1`, you'll see:

```
sbom/
├── MrWhoOidc.Solution.sbom.json      # Complete solution SBOM
├── all-dependencies.txt               # All unique dependencies
├── projects/                          # Per-project SBOMs
│   ├── MrWhoOidc.Auth.sbom.json
│   ├── MrWhoOidc.Client.sbom.json
│   ├── MrWhoOidc.Security.sbom.json
│   └── ... (one per project)
└── dependencies/                      # Human-readable dependency lists
    ├── MrWhoOidc.Auth.txt
    ├── MrWhoOidc.Client.txt
    ├── MrWhoOidc.Security.txt
    └── ... (one per project)
```

## SBOM Content

Each SBOM file includes:

- **Project Name & Type** (Library, Application, Test, Example)
- **Target Framework** (net9.0)
- **NuGet Package Dependencies** with versions
- **Project References** (internal dependencies)
- **Generation Timestamp**

### Example SBOM Structure

```json
{
  "ProjectName": "MrWhoOidc.Auth",
  "ProjectType": "Library",
  "TargetFramework": "net9.0",
  "Timestamp": "2025-10-02T...",
  "NuGetPackages": [
    {
      "Id": "Microsoft.EntityFrameworkCore",
      "RequestedVersion": "9.0.0",
      "ResolvedVersion": "9.0.0"
    }
  ],
  "ProjectReferences": [
    "..\MrWhoOidc.Security\MrWhoOidc.Security.csproj"
  ]
}
```

## Use Cases

### Security Auditing
Use the generated SBOMs to:
- Identify vulnerable package versions
- Track transitive dependencies
- Audit third-party components
- Comply with security policies

### License Compliance
Review dependencies for:
- License compatibility
- Attribution requirements
- Open source obligations

### Dependency Management
- Track package versions across projects
- Identify duplicate dependencies
- Plan upgrades and migrations
- Monitor dependency drift

## CI/CD Integration

### GitHub Actions Example

```yaml
name: Generate SBOM

on:
  push:
    branches: [main]
  release:
    types: [created]

jobs:
  sbom:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      
      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '9.0.x'
      
      - name: Generate SBOM
        shell: pwsh
        run: .\generate-sbom-comprehensive.ps1
      
      - name: Upload SBOM Artifacts
        uses: actions/upload-artifact@v4
        with:
          name: sbom-files
          path: sbom/
          retention-days: 90
```

### Azure DevOps Pipeline Example

```yaml
trigger:
  branches:
    include:
    - main

pool:
  vmImage: 'windows-latest'

steps:
- task: UseDotNet@2
  inputs:
    version: '9.0.x'

- pwsh: |
    .\generate-sbom-comprehensive.ps1
  displayName: 'Generate SBOM'

- task: PublishBuildArtifacts@1
  inputs:
    PathtoPublish: 'sbom'
    ArtifactName: 'sbom-files'
```

## Troubleshooting

### Build Failures

If you see build errors:
```powershell
# Clean and rebuild
dotnet clean
dotnet restore
dotnet build --configuration Release
```

### Missing Dependencies

Ensure all projects are restored:
```powershell
dotnet restore MrWhoOidc.slnx
```

### Permission Issues

Run PowerShell as Administrator if you encounter file access issues.

### Script Execution Policy

If scripts won't run:
```powershell
Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser
```

## Standards Compliance

- **SPDX 2.2**: Use `generate-sbom.ps1` with Microsoft.Sbom.DotNetTool
- **CycloneDX**: Can be added via additional tooling
- **Custom Format**: Use `generate-sbom-comprehensive.ps1` for JSON

## Additional Resources

- [SPDX Specification](https://spdx.dev/)
- [CycloneDX Specification](https://cyclonedx.org/)
- [Microsoft SBOM Tool](https://github.com/microsoft/sbom-tool)
- [NTIA SBOM Guidelines](https://www.ntia.gov/sbom)

## Integration with Security Tools

The generated SBOMs can be consumed by:
- **GitHub Dependency Graph** (via SPDX)
- **Dependabot** (automatic)
- **Snyk** (import SBOM)
- **WhiteSource/Mend** (SBOM analysis)
- **Black Duck** (SBOM scanning)
- **JFrog Xray** (artifact analysis)

---

**Note:** Always regenerate SBOMs after dependency updates or before releases to ensure accuracy.
