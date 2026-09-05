# QR Code Browser Bundle

`qrcode-1.5.4.min.js` exposes `window.QRCode`, including `toCanvas`, for the customer portal.
It is built from `qrcode@1.5.4` and `dijkstrajs@1.0.3` using `esbuild@0.25.12`.
The npm package does not ship the `build/qrcode.min.js` file previously referenced on jsDelivr.

Regenerate from the repository root with PowerShell 7, Node.js, and npm:

```powershell
./scripts/update-web-qrcode.ps1
```

The script installs build dependencies in a temporary directory, disables package lifecycle scripts,
creates the browser bundle, copies both runtime licenses, and removes its temporary directory.
Commit the generated bundle and license files together. Deployment serves these static files without Node.js.

QR payloads are rendered locally in the browser. No QR generation service receives payment data.
