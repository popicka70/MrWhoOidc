# TLS Certificate

The `docker-compose.yml` expects a PFX certificate mounted at `./certs/aspnetapp.pfx` so that `MrWhoOidc.WebAuth` can serve HTTPS from inside the container.

Generate a development certificate locally; `certs/aspnetapp.pfx` is ignored by Git and is not supplied with the repository.

## Quick start (recommended)

Install the .NET 10 SDK and run the setup script from the source repository root. It:

- exports a local HTTPS developer certificate to `./certs/aspnetapp.pfx`,
- attempts to trust the certificate; confirmation or OS/browser configuration may still be needed,
- creates `.env` from `.env.example` with development defaults (including `CERT_PASSWORD=changeit`).

Linux/macOS:

```bash
bash scripts/setup-dev.sh
```

Windows (PowerShell):

```powershell
pwsh scripts/setup-dev.ps1
```

Re-running the script preserves an existing `.env` but regenerates `certs/aspnetapp.pfx`. Check whether other local services use that file before running it again; do not delete `.env` merely to refresh the certificate.

## Manual alternative (if the setup script is unavailable)

1. Export a developer HTTPS certificate:

   ```powershell
   dotnet dev-certs https -ep "$(Get-Location)\certs\aspnetapp.pfx" -p "<local-dev-cert-password>"
   ```

2. Optional: trust the certificate locally if you want browsers and local tools to accept it without TLS warnings.

   Windows or macOS:

   ```powershell
   dotnet dev-certs https --trust
   ```

   Linux:

   ```bash
   export SSL_CERT_DIR="$HOME/.aspnet/dev-certs/trust:/usr/lib/ssl/certs"
   dotnet dev-certs https --trust
   ```

   `dotnet dev-certs https --clean` is optional cleanup only. On some Linux hosts it exits with code 8, so skip it unless you are trying to reset a broken local dev-certs state.

3. On Linux or macOS hosts, make the exported file readable by the non-root container user:

   ```bash
   chmod 644 ./certs/aspnetapp.pfx
   ```

4. Confirm the `aspnetapp.pfx` file now exists in this folder. Set `CERT_PASSWORD` for the source production-shaped Compose file, or `DEV_CERT_PASSWORD` for the development WebAuth service. Some sample services still use `changeit` directly; inspect their certificate settings before choosing a different password for the full dev stack.
5. Restart the Compose stack so that the container picks up the certificate.

If the file mode is too restrictive, `MrWhoOidc.WebAuth` can fail during startup with `Access to the path '/https/aspnetapp.pfx' is denied`.

If `dotnet dev-certs https --trust` fails on Linux before startup, the `SSL_CERT_DIR` override above usually fixes it by pointing dotnet at both the dev-certs trust directory and the system CA bundle.

## Security notes

The locally generated `aspnetapp.pfx` is development/test material only. Do not reuse it for staging or production. Production startup rejects missing, short, or default-looking certificate passwords such as `changeit`.

For production usage, replace this certificate with one issued by a trusted authority and provide the password through a deployment secret such as Docker secrets, Kubernetes secrets, or a managed secret store.
