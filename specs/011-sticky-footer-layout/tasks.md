---
description: "Task list for Sticky Footer Layout implementation"
---

# Tasks: Sticky Footer Layout

**Input**: Design documents from `/specs/011-sticky-footer-layout/`
**Prerequisites**: plan.md (required), spec.md (required), research.md, data-model.md, contracts/, quickstart.md

**Tests**: No automated tests requested in spec; validation is manual per quickstart.md.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Include exact file paths in descriptions


---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Prepare the workspace for safe, repeatable UI verification.

- [X] T001 Create verification notes file at specs/011-sticky-footer-layout/artifacts/verification-notes.md
- [ ] T002 Capture baseline screenshots for footer behavior on 3 representative pages and link them from specs/011-sticky-footer-layout/artifacts/verification-notes.md
- [X] T003 Identify the shared layouts in use via MrWhoOidc.WebAuth/Pages/_ViewStart.cshtml and confirm which pages use MrWhoOidc.WebAuth/Pages/Shared/_AuthLayout.cshtml
- [X] T004 [P] Review footer CSS sources and note conflicts in MrWhoOidc.WebAuth/Pages/Shared/_Layout.cshtml.css and MrWhoOidc.WebAuth/wwwroot/css/site.css (record findings in specs/011-sticky-footer-layout/artifacts/verification-notes.md)

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Establish a single, centralized layout mechanism that all user stories depend on.

**⚠️ CRITICAL**: No user story work should begin until this phase is complete.

- [X] T005 Remove absolute footer positioning in MrWhoOidc.WebAuth/Pages/Shared/_Layout.cshtml.css (eliminate `.footer { position: absolute; bottom: 0; ... }`)
- [X] T006 Add a shared “page shell” CSS class for a 3-row grid layout in MrWhoOidc.WebAuth/wwwroot/css/site.css (header / scrollable content / footer)
- [X] T007 [P] Add a shared “scroll container” CSS class with appropriate `overflow-y`, `min-height`, and `scroll-padding-bottom` in MrWhoOidc.WebAuth/wwwroot/css/site.css
- [X] T008 Confirm footer styles are defined only once and do not conflict between CSS isolation and global CSS (MrWhoOidc.WebAuth/Pages/Shared/_Layout.cshtml.css, MrWhoOidc.WebAuth/wwwroot/css/site.css)

**Checkpoint**: Foundation ready — user story work can now begin.

---

## Phase 3: User Story 1 - Footer is always visible (Priority: P1) 🎯 MVP

**Goal**: Footer is visible immediately on load across MrWhoOidc.WebAuth pages.

**Independent Test**: At 1024x700 or larger, open multiple pages and confirm the footer is visible without scrolling.

### Implementation for User Story 1

- [X] T009 [US1] Wrap MrWhoOidc.WebAuth/Pages/Shared/_Layout.cshtml body structure in the new “page shell” wrapper (header/content/footer as grid rows)
- [X] T010 [US1] Move the main content area in MrWhoOidc.WebAuth/Pages/Shared/_Layout.cshtml into the dedicated scroll container so the footer remains visible
- [X] T011 [US1] Ensure the unauthenticated layout branch in MrWhoOidc.WebAuth/Pages/Shared/_Layout.cshtml also uses the same page shell and scroll container
- [X] T012 [US1] Ensure the footer element in MrWhoOidc.WebAuth/Pages/Shared/_Layout.cshtml is placed as the final grid row and remains visible without fixed positioning
- [X] T013 [US1] Record US1 verification results (1024x700+) in specs/011-sticky-footer-layout/artifacts/verification-notes.md

**Checkpoint**: User Story 1 functional and independently verifiable.

---

## Phase 4: User Story 2 - Content never hides behind the footer (Priority: P2)

**Goal**: No page content is obscured by the footer; last rows/buttons are always reachable.

**Independent Test**: On a long content page, scroll to the end and confirm the last interactive controls are fully visible and clickable.

### Implementation for User Story 2

- [X] T014 [US2] Audit and remove layout-level “compensating” padding/margins that were previously used to avoid overlap (e.g., bottom padding on main) in MrWhoOidc.WebAuth/Pages/Shared/_Layout.cshtml
- [X] T015 [US2] Ensure the scroll container in MrWhoOidc.WebAuth/wwwroot/css/site.css provides sufficient bottom scroll-padding so focused elements are not hidden behind the footer
- [ ] T016 [US2] Validate long-table pages render fully to the bottom inside the scroll container (no overlap) and note the page used in specs/011-sticky-footer-layout/artifacts/verification-notes.md
- [ ] T017 [US2] Validate edit pages with bottom action buttons remain fully visible and usable without per-page hacks and note the page used in specs/011-sticky-footer-layout/artifacts/verification-notes.md
- [ ] T018 [US2] Verify keyboard navigation: tab to bottom controls and footer links without focus being hidden (MrWhoOidc.WebAuth/Pages/Shared/_Layout.cshtml + MrWhoOidc.WebAuth/wwwroot/css/site.css) and record results in specs/011-sticky-footer-layout/artifacts/verification-notes.md

**Checkpoint**: User Stories 1 and 2 both work without per-page fixes.

---

## Phase 5: User Story 3 - Works well on mobile (Priority: P3)

**Goal**: Layout remains usable down to 360px width without horizontal scrolling; footer stays visible.

**Independent Test**: At 360px width, scroll content and confirm footer remains visible, content is readable, and no horizontal scrolling is required.

### Implementation for User Story 3

- [X] T019 [US3] Ensure the page shell height uses mobile-friendly viewport sizing in MrWhoOidc.WebAuth/wwwroot/css/site.css (prefer dynamic viewport behavior where supported)
- [ ] T020 [US3] Validate offcanvas sidebar + scroll container interaction on mobile (navigation still usable; content still scrollable) in MrWhoOidc.WebAuth/Pages/Shared/_Layout.cshtml and record results in specs/011-sticky-footer-layout/artifacts/verification-notes.md
- [ ] T021 [US3] Validate no horizontal scrolling at 360px width on representative admin pages and record pages/results in specs/011-sticky-footer-layout/artifacts/verification-notes.md

**Checkpoint**: All user stories verified on mobile constraints.

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Ensure the solution is consistent, maintainable, and meets build quality gates.

- [X] T022 [P] Update any documentation note about the old footer workaround (if present) in docs/done/footer-overlap-fix-revised.md to reflect the new centralized approach
- [ ] T023 Run the full manual verification checklist in specs/011-sticky-footer-layout/quickstart.md and record completion in specs/011-sticky-footer-layout/artifacts/verification-notes.md
- [X] T024 Ensure build quality gates are met by running `dotnet build` for MrWhoOidc.slnx
- [X] T025 Ensure test suite passes by running `dotnet test` for MrWhoOidc.slnx

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies
- **Foundational (Phase 2)**: Depends on Setup completion — BLOCKS all user stories
- **User Story 1 (Phase 3)**: Depends on Phase 2
- **User Story 2 (Phase 4)**: Depends on Phase 3 (because overlap checks assume the new shell exists)
- **User Story 3 (Phase 5)**: Depends on Phase 3 (mobile validation assumes the new shell exists)
- **Polish (Phase 6)**: Depends on desired user stories completion

### User Story Dependencies

- **US1 (P1)**: Base layout wrapper + always-visible footer
- **US2 (P2)**: Builds on US1 to guarantee no overlap in edge cases and keyboard focus scenarios
- **US3 (P3)**: Builds on US1 to ensure mobile viewport behavior is correct

---

## Parallel Opportunities

- Phase 1: T001, T002, T003 can run in parallel.
- Phase 2: T006 and T007 can be done in parallel once T005 is understood.
- Phase 6: T022 can run in parallel with manual verification (T023).

---

## Parallel Example: User Story 1

```text
Task: "Wrap body structure in page shell in MrWhoOidc.WebAuth/Pages/Shared/_Layout.cshtml" (T009)
Task: "Move content into scroll container in MrWhoOidc.WebAuth/Pages/Shared/_Layout.cshtml" (T010)
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1 → Phase 2
2. Implement US1 (Phase 3)
3. Validate US1 quickly on 2–3 pages at 1024x700+

### Incremental Delivery

1. US1 → validate “always visible”
2. US2 → validate “no overlap” and keyboard focus
3. US3 → validate mobile viewport behavior

---

## Notes

- Tasks are intentionally centralized around shared layout + CSS to satisfy FR-005 (avoid page-by-page special casing).
- No automated tests are included because the spec does not request them; rely on quickstart.md manual checks.
