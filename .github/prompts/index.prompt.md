---
description: "Initialize or refresh the project wiki index and core wiki pages for this codebase."
argument-hint: "Optional scope, for example: auth only, tests only, or full refresh"
agent: "agent"
---

## User Input

```text
$ARGUMENTS
```

Initialize or refresh the project wiki for this repository.

Requirements:

1. Treat [wiki/schema.md](../../wiki/schema.md) as the wiki contract.
2. Read [README.md](../../README.md), [docs/index.md](../../docs/index.md), and [.github/copilot-instructions.md](../copilot-instructions.md) before updating architecture pages.
3. Update [wiki/overview.md](../../wiki/overview.md), [wiki/index.md](../../wiki/index.md), and [wiki/log.md](../../wiki/log.md).
4. Maintain entity pages under [wiki/entities](../../wiki/entities) for major projects and executable surfaces.
5. Maintain concept pages under [wiki/concepts](../../wiki/concepts) for cross-cutting behavior such as flows, deployment, and testing.
6. Use `[[wikilinks]]` between wiki pages and keep `related_files` current.
7. Treat code and curated docs as the source of truth. If the wiki disagrees with code or curated docs, update the wiki.
8. Focus on structural changes. Skip formatting-only, comment-only, and other trivial edits unless the user explicitly asks to capture them.
9. Append a parseable entry to [wiki/log.md](../../wiki/log.md) using `## [YYYY-MM-DD] init | ...`, `ingest | ...`, `query | ...`, or `lint | ...`.

When the wiki is missing major pages, bootstrap them first. When it already exists, refresh it in place and report which pages were created or updated, which sources were consulted, and which areas remain thin.