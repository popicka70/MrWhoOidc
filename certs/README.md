# TLS Certificate Placeholder

The `docker-compose.yml` expects a PFX certificate mounted at `./certs/aspnetapp.pfx` so that `MrWhoOidc.WebAuth` can serve HTTPS from inside the container.

## Quick start

1. Create a developer HTTPS certificate:
   ```powershell
   dotnet dev-certs https --clean
   dotnet dev-certs https --trust
   dotnet dev-certs https -ep "$(Get-Location)\certs\aspnetapp.pfx" -p changeit
   ```
2. Confirm the `aspnetapp.pfx` file now exists in this folder. Update the password in `docker-compose.yml` if you used something other than `changeit`.
3. Restart the Compose stack so that the container picks up the certificate.

For production usage, replace this certificate with one issued by a trusted authority and rotate the password securely (environment secrets, Docker secrets, etc.).
