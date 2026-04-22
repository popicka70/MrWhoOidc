---
title: MrWhoOidc.Cli
type: entity
tags: [cli, admin, automation]
created: 2026-04-22
updated: 2026-04-22
related_files:
  - MrWhoOidc.Cli/README.md
  - deploy-mrwho-cli.sh
  - skills/mrwho-cli.md
---

`MrWhoOidc.Cli` is the administrative command-line surface for working with tenants, realms, clients, exports, imports, and scripted auth server operations. It turns common admin tasks into repeatable automation instead of requiring manual UI work.

## Responsibilities

- Authenticate to a target server profile.
- Expose tenant, realm, client, and configuration workflows.
- Support import/export and scripting scenarios.
- Provide a stable automation surface for examples and operators.

## Notes

- The repository keeps dedicated CLI usage guidance under `skills/mrwho-cli.md` and the CLI README.
- Wiki updates are warranted when command families, authentication behavior, or server-context conventions change.

## Related Pages

- [[deployment-modes]]
- [[testing-strategy]]