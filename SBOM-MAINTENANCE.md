# SBOM Maintenance Checklist

## When to Regenerate SBOMs

Use this checklist to know when to regenerate your Software Bill of Materials.

### 🔴 Always Regenerate (Critical)

- [ ] **Before Every Release**
  ```powershell
  .\generate-sbom-comprehensive.ps1
  ```
  - Ensures accurate dependency tracking
  - Required for compliance
  - Archive with release artifacts

- [ ] **After Adding/Removing NuGet Packages**
  ```powershell
  # After: dotnet add package <package-name>
  # Or: dotnet remove package <package-name>
  .\generate-sbom-comprehensive.ps1
  ```

- [ ] **After Updating Package Versions**
  ```powershell
  # After: dotnet add package <package-name> --version <version>
  # Or updating in .csproj files
  .\generate-sbom-comprehensive.ps1
  ```

- [ ] **After Major Framework Updates**
  ```powershell
  # After updating from .NET 8 to .NET 9, etc.
  .\generate-sbom-comprehensive.ps1
  ```

### 🟡 Recommended (Best Practice)

- [ ] **Monthly Security Reviews**
  ```powershell
  # First week of each month
  .\generate-sbom-comprehensive.ps1
  
  # Check for vulnerabilities
  dotnet list package --vulnerable
  
  # Review dependencies
  cat sbom\all-dependencies.txt
  ```

- [ ] **After Adding New Projects**
  - Verify the script includes the new project
  - Update project arrays if needed
  - Regenerate SBOM

- [ ] **Quarterly Dependency Audits**
  - Review all dependencies
  - Check for outdated packages
  - Plan upgrades
  - Document justification for older versions

### 🟢 Optional (As Needed)

- [ ] **Before Security Assessments**
  - Generate fresh SBOMs
  - Provide to security team
  - Include in assessment documentation

- [ ] **For Compliance Reporting**
  - Generate SBOM with appropriate format
  - Include in compliance package
  - Archive with compliance records

- [ ] **After Branch Merges (Major)**
  - After merging feature branches with dependency changes
  - Verify no conflicts in package versions

## Automated Checks

### CI/CD Integration Status

- [ ] **GitHub Actions Workflow Enabled**
  - Workflow file: `.github/workflows/sbom-generation.yml`
  - Triggers on: push, PR, release
  - Status: ✅ Created (enable in repo settings)

- [ ] **Artifact Retention**
  - Current: 90 days
  - Adjust in workflow if needed

- [ ] **Release Attachment**
  - Automatically attaches to releases
  - Files: Solution SBOM + consolidated deps

## Quick Commands

### Generate SBOMs
```powershell
# Recommended (full featured)
.\generate-sbom-comprehensive.ps1

# With custom output directory
.\generate-sbom-comprehensive.ps1 -OutputDir "release-sbom"

# Standard compliant (SPDX)
dotnet tool install --global Microsoft.Sbom.DotNetTool
.\generate-sbom.ps1

# Quick generation
.\generate-sbom-simple.ps1
```

### Review Dependencies
```powershell
# All unique dependencies
cat sbom\all-dependencies.txt

# Specific project
cat sbom\dependencies\MrWhoOidc.Auth.txt

# Check for vulnerabilities
dotnet list package --vulnerable

# Check for updates
dotnet list package --outdated
```

### Clean and Regenerate
```powershell
# Remove old SBOMs
Remove-Item sbom -Recurse -Force -ErrorAction SilentlyContinue

# Generate fresh
.\generate-sbom-comprehensive.ps1

# Verify
Get-ChildItem sbom -Recurse
```

### Archive for Release
```powershell
# Create release archive
$version = "1.0.0"  # Update version
Compress-Archive -Path sbom\* -DestinationPath "MrWhoOidc-SBOM-$version.zip"
```

## Verification Checklist

After generating SBOMs, verify:

- [ ] All expected projects have SBOM files
- [ ] Dependencies are accurate (spot check key packages)
- [ ] Timestamps are current
- [ ] No build errors in output
- [ ] File sizes are reasonable (not empty)
- [ ] JSON is valid (can be parsed)

```powershell
# Quick verification
Get-ChildItem sbom\projects\*.json | ForEach-Object {
    $content = Get-Content $_.FullName | ConvertFrom-Json
    Write-Host "$($content.ProjectName): $($content.NuGetPackages.Count) packages"
}
```

## Integration with Security Tools

### Vulnerability Scanning
```powershell
# Check for known vulnerabilities
dotnet list package --vulnerable

# After fixing, regenerate SBOM
.\generate-sbom-comprehensive.ps1
```

### Dependency Updates
```powershell
# List outdated packages
dotnet list package --outdated

# Update packages (example)
dotnet add package Microsoft.EntityFrameworkCore --version 9.0.9

# Regenerate SBOM
.\generate-sbom-comprehensive.ps1
```

### License Compliance
1. Generate SBOM
2. Review NuGet packages
3. Check licenses on nuget.org
4. Document findings
5. Update compliance records

## Troubleshooting

### SBOM Generation Fails

**Issue:** Script fails with build errors
```powershell
# Solution: Clean and rebuild
dotnet clean
dotnet restore MrWhoOidc.slnx
dotnet build --configuration Release
.\generate-sbom-comprehensive.ps1
```

**Issue:** PowerShell execution policy
```powershell
# Solution: Allow script execution
Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope CurrentUser
```

**Issue:** Missing projects in output
- Check project paths in script
- Verify .csproj files exist
- Update project arrays if needed

### SBOM Review Issues

**Issue:** Dependencies seem incomplete
- Check if project restored correctly
- Verify NuGet packages are resolved
- Run `dotnet list package` manually

**Issue:** Outdated information
- Always regenerate, don't reuse old SBOMs
- Check file timestamps

## Schedule Template

### Weekly
- Monitor CI/CD workflow runs
- Review any build failures

### Monthly
- Generate fresh SBOMs
- Review for vulnerabilities
- Check for outdated packages
- Update documentation if needed

### Quarterly
- Full dependency audit
- Plan package updates
- Review licensing
- Update compliance records

### Before Each Release
- Generate fresh SBOM
- Archive with release
- Update release notes with dependency changes
- Attach to GitHub release

## History Log

| Date | Action | Reason | Generated By |
|------|--------|--------|--------------|
| 2025-10-02 | Initial setup | Project requirement | Script |
| | | | |
| | | | |

## Notes

- SBOM files in `sbom/` are gitignored (generated fresh each time)
- CI/CD workflows generate artifacts automatically
- Keep scripts updated if adding/removing projects
- Review this checklist quarterly

---

**Last Updated:** October 2, 2025  
**Script Version:** 1.0  
**Next Review:** January 2, 2026
