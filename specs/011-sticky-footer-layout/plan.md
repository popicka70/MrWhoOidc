# Implementation Plan: Sticky Footer Layout

**Branch**: `011-sticky-footer-layout` | **Date**: 2025-12-15 | **Spec**: [spec.md](spec.md)
**Input**: Feature specification from `C:\Users\rum2c\source\repos\MrWhoOidc\specs\011-sticky-footer-layout\spec.md`

## Summary

Make the MrWhoOidc.WebAuth footer (bottom bar) always visible and non-overlapping across pages by introducing a single, centralized page-shell layout that uses a grid-based structure (header / scrollable content / footer). Remove conflicting absolute footer styling so the fix is applied once and inherited everywhere.

## Technical Context

**Language/Version**: C# / .NET 9 (Razor Pages)
**Primary Dependencies**: ASP.NET Core Razor Pages, Bootstrap (CSS/JS), Bootstrap Icons, repo Fluent design CSS (design-system.css, fluent-base.css)
**Storage**: N/A (layout-only change)
**Testing**: MSTest present in solution; for this feature, validation is primarily manual UI verification across representative pages + viewport sizes
**Target Platform**: Modern browsers (desktop + mobile)
**Project Type**: Web application (Razor Pages)
**Performance Goals**: No measurable performance change expected; layout should remain responsive
**Constraints**: No footer/content overlap; footer visible without scrolling at 1024x700+; layout usable down to 360px width
**Scale/Scope**: MrWhoOidc.WebAuth shared layouts only; no protocol or persistence behavior changes

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

Applicable constitution principles for this feature:

- Separation of concerns: UI/layout changes remain within MrWhoOidc.WebAuth.
- Zero-warning policy: solution builds with zero warnings.

**Build Quality Gates**:

- [ ] Zero compiler warnings (Debug and Release configurations)
- [ ] Zero analyzer warnings (unless documented suppressions in place)
- [ ] All tests pass without warnings
- [ ] EF Core migrations generated using `dotnet ef migrations add` (not hand-written) (N/A)
- [ ] Entity primary keys use `GuidHelper.NewId()` (N/A)
- [ ] OIDC specification compliance validated with RFC references in tests (N/A)

## Project Structure

### Documentation (this feature)

```
specs/[###-feature]/
├── plan.md              # This file (/speckit.plan command output)
├── research.md          # Phase 0 output (/speckit.plan command)
├── data-model.md        # Phase 1 output (/speckit.plan command)
├── quickstart.md        # Phase 1 output (/speckit.plan command)
├── contracts/           # Phase 1 output (/speckit.plan command)
└── tasks.md             # Phase 2 output (/speckit.tasks command - NOT created by /speckit.plan)
```

### Source Code (repository root)
```
MrWhoOidc.WebAuth/
├── Pages/
│   └── Shared/
│       ├── _Layout.cshtml          # Main app layout (authenticated + public)
│       ├── _Layout.cshtml.css      # Layout-scoped CSS (currently affects footer positioning)
│       └── _AuthLayout.cshtml      # Auth pages layout
└── wwwroot/
    └── css/
        └── site.css               # Global UI styles (includes footer styling)
```

**Structure Decision**: WebAuth Razor Pages layout + CSS-only changes.

## Phase 0: Outline & Research

Research goals (resolve unknowns before design):

1. Identify the exact source(s) of footer overlap and “not visible without scrolling” behavior.
2. Confirm the shared layout(s) responsible for footer rendering across the app.
3. Select a layout strategy that guarantees a visible footer without overlapping content on both desktop and mobile.

Planned research tasks:

- Inspect the shared layout(s) and current footer markup.
- Inspect global CSS and Razor CSS-isolation output for footer-related positioning.
- Validate whether existing “body padding” workaround is still present and why it doesn’t meet the “always visible” requirement.

Research conclusion (from `research.md`):

- Footer overlap is driven by `.footer { position: absolute; bottom: 0; }` in `MrWhoOidc.WebAuth\Pages\Shared\_Layout.cshtml.css`.
- The documented “body padding” workaround reduces overlap but cannot satisfy “footer always visible”.

**Phase 0 Output**: `C:\Users\rum2c\source\repos\MrWhoOidc\specs\011-sticky-footer-layout\research.md` (complete)

## Phase 1: Design & Contracts

### Design Goals

- Footer is always visible (no scrolling needed to reach it).
- Page content is scrollable and never obscured by the footer.
- One centralized solution applied via shared layouts.
- Responsive down to 360px width.

### Proposed Design (to be validated by research)

Validated design choice:

- Introduce a “page shell” structure in shared layouts that uses a 3-row grid: header / content / footer.
- Make the content region the scroll container (so footer stays visible) and ensure nested Bootstrap layout still behaves (e.g., sidebar `h-100` usage).
- Remove the absolute footer positioning in `MrWhoOidc.WebAuth\Pages\Shared\_Layout.cshtml.css` so footer no longer overlaps content.

### Contracts

This is a UI layout-only change; no new HTTP endpoints or API contracts are required.

**Phase 1 Outputs**:

- `C:\Users\rum2c\source\repos\MrWhoOidc\specs\011-sticky-footer-layout\data-model.md` (complete)
- `C:\Users\rum2c\source\repos\MrWhoOidc\specs\011-sticky-footer-layout\contracts\README.md` (complete)
- `C:\Users\rum2c\source\repos\MrWhoOidc\specs\011-sticky-footer-layout\quickstart.md` (complete)

## Phase 1: Agent Context Update

Run: `C:\Users\rum2c\source\repos\MrWhoOidc\.specify\scripts\powershell\update-agent-context.ps1 -AgentType copilot`

## Post-Design Constitution Re-check

- Scope remains within MrWhoOidc.WebAuth.
- No OIDC/protocol logic changes.
- Ensure build remains warning-free after implementation.

## Phase 2: Implementation Planning (stops after planning)

Implementation steps (high-level):

1. Update shared layouts to adopt a consistent page-shell wrapper.
2. Update CSS so the footer is not absolutely positioned and the content area becomes the scroll container.
3. Verify both authenticated (sidebar + main content) and unauthenticated layouts behave correctly.
4. Manual verification on representative pages (long table pages, forms with bottom buttons, short pages) at desktop + mobile viewport sizes.

## Complexity Tracking

*Fill ONLY if Constitution Check has violations that must be justified*

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| [e.g., 4th project] | [current need] | [why 3 projects insufficient] |
| [e.g., Repository pattern] | [specific problem] | [why direct DB access insufficient] |
