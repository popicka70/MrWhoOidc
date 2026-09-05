param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$vendorDirectory = Join-Path $repositoryRoot 'MrWhoOidc.Web/js/vendor'
$temporaryDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ('mrwho-qrcode-' + [guid]::NewGuid())
$npmCommand = if ($IsWindows) { 'npm.cmd' } else { 'npm' }

try {
    New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null
    & $npmCommand install --prefix $temporaryDirectory --ignore-scripts --no-audit --no-fund --package-lock=false qrcode@1.5.4 dijkstrajs@1.0.3 esbuild@0.25.12
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not install the pinned QR bundle dependencies.'
    }

    $modulesDirectory = Join-Path $temporaryDirectory 'node_modules'
    $entryPoint = Join-Path $modulesDirectory 'qrcode/lib/browser.js'
    $bundler = Join-Path $modulesDirectory 'esbuild/bin/esbuild'
    $bundlePath = Join-Path $temporaryDirectory 'qrcode-1.5.4.min.js'
    & node $bundler $entryPoint --bundle --platform=browser --format=iife --global-name=QRCode --minify --legal-comments=inline "--outfile=$bundlePath"
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not build the browser QR bundle.'
    }

    New-Item -ItemType Directory -Path $vendorDirectory -Force | Out-Null
    Copy-Item $bundlePath (Join-Path $vendorDirectory 'qrcode-1.5.4.min.js')
    Copy-Item (Join-Path $modulesDirectory 'qrcode/license') (Join-Path $vendorDirectory 'qrcode-LICENSE.txt')
    Copy-Item (Join-Path $modulesDirectory 'dijkstrajs/LICENSE.md') (Join-Path $vendorDirectory 'dijkstrajs-LICENSE.txt')
}
finally {
    if (Test-Path $temporaryDirectory) {
        Remove-Item $temporaryDirectory -Recurse -Force
    }
}
