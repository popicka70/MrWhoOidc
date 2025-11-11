# Implementation Plan: WebAuth UI Unification

**Branch**: `005-webauth-ui-unification` | **Date**: November 11, 2025 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `/specs/005-webauth-ui-unification/spec.md`

**Note**: This template is filled in by the `/speckit.plan` command. See `.specify/templates/commands/plan.md` for the execution workflow.

## Summary

Unify the MrWhoOidc.WebAuth UI by establishing a consistent design system with centralized CSS variables, reusable component classes, and elimination of inline styles. This refactoring will create a cohesive visual experience across all pages (login, consent, admin dashboards) while improving maintainability through centralized style management. The approach involves auditing existing pages, extracting common patterns, defining design tokens in CSS custom properties, creating reusable component classes, and systematically replacing inline styles.

## Technical Context

**Language/Version**: C# / .NET 9  
**Primary Dependencies**: ASP.NET Core Razor Pages, Bootstrap 5, Bootstrap Icons, CSS3 Custom Properties  
**Storage**: N/A (UI refactoring only, no data model changes)  
**Testing**: Manual visual testing, screenshot comparison, CSS validation, accessibility checks  
**Target Platform**: Web browsers (Chrome, Firefox, Safari, Edge) - responsive design for mobile, tablet, desktop  
**Project Type**: Web application (frontend refactoring within MrWhoOidc.WebAuth)  
**Performance Goals**: No performance impact (CSS-only changes), maintain or improve page load times  
**Constraints**: 
- Must maintain existing functionality (no behavioral changes)
- Must preserve tenant branding capability (custom logos/colors)
- Must remain compatible with Bootstrap 5 utility classes
- Must not break responsive layouts
- Must maintain accessibility compliance (WCAG 2.1 AA)

**Scale/Scope**: 
- ~170+ Razor Pages (.cshtml files) in MrWhoOidc.WebAuth
- ~30+ inline style occurrences to refactor
- Main CSS file: `MrWhoOidc.WebAuth/wwwroot/css/site.css` (~1092 lines currently)
- Component patterns: 5-7 common UI patterns (page headers, tables, forms, alerts, auth cards)

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

**Build Quality Gates**:

- [x] Zero compiler warnings (Debug and Release configurations) - No C# code changes, CSS only
- [x] Zero analyzer warnings (unless documented suppressions in place) - No C# code changes
- [x] All tests pass without warnings - No test changes required (UI-only refactoring)
- [x] EF Core migrations generated using `dotnet ef migrations add` (not hand-written) - N/A (no data model changes)
- [x] Entity primary keys use `GuidHelper.NewId()` (not `Guid.NewGuid()`) - N/A (no entity changes)
- [x] OIDC specification compliance validated with RFC references in tests - N/A (no protocol changes)

**Architecture Gates**:

- [x] Domain logic in MrWhoOidc.Auth - N/A (UI-only changes in MrWhoOidc.WebAuth)
- [x] HTTP surface in MrWhoOidc.WebAuth - All changes confined to WebAuth project
- [x] No OpenIddict/Microsoft Identity Platform dependencies - No new dependencies introduced
- [x] Bootstrap 5 compatibility maintained - Refactoring extends Bootstrap, not replaces it

**Documentation Gates**:

- [x] Markdown formatting follows standards (MD022, MD032, MD040, MD047) - Will be validated
- [x] Documentation updates for user-facing changes - Design system guide will be created

**Status**: ✅ PASSED - No constitution violations. This is a pure UI refactoring within WebAuth project boundaries.

## Project Structure

### Documentation (this feature)

```text
specs/005-webauth-ui-unification/
├── plan.md              # This file (/speckit.plan command output)
├── research.md          # Phase 0 output: CSS best practices, design system patterns
├── data-model.md        # N/A (no data model changes)
├── quickstart.md        # Phase 1 output: Developer guide for using the design system
├── contracts/           # N/A (no API contracts)
└── checklists/
    └── requirements.md  # Quality checklist (completed)
```

### Source Code (repository root)

```text
MrWhoOidc.WebAuth/
├── wwwroot/
│   └── css/
│       ├── site.css              # Main CSS file (to be refactored and expanded)
│       └── design-system.css     # New: Centralized design tokens and component classes
├── Pages/
│   ├── Shared/
│   │   ├── _Layout.cshtml        # Main layout (update to use design system classes)
│   │   ├── _AuthLayout.cshtml    # Auth pages layout (update classes)
│   │   └── *.cshtml              # Shared partials (update to use component classes)
│   ├── Login.cshtml              # Auth pages (remove inline styles)
│   ├── Consent.cshtml
│   ├── Admin/
│   │   ├── Users/
│   │   │   └── Index.cshtml      # Admin pages (standardize table/form classes)
│   │   ├── Clients/
│   │   │   └── Index.cshtml
│   │   └── *.cshtml
│   └── Account/
│       └── *.cshtml
└── docs/
    └── design-system-guide.md    # New: Documentation for design system usage
```

**Structure Decision**: This is a web application frontend refactoring within the existing MrWhoOidc.WebAuth project. No new projects or backend changes required. All work focuses on the presentation layer (CSS and Razor markup).

## Complexity Tracking

**Status**: No violations - This feature does not introduce architectural complexity.

This is a pure UI refactoring project with no violations of the constitution. No new projects, dependencies, or architectural patterns are introduced. All work is confined to CSS and Razor markup within the existing MrWhoOidc.WebAuth project.

## Phase Execution Summary

### Phase 0: Research (✅ COMPLETED)

**Objectives**: Identify CSS best practices, design system patterns, and refactoring strategies

**Artifacts**:

- ✅ `research.md` - Comprehensive research on CSS custom properties, BEM methodology, responsive patterns, icon standardization, and accessibility
- ✅ Technology decisions documented (CSS variables, component naming, responsive strategy)
- ✅ Implementation patterns defined for all major component types

**Key Decisions**:

- Use CSS custom properties for all design tokens (colors, spacing, typography, shadows, transitions)
- Apply BEM methodology for component class naming
- Mobile-first responsive approach using Bootstrap breakpoints
- Systematic three-phase inline style elimination strategy
- Standard icon size and color classes

### Phase 1: Design & Contracts (✅ COMPLETED)

**Objectives**: Define component API, create developer documentation, establish design system structure

**Artifacts**:

- ✅ `data-model.md` - Confirmed no data model changes (UI-only refactoring)
- ✅ `quickstart.md` - Comprehensive developer guide with:
  - Design token reference (colors, spacing, typography, shadows)
  - Component class documentation (page headers, tables, forms, auth cards)
  - Icon utilities (sizes and colors)
  - Responsive patterns and best practices
  - Complete page examples (admin pages and auth pages)
  - Migration guide from inline styles to classes
- ✅ Agent context updated via `update-agent-context.ps1`

**Component Inventory** (from quickstart):

1. Page Header Component (`.page-header`, `.page-header__title`, etc.)
2. Data Table Component (`.data-table`, `.data-table__row`)
3. Auth Card Component (`.auth-card`, `.auth-card__header`, etc.)
4. Form Group patterns (leveraging Bootstrap classes)
5. Alert Messages (standardized with icons)
6. Action Button Groups (`.action-buttons`)
7. Icon utilities (`.icon-{size}`, `.icon-{color}`)
8. Layout containers (`.auth-container`, `.content-container`)

**Design Token Categories** (30+ tokens defined):

- Colors (8 semantic colors)
- Spacing (7-level scale)
- Typography (font families, sizes, weights)
- Border radius (4 levels)
- Shadows (3 depths)
- Transitions (2 speeds)

### Phase 2: Task Planning (⏳ PENDING)

**Next Step**: Run `/speckit.tasks` to generate implementation tasks

This phase will create `tasks.md` with:

- Detailed refactoring tasks organized by component type
- File-by-file migration checklist
- Testing and validation steps
- Acceptance criteria for each task

## Constitution Re-Check (Post-Design)

**Status**: ✅ PASSED

All gates continue to pass after Phase 1 design:

- No architectural violations introduced
- No new dependencies added
- Design system follows modern CSS best practices
- Component patterns align with industry standards (BEM, mobile-first)
- Documentation complete and comprehensive
- Accessibility considerations documented (WCAG 2.1 AA)
- Responsive design maintains Bootstrap compatibility

## Implementation Readiness

**Status**: ✅ READY FOR TASK PLANNING

All research and design phases are complete. The feature is ready for detailed task breakdown via `/speckit.tasks` command.

**Deliverables Created**:

1. ✅ Implementation plan (`plan.md`)
2. ✅ Research documentation (`research.md`)
3. ✅ Developer quickstart guide (`quickstart.md`)
4. ✅ Data model analysis (`data-model.md`)
5. ✅ Agent context updated
6. ✅ Constitution compliance verified

**Next Steps**:

1. Run `/speckit.tasks` to generate detailed implementation tasks
2. Create `design-system.css` with all CSS custom properties and component classes
3. Begin systematic refactoring of Razor pages
4. Update `_Layout.cshtml` and `_AuthLayout.cshtml` to reference design system
5. Create visual regression testing baseline (screenshots)
6. Document component usage patterns in `/docs/design-system-guide.md`

## Notes

- This refactoring is purely visual/presentational - no functional changes
- All existing tests should continue to pass without modification
- Manual visual testing required to verify consistency
- Consider capturing "before" screenshots for comparison
- Tenant branding override capability must be preserved and tested
- Accessibility compliance (WCAG 2.1 AA) must be maintained throughout refactoring

