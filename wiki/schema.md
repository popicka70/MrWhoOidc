---
title: Project Wiki Schema
type: schema
created: 2026-04-22
updated: 2026-04-22
---

This wiki is a generated architectural companion for MrWhoOidc. It is useful for navigation and synthesis, but source code and curated docs remain the source of truth.

## Scope

- Capture structural knowledge about the repo: major projects, runtime surfaces, deployment modes, testing strategy, and cross-cutting OIDC concepts.
- Keep generated wiki pages separate from `docs/`, which remains the human-authored documentation set.
- Prefer documenting architectural shifts and public behavior over trivial implementation churn.

## Directory Layout

```text
wiki/
├── index.md
├── log.md
├── schema.md
├── overview.md
├── concepts/
├── entities/
└── queries/
```

## Page Types

- `overview`: repository-wide summary, architecture diagram, run modes, and key decisions.
- `concept`: cross-cutting behavior such as protocol flow, deployment, testing, or logout handling.
- `entity`: a major project, subsystem, executable surface, or documentation corpus.
- `query`: a filed synthesis answer that is worth keeping.

## Frontmatter

Every wiki page should use YAML frontmatter with these fields when applicable:

```yaml
---
title: Page Title
type: overview | concept | entity | query | schema
tags: [tag1, tag2]
created: YYYY-MM-DD
updated: YYYY-MM-DD
related_files: [path/to/file]
---
```

## Writing Rules

- Start each page with a short summary paragraph.
- Use `[[wikilinks]]` between wiki pages.
- Keep file references in `related_files` current.
- Prefer naming pages after stable concepts or project names, not temporary branch work.
- When code and wiki disagree, update the wiki.
- Do not edit `docs/` solely to mirror wiki content.

## Index Rules

- `index.md` is the entry point.
- Organize entries by category: overview, concepts, entities, queries.
- Each entry should include a one-line summary and last update date.

## Log Rules

- `log.md` is append-only.
- Each entry starts with `## [YYYY-MM-DD] operation | Title`.
- Valid operations here are `init`, `ingest`, `query`, and `lint`.
- Entries should record the trigger, pages created or updated, and any thin areas left behind.

## Repo-Specific Priorities

- Core projects: `MrWhoOidc.Auth`, `MrWhoOidc.WebAuth`, `MrWhoOidc.Security`, `MrWhoOidc.AppHost`, `MrWhoOidc.Cli`, `MrWhoOidc.ApiService`.
- Operational surfaces: Docker Compose files, Aspire host wiring, seeded local development paths, bootstrap behavior.
- Test surfaces: `MrWhoOidc.UnitTests` and the Python Playwright suite under `e2e/`.
- Important docs: `README.md`, `docs/index.md`, `docs/developer-guide.md`, `docs/deployment-guide.md`, `docs/admin-guide.md`, and ADR/reference material.

## Bootstrap and Refresh Heuristics

- Bootstrap should create the overview, a small set of concept pages, and entity pages for major projects.
- Refresh should focus on structural edits: new modules, changed endpoints, changed flows, changed deployment shapes, and changed tests.
- Skip noise such as formatting-only edits, comment-only edits, and trivial text fixes unless the user explicitly asks to capture them.