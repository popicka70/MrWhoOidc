# ReactOidcClient - Node.js Upgrade Guide

## Problem
The ReactOidcClient example requires Node.js 18+ but you're running Node.js v10.24.1 (from 2018).

## Error
```
SyntaxError: Unexpected token {
```
This occurs because Node.js v10 doesn't support ES Modules syntax used by Vite 5.

## Solution

### 1. Check Current Version
```powershell
node --version
# Should show v10.24.1 (outdated)
```

### 2. Install Node.js 20 LTS (Recommended)

#### Option A: Direct Install
1. Visit https://nodejs.org/
2. Download **Node.js 20.x LTS** (Windows Installer)
3. Run the installer
4. Restart your terminal/VS Code

#### Option B: Using nvm-windows
```powershell
# Install nvm-windows from: https://github.com/coreybutler/nvm-windows/releases
# Then run:
nvm install 20
nvm use 20
```

### 3. Verify Installation
```powershell
node --version   # Should show v20.x.x
npm --version    # Should show v10.x.x
```

### 4. Reinstall Dependencies
```powershell
cd Examples\ReactOidcClient
rm -rf node_modules package-lock.json
npm install
```

### 5. Run the Dev Server
```powershell
npm run dev
```

Expected output:
```
  VITE v5.4.3  ready in 500 ms

  ➜  Local:   http://localhost:5173/
  ➜  Network: use --host to expose
```

## Minimum Requirements

| Package | Min Node Version |
|---------|-----------------|
| Vite 5.x | Node.js 18+ |
| React 18 | Node.js 14+ |
| TypeScript 5.x | Node.js 14+ |

**Current setup requires: Node.js 18.0.0 or higher**

## Troubleshooting

### "npm command not found" after upgrade
- Restart your terminal/PowerShell
- Restart VS Code completely
- Check PATH environment variable includes Node.js

### Still showing old version
```powershell
# Clear npm cache
npm cache clean --force

# Check which node is being used
where.exe node

# Should point to new installation
```

### Multiple Node.js installations
Use nvm-windows to manage versions:
```powershell
nvm list           # Show installed versions
nvm use 20         # Switch to version 20
nvm uninstall 10   # Remove old version
```

## Alternative: Use Older Build Tools (Not Recommended)

If you cannot upgrade Node.js, you'd need to downgrade the entire stack:
- React 17
- Vite 2.x or webpack
- TypeScript 4.x

This is **NOT recommended** as you'll miss security updates and modern features.

## Next Steps After Upgrade

1. **Verify the React app runs**:
   ```powershell
   cd Examples\ReactOidcClient
   npm run dev
   ```

2. **Test the OIDC flow**:
   - Ensure MrWhoOidc.AppHost is running
   - Navigate to http://localhost:5173
   - Click "Login" button
   - Should redirect to WebAuth login page

3. **Build for production**:
   ```powershell
   npm run build
   npm run preview
   ```

## Support

Node.js v10 reached End-of-Life on **April 30, 2021**. Upgrading is essential for:
- ✅ Security patches
- ✅ Modern JavaScript features
- ✅ Compatibility with current libraries
- ✅ Performance improvements

**Recommended version**: Node.js 20.x LTS (supported until April 2026)
