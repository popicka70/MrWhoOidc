# Docker Security for MrWhoOidc

Hardening notes for the images and Compose files in this repository. For configuration and bootstrap, see the [deployment guide](deployment-guide.md) and [production setup](production-setup-guide.md).

## Image

The production image is built from the root [Dockerfile](../Dockerfile):

- Build stage `mcr.microsoft.com/dotnet/sdk:10.0`; runtime `mcr.microsoft.com/dotnet/aspnet:10.0-noble`. The runtime is a full Ubuntu image, not chiseled, because ICU globalization is required. It contains a shell and apt, plus `curl` (for `HEALTHCHECK`) and `libgssapi-krb5-2`.
- Runs as the non-root `app` user (`USER $APP_UID`, UID 1654). Check with `docker compose exec webauth id`.
- Listens on `https://+:8443` and `http://+:8080`; both are above 1024, so no capabilities are needed.
- Sets `ASPNETCORE_ENVIRONMENT=Production`, `DOTNET_GCHeapHardLimitPercent=75`, and `DOTNET_GCConserveMemory=1`. The GC heap limit is relative to the container memory limit.
- `.dockerignore` excludes `.env`, `certs/`, `secrets/`, `bin/`, and `obj/` from the build context, so key material is never baked into a layer. Supply it at runtime through mounts or the platform's secret store.

`MrWhoOidc.WebAuth/Dockerfile` (used by `docker-compose.dev.yml`) is a development image that trusts the mounted dev PFX at startup. Do not deploy it. It and the example images (`Examples/*/Dockerfile`) also run as `app`. Their entrypoint, `tools/run-dotnet-with-dev-cert.sh`, needs no root:

- It appends the dev certificate to a copy of the system CA bundle in a private temp directory and points `SSL_CERT_FILE` at it. It does not run `update-ca-certificates` or write to `/etc/ssl`.
- It does not edit `/etc/hosts`. The compose file maps `localhost` to the host with `extra_hosts: localhost:host-gateway`. The resolver returns `127.0.0.1` first and the host after it, so a connection to another service's published `localhost` port is refused on loopback and falls through to the host.
- The example images create `/app/data-protection-keys` owned by `app`. A named volume created by an older root image keeps root ownership; remove it once (`docker volume rm mrwhooidc_testapi-data-protection mrwhooidc_razorclient-data-protection mrwhooidc_oidcdemo-data-protection`, or `docker compose -f docker-compose.dev.yml down -v`, which also drops the dev databases).

### Published images and supply chain

`.github/workflows/docker-publish.yml` pushes `ghcr.io/popicka70/mrwhooidc` for `linux/amd64` and `linux/arm64` on pushes to `master` and `v*` tags. Tags: branch name, semver (`X.Y.Z`, `X.Y`, `X`), `sha-<commit>`, and `latest` on `master`. Images are not signed and carry no provenance attestation. Therefore:

- Deploy by digest (`image: ghcr.io/popicka70/mrwhooidc@sha256:...`) and record it; do not use `latest` in production.
- Scan the exact digest before rollout, for example `trivy image ghcr.io/popicka70/mrwhooidc@sha256:...`. The `sbom-generation.yml` workflow produces a dependency SBOM artifact, not an image SBOM.
- Rebuild or update regularly to pick up base-image patches.

## Container Runtime Options

The source `docker-compose.yml` does not set these. Add them through an override and test them:

```yaml
services:
  webauth:
    cap_drop: [ALL]
    security_opt: ["no-new-privileges:true"]
    deploy:
      resources:
        limits:
          memory: 1G
```

- Set a memory limit so `DOTNET_GCHeapHardLimitPercent` has a bound. Prefer CPU requests/reservations over hard CPU limits, which cause CFS throttling of latency-sensitive password hashing and token signing.
- A read-only root filesystem (`read_only: true` with a `tmpfs` for `/tmp`) has not been tested with this image. WebAuth stores DataProtection keys and signing keys in PostgreSQL, not on disk, but verify login, key rotation, and email before relying on it.

## Network

The source Compose file defines two networks:

| Network | Members | Notes |
| --- | --- | --- |
| `internal` (`internal: true`) | `webauth`, `postgres`, `redis` | No external connectivity; PostgreSQL and Redis publish no host ports |
| `edge` | `webauth` | Publishes `8443:8443` and `8081:8080` |

- Port `8080`/`8081` exists only for HTTP to HTTPS redirection (`Security:HttpsRedirectMode`, default `Auto`). If a proxy terminates TLS, publish only what the proxy needs, preferably bound to a private address such as `127.0.0.1:8443:8443`.
- Configure `ForwardedHeaders` known proxies/networks for any proxy, keep `ForwardedHeaders__UnsafeTrustAll=false`, and enable `ForwardedHeaders__EnforceHostAllowList` with `AllowedHosts`. See [Reverse Proxy / Forwarded Headers](deployment-guide.md#reverse-proxy--forwarded-headers-optional).
- With `Security__CertificateForwarding__Enabled=true`, `X-Client-Cert` is honoured only when the direct peer is loopback or a known proxy/network (or `UnsafeTrustAll`); otherwise it is stripped. The proxy must still overwrite any client-supplied value. See [forwarded client certificates](deployment-guide.md#forwarded-client-certificates-mtls-behind-a-proxy).
- WebAuth makes outbound calls (upstream IdPs, client JWKS, `sector_identifier_uri`, back-channel logout, SMTP); allow egress accordingly. Fetches of `sector_identifier_uri` and upstream OIDC endpoints go through an SSRF-guarded client that blocks private addresses.

## Secrets

- `POSTGRES_PASSWORD` is mandatory: Compose fails with `POSTGRES_PASSWORD must be set...` if it is empty. It only initializes a new data directory; rotate an existing role with `ALTER ROLE` and update the connection string together.
- Keep `.env` out of version control and readable only by the deploying user (`chmod 600 .env`). `docker compose config` without `--quiet` prints expanded secrets.
- The default connection string sets `Include Error Detail=false`; keep it that way in production.
- Production startup fails unless the DataProtection key ring is encrypted with a certificate (`DATAPROTECTION_CERTIFICATE_PATH` or `DATAPROTECTION_CERTIFICATE_BASE64` plus password) or `DATAPROTECTION_ALLOW_UNENCRYPTED_KEY_RING=true` is set. Use the certificate.
- Private signing keys are encrypted with DataProtection before they are first stored and are cached only in process memory, never in Redis.
- Set `BOOTSTRAP_TOKEN` only for the first-run bootstrap, then clear it and recreate the container (`docker compose up -d`). With no token, `/bootstrap` returns 404.
- On orchestrators, inject the same settings as `ConnectionStrings__authdb`, `DataProtection__CertificatePassword`, and so on from the platform's secret store instead of `.env`.

## TLS

- Kestrel reads the PFX from `ASPNETCORE_Kestrel__Certificates__Default__Path` (default `/https/aspnetapp.pfx`, from the read-only `./certs` mount) with `CERT_PASSWORD`. The container user must be able to read it; do not make it world-readable on the host.
- `certs/aspnetapp.pfx` from `scripts/setup-dev.sh` is a local development certificate. Never use it, or the `changeit` password, in production.
- HSTS (365 days, include subdomains, preload) is enabled outside Development.
- Renewing a certificate requires recreating or restarting `webauth`; plan renewal with the issuing CA or proxy.
- The container health check uses `curl -k` against `localhost` only. External probes must verify certificates.

## PostgreSQL and Redis

- Both images are pinned to major/minor tags (`postgres:16-alpine`, `redis:7.2-alpine`). Pin digests if you need reproducibility.
- The bundled Redis runs without a password and relies on the `internal` network. If Redis is shared or external, use `password=` and `ssl=true` in `REDIS_CONNECTION_STRING`. Redis holds caches, DPoP/JAR replay state, and rate-limit counters, but not private keys.
- For an external PostgreSQL, set `CONNECTION_STRING_AUTHDB` with `SSL Mode=VerifyFull` (or the provider's equivalent). Encrypt backups and store DataProtection certificates separately from them.

## Development Stack Is Not Hardened

`docker-compose.dev.yml` uses fixed credentials (`oidcPass!`, `Admin123!`, seeded client secrets), `Testing__EnableAutoSeed`, `Testing__AllowLocalExternalOidcHttp` (disables the SSRF private-address guard for upstream OIDC calls), open dynamic client registration without an initial access token, and publishes MailHog. Never expose it beyond a developer machine.

## KeyGen

`docker-compose-keygen.yml` mounts the licensing private key from `./secrets` (read-only), serves HTTP only, and binds to `127.0.0.1:8090`. Keep it local or behind an authenticated TLS proxy; it is not a public service.

## Checklist

- [ ] Image deployed by digest and scanned; base image updated regularly.
- [ ] Non-root user confirmed; `cap_drop: [ALL]` and `no-new-privileges` applied.
- [ ] Memory limit set; CPU limits justified.
- [ ] PostgreSQL and Redis unreachable from outside the `internal` network.
- [ ] Only required ports published; proxy addresses listed in `ForwardedHeaders`; `UnsafeTrustAll=false`; host allow-list enforced.
- [ ] Trusted TLS certificate with a renewal plan; no dev certificate or `changeit`.
- [ ] DataProtection certificate configured and backed up separately from the database.
- [ ] `BOOTSTRAP_TOKEN` cleared after bootstrap; no `Testing__*` settings in production.
- [ ] Resource servers that introspect have `IntrospectionAudiencesJson` or `Auth__IntrospectionPermissions` (introspection is deny-by-default).
- [ ] Logs exported with tokens, secrets, and personal data redacted; alerts tested (see [monitoring](for-operators/monitoring/alerting-rules.md)).
