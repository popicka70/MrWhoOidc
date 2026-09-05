# Historical Docs Archive

The files in this folder are preserved as implementation notes, completed backlogs, debug write-ups, and design snapshots.

They are **not** the source of truth for current setup or operations. Many of them intentionally describe the repository as it existed when the work was completed, so they can still reference older values such as:

- `.NET 9` instead of the current `.NET 10` baseline
- `https://localhost:7208` instead of the current local development endpoint `https://localhost:8443`
- older seed users, branch names, or in-progress status markers

Use the active docs for current guidance:

- [Repository README](../../README.md)
- [Documentation Index](../index.md)
- [Developer Guide](../developer-guide.md)
- [Deployment Guide](../deployment-guide.md)
- [Production Setup Guide](../production-setup-guide.md)
- [Example Applications Guide](../example-applications-guide.md)

When a file in this folder contains a command, URL, or runtime note that looks runnable, treat it as historical context unless it matches the active docs above.

## Extracted Current Guidance

- [Client Secret Rotation](../for-operators/client-secret-rotation.md) replaces the operational role of the archived rotation guide and playbook.
- [Key Rotation and Certificate Lifecycles](../for-operators/key-rotation.md) separates server signing keys from the old outbound-JAR playbook.
- [Security Keys and Passkeys](../for-administrators/webauthn.md) replaces the historical WebAuthn user guide.
- [Pairwise Subject Identifiers](../reference/pairwise-subject-identifiers.md) documents the implemented foundation formerly described as a future plan.

See [Documentation Status and Follow-up](../documentation-status.md) for retained evidence and current verification work. Files were kept in place for traceability; an archive location or a completion-oriented filename is not proof that every acceptance criterion still passes.
