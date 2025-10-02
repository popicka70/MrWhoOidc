# SBOM Quick Reference

## Generate SBOM Files

```powershell
# Generate comprehensive SBOMs (recommended)
.\generate-sbom-comprehensive.ps1

# Output location
.\sbom\
```

## What Gets Generated

### Files Structure
```
sbom/
├── MrWhoOidc.Solution.sbom.json    ← Complete solution SBOM
├── all-dependencies.txt             ← All unique dependencies
├── projects/                        ← Individual project SBOMs
│   ├── MrWhoOidc.Auth.sbom.json
│   ├── MrWhoOidc.Client.sbom.json
│   └── ... (one per project)
└── dependencies/                    ← Human-readable reports
    ├── MrWhoOidc.Auth.txt
    ├── MrWhoOidc.Client.txt
    └── ... (one per project)
```

## View Dependencies

### For a specific project
```powershell
cat sbom\dependencies\MrWhoOidc.Auth.txt
```

### For all projects
```powershell
cat sbom\all-dependencies.txt
```

### View JSON SBOM
```powershell
cat sbom\MrWhoOidc.Solution.sbom.json | ConvertFrom-Json
```

## Common Tasks

### Security Audit
1. Generate SBOM: `.\generate-sbom-comprehensive.ps1`
2. Review: `cat sbom\all-dependencies.txt`
3. Check for vulnerabilities in listed packages

### Before Release
```powershell
# Clean old SBOMs
Remove-Item sbom -Recurse -Force -ErrorAction SilentlyContinue

# Generate fresh SBOMs
.\generate-sbom-comprehensive.ps1

# Archive for distribution
Compress-Archive -Path sbom\* -DestinationPath release-sbom.zip
```

### CI/CD Integration
- GitHub Actions workflow included: `.github/workflows/sbom-generation.yml`
- Runs automatically on push, PR, and releases
- Artifacts available for 90 days

## Project Information

### Main Libraries
- **MrWhoOidc.Auth** - Core OIDC domain logic
- **MrWhoOidc.Client** - OIDC client SDK
- **MrWhoOidc.Security** - Security utilities (DPoP, etc.)
- **MrWhoOidc.ServiceDefaults** - Shared configuration

### Applications
- **MrWhoOidc.WebAuth** - Authorization server
- **MrWhoOidc.Web** - Sample RP client
- **MrWhoOidc.ApiService** - Sample resource API

### Target Framework
- All projects: **.NET 9.0**

## Alternative Scripts

### Microsoft SBOM Tool (SPDX 2.2 format)
```powershell
# Install tool first
dotnet tool install --global Microsoft.Sbom.DotNetTool

# Generate SPDX SBOMs
.\generate-sbom.ps1
```

### Simple/Quick Generation
```powershell
.\generate-sbom-simple.ps1
```

## Troubleshooting

### Build errors
```powershell
dotnet clean
dotnet restore
dotnet build --configuration Release
```

### PowerShell execution policy
```powershell
Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser
```

### Missing dependencies
- Ensure solution builds successfully first
- Run `dotnet restore MrWhoOidc.slnx`

## Documentation

See `SBOM-README.md` for complete documentation including:
- Detailed usage instructions
- Format specifications
- CI/CD examples
- Integration with security tools
- Standards compliance (SPDX, CycloneDX)

## Key Dependencies (Summary)

**Database:** PostgreSQL via Npgsql.EntityFrameworkCore.PostgreSQL
**Security:** System.IdentityModel.Tokens.Jwt, Argon2
**Framework:** ASP.NET Core 9.0, Entity Framework Core 9.0
**Observability:** Application Insights, OpenTelemetry
**Caching:** StackExchange.Redis

---
*Generated: October 2025*
*Target: .NET 9.0*
