# MailHog Local SMTP

MailHog is included in `docker-compose.yml` to capture outbound email during local development. It exposes an SMTP endpoint (port 1025) and a web UI (port 8025) so you can inspect messages without sending real email.

## Start MailHog with Docker Compose

```powershell
docker compose up -d mailhog
```

- Starts only the MailHog container.
- Use `docker compose up -d` to launch the full stack (MailHog, webauth, postgres, redis).

## Access Points

- SMTP: `localhost:1025`
- Web UI: `http://localhost:8025`

## Using MailHog from Containers

When your application runs inside Docker, point SMTP settings at the `mailhog` host:

- Host: `mailhog`
- Port: `1025`
- TLS/Auth: disabled (MailHog ignores credentials)

Example environment snippet for future mail integration:

```yaml
environment:
  Mail__SmtpHost: mailhog
  Mail__SmtpPort: 1025
  Mail__UseSsl: "false"
  Mail__Username: ""
  Mail__Password: ""
```

## Reset the Inbox

Use the web UI **Trash** button to clear messages, or run:

```powershell
Invoke-WebRequest -Method Delete http://localhost:8025/api/v1/messages
```

MailHog should only be used for development and test environments.
