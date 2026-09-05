# MailHog Local SMTP

MailHog is included in [docker-compose.dev.yml](../docker-compose.dev.yml), not the production Compose. It captures development mail without delivering it to real recipients. Complete the source-development [quickstart](for-developers/quickstart-15-min.md) first.

## Start and Inspect

From the repository root in PowerShell:

```powershell
docker compose -f docker-compose.dev.yml up -d mailhog
docker compose -f docker-compose.dev.yml ps mailhog
```

This starts the mail sink only, not WebAuth. The development stack normally uses these defaults:

```dotenv
DEV_MAIL_ENABLED=true
DEV_MAIL_SMTP_HOST=mailhog
DEV_MAIL_SMTP_PORT=1025
DEV_MAIL_SMTP_USE_SSL=false
```

`mailhog` resolves from containers on the development Compose network. For an application running directly on the host, configure its mail host as `localhost` and SMTP port as `1025`; host processes do not inherit container environment variables. The application property names are `Mail:Enabled`, `Mail:SmtpHost`, `Mail:SmtpPort`, and `Mail:UseSsl`.

The inbox is at <http://localhost:8025>. After triggering a controlled email flow, check the actual recipient, links, issuer/host, and contents. Absence of mail can also mean the workflow deliberately suppressed a response; inspect application diagnostics without exposing tokens or reset links.

## Apply Configuration and Clear Messages

If you change `DEV_MAIL_*`, recreate the affected application service with the same development Compose file. A simple container restart does not apply new environment variables. Production `MAIL_*` values do not replace these development-specific mappings.

Use the inbox's delete action to remove captured messages only when their loss is acceptable. MailHog is not a durable mail archive or a production SMTP relay.

The default development file publishes ports 1025 and 8025 on the host. Restrict network/firewall access; do not expose an unauthenticated inbox containing reset or verification links to untrusted networks. Never use this sink as the production recovery-mail service.

Reviewed against the development Compose mappings on 2026-09-05; no messages were sent during this review.
