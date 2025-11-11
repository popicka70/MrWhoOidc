# Tasks: WebAuth UI Unification

**Input**: Design documents from `/specs/005-webauth-ui-unification/`  
**Prerequisites**: plan.md, spec.md, research.md, quickstart.md, data-model.md

**Tests**: This feature does NOT include automated test tasks. Testing is manual visual verification and screenshot comparison.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Include exact file paths in descriptions

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Create design system CSS file and baseline documentation

- [ ] T001 Create design-system.css file in MrWhoOidc.WebAuth/wwwroot/css/design-system.css
- [ ] T002 Add design-system.css reference to MrWhoOidc.WebAuth/Pages/Shared/_Layout.cshtml
- [ ] T003 Add design-system.css reference to MrWhoOidc.WebAuth/Pages/Shared/_AuthLayout.cshtml
- [ ] T004 [P] Create design-system-guide.md in MrWhoOidc.WebAuth/docs/design-system-guide.md
- [ ] T005 [P] Capture baseline screenshots of 10 representative pages (Login, Consent, Admin Users, Admin Clients, Admin Roles, Account Profile, SelectTenant, Error, NotFound, Index)

---

## Phase 2: Foundational (Design Tokens & Core Components)

**Purpose**: CSS custom properties and core component classes that ALL user stories depend on

**⚠️ CRITICAL**: No user story refactoring can begin until this phase is complete

### CSS Custom Properties (Design Tokens)

- [ ] T006 Define color variables (--color-primary, --color-secondary, --color-success, --color-danger, --color-warning, --color-info, --color-light, --color-dark) in MrWhoOidc.WebAuth/wwwroot/css/design-system.css
- [ ] T007 [P] Define spacing scale variables (--space-xs through --space-3xl, 7 values) in MrWhoOidc.WebAuth/wwwroot/css/design-system.css
- [ ] T008 [P] Define typography variables (--font-family-base, --font-size-*, --font-weight-*) in MrWhoOidc.WebAuth/wwwroot/css/design-system.css
- [ ] T009 [P] Define border radius variables (--radius-sm through --radius-xl) in MrWhoOidc.WebAuth/wwwroot/css/design-system.css
- [ ] T010 [P] Define shadow variables (--shadow-sm, --shadow-md, --shadow-lg) in MrWhoOidc.WebAuth/wwwroot/css/design-system.css
- [ ] T011 [P] Define transition variables (--transition-base, --transition-fast) in MrWhoOidc.WebAuth/wwwroot/css/design-system.css

### Core Component Classes

- [ ] T012 [P] Implement page header component classes (.page-header, .page-header__content, .page-header__title, .page-header__subtitle, .page-header__actions) in MrWhoOidc.WebAuth/wwwroot/css/design-system.css
- [ ] T013 [P] Implement data table component classes (.data-table, .data-table__row with hover states) in MrWhoOidc.WebAuth/wwwroot/css/design-system.css
- [ ] T014 [P] Implement auth card component classes (.auth-card, .auth-card__header, .auth-card__logo, .auth-card__title, .auth-card__subtitle) in MrWhoOidc.WebAuth/wwwroot/css/design-system.css
- [ ] T015 [P] Implement form group component classes (.form-group with consistent spacing) in MrWhoOidc.WebAuth/wwwroot/css/design-system.css
- [ ] T016 [P] Implement action button group classes (.action-buttons with flex gap) in MrWhoOidc.WebAuth/wwwroot/css/design-system.css
- [ ] T017 [P] Implement icon size utility classes (.icon-xs through .icon-2xl, 6 sizes) in MrWhoOidc.WebAuth/wwwroot/css/design-system.css
- [ ] T018 [P] Implement icon color utility classes (.icon-primary, .icon-success, .icon-danger, .icon-warning, .icon-muted) in MrWhoOidc.WebAuth/wwwroot/css/design-system.css
- [ ] T019 [P] Implement layout container classes (.auth-container max-width 420px, .content-container max-width 750px) in MrWhoOidc.WebAuth/wwwroot/css/design-system.css
- [ ] T020 [P] Add responsive media queries for page-header, data-table, and auth-card components in MrWhoOidc.WebAuth/wwwroot/css/design-system.css
- [ ] T021 [P] Add accessibility features (focus states, reduced motion support, high contrast mode) in MrWhoOidc.WebAuth/wwwroot/css/design-system.css
- [ ] T022 [P] Add CSS documentation comments for each section (variables, components, utilities) in MrWhoOidc.WebAuth/wwwroot/css/design-system.css

**Checkpoint**: Design system CSS is complete - page refactoring can now begin in parallel

---

## Phase 3: User Story 1 - Consistent Visual Experience Across All Pages (Priority: P1) 🎯 MVP

**Goal**: Establish visual consistency across representative page types (auth pages, admin pages, public pages) by applying design system classes to eliminate inline styles and ensure unified colors, fonts, spacing, and component styling.

**Independent Test**: Navigate through Login → Consent → Admin Users → Admin Clients → Settings pages and verify fonts, colors, button styles, card designs, spacing, and icon usage are identical across all pages.

### Auth Pages Refactoring (Login Flow)

- [ ] T023 [P] [US1] Refactor Login.cshtml: Replace inline styles with .auth-card, .auth-card__logo, .auth-card__title classes in MrWhoOidc.WebAuth/Pages/Login.cshtml
- [ ] T024 [P] [US1] Refactor Consent.cshtml: Replace card inline styles with design system classes in MrWhoOidc.WebAuth/Pages/Consent.cshtml
- [ ] T025 [P] [US1] Refactor LoginTotp.cshtml: Apply .auth-card and .form-group classes in MrWhoOidc.WebAuth/Pages/LoginTotp.cshtml
- [ ] T026 [P] [US1] Refactor SelectTenant.cshtml: Replace inline styles (font-size, width, height) with .icon-* and layout classes in MrWhoOidc.WebAuth/Pages/SelectTenant.cshtml
- [ ] T027 [P] [US1] Refactor DiscoverTenant.cshtml: Apply .auth-card and .icon-xl classes in MrWhoOidc.WebAuth/Pages/DiscoverTenant.cshtml

### Admin Pages Refactoring (User Management)

- [ ] T028 [P] [US1] Refactor Admin/Users/Index.cshtml: Apply .page-header, .data-table, .action-buttons classes, remove inline styles in MrWhoOidc.WebAuth/Pages/Admin/Users/Index.cshtml
- [ ] T029 [P] [US1] Refactor Admin/Users/Edit.cshtml: Apply .form-group and button classes consistently in MrWhoOidc.WebAuth/Pages/Admin/Users/Edit.cshtml
- [ ] T030 [P] [US1] Refactor Admin/Users/Add.cshtml: Apply .form-group classes in MrWhoOidc.WebAuth/Pages/Admin/Users/Add.cshtml
- [ ] T031 [P] [US1] Refactor Admin/Clients/Index.cshtml: Apply .page-header and .data-table classes in MrWhoOidc.WebAuth/Pages/Admin/Clients/Index.cshtml
- [ ] T032 [P] [US1] Refactor Admin/Roles/Index.cshtml: Apply .page-header and consistent button styling in MrWhoOidc.WebAuth/Pages/Admin/Roles/Index.cshtml

### Error & Public Pages Refactoring

- [ ] T033 [P] [US1] Refactor Error.cshtml: Replace inline icon size styles with .icon-2xl and .icon-danger classes in MrWhoOidc.WebAuth/Pages/Error.cshtml
- [ ] T034 [P] [US1] Refactor NotFound.cshtml: Replace inline styles with .icon-2xl and .icon-warning classes in MrWhoOidc.WebAuth/Pages/NotFound.cshtml
- [ ] T035 [P] [US1] Refactor Index.cshtml (home page): Standardize image sizing with classes in MrWhoOidc.WebAuth/Pages/Index.cshtml
- [ ] T036 [P] [US1] Refactor Privacy.cshtml: Apply consistent typography classes in MrWhoOidc.WebAuth/Pages/Privacy.cshtml

### Shared Layouts & Partials

- [ ] T037 [US1] Update _Layout.cshtml: Ensure logo sizing, navigation, footer use design system classes in MrWhoOidc.WebAuth/Pages/Shared/_Layout.cshtml
- [ ] T038 [US1] Update _AuthLayout.cshtml: Apply .auth-container class, remove inline width styles in MrWhoOidc.WebAuth/Pages/Shared/_AuthLayout.cshtml
- [ ] T039 [P] [US1] Update _ImpersonationBanner.cshtml: Standardize alert styling in MrWhoOidc.WebAuth/Pages/Shared/_ImpersonationBanner.cshtml
- [ ] T040 [P] [US1] Update _TenantContextBanner.cshtml: Apply consistent badge and alert classes in MrWhoOidc.WebAuth/Pages/Shared/_TenantContextBanner.cshtml

### Visual Verification

- [ ] T041 [US1] Capture post-refactoring screenshots of 10 representative pages (same as baseline T005)
- [ ] T042 [US1] Compare before/after screenshots: Verify fonts, colors, spacing, button styles are identical across Login, Consent, Admin Users, Admin Clients, Admin Roles, Account Profile, SelectTenant, Error, NotFound, Index
- [ ] T043 [US1] Test responsive behavior on mobile (375px), tablet (768px), and desktop (1200px) viewports for all 10 pages
- [ ] T044 [US1] Verify tenant branding override works: Test with custom logo and color on Login and SelectTenant pages

**Checkpoint**: At this point, core page types have consistent visual styling, User Story 1 is complete and independently testable

---

## Phase 4: User Story 2 - Centralized Style Management (Priority: P2)

**Goal**: Eliminate all remaining inline styles across the application and ensure all style values reference CSS custom properties, enabling global design token updates.

**Independent Test**: Change primary color variable in design-system.css from blue to orange and verify all buttons, links, icons, and branded elements reflect the new color across every page without modifying page files.

### Account Management Pages

- [ ] T045 [P] [US2] Refactor Account/Index.cshtml: Replace inline styles with design system classes in MrWhoOidc.WebAuth/Pages/Account/Index.cshtml
- [ ] T046 [P] [US2] Refactor Account/Profile.cshtml: Apply .form-group and consistent spacing classes in MrWhoOidc.WebAuth/Pages/Account/Profile.cshtml
- [ ] T047 [P] [US2] Refactor Account/Sessions.cshtml: Apply .data-table classes in MrWhoOidc.WebAuth/Pages/Account/Sessions.cshtml
- [ ] T048 [P] [US2] Refactor Account/Consents.cshtml: Apply .data-table and action button classes in MrWhoOidc.WebAuth/Pages/Account/Consents.cshtml
- [ ] T049 [P] [US2] Refactor Account/Emails.cshtml: Apply consistent form and table styling in MrWhoOidc.WebAuth/Pages/Account/Emails.cshtml
- [ ] T050 [P] [US2] Refactor Account/WebAuthn.cshtml: Apply .form-group and button classes in MrWhoOidc.WebAuth/Pages/Account/WebAuthn.cshtml
- [ ] T051 [P] [US2] Refactor Account/LinkedAccounts.cshtml: Apply .data-table classes in MrWhoOidc.WebAuth/Pages/Account/LinkedAccounts.cshtml
- [ ] T052 [P] [US2] Refactor Account/ConfirmEmail.cshtml: Apply .auth-card classes in MrWhoOidc.WebAuth/Pages/Account/ConfirmEmail.cshtml
- [ ] T053 [P] [US2] Refactor Account/AccessDenied.cshtml: Apply .icon-* classes, remove inline styles in MrWhoOidc.WebAuth/Pages/Account/AccessDenied.cshtml

### Admin Configuration Pages

- [ ] T054 [P] [US2] Refactor Admin/Settings.cshtml: Apply .form-group and consistent button styling in MrWhoOidc.WebAuth/Pages/Admin/Settings.cshtml
- [ ] T055 [P] [US2] Refactor Admin/Backchannel/Index.cshtml: Apply .data-table classes in MrWhoOidc.WebAuth/Pages/Admin/Backchannel/Index.cshtml
- [ ] T056 [P] [US2] Refactor Admin/Clients/Edit.cshtml: Apply .form-group, remove inline styles in MrWhoOidc.WebAuth/Pages/Admin/Clients/Edit.cshtml
- [ ] T057 [P] [US2] Refactor Admin/Clients/Add.cshtml: Apply .form-group classes in MrWhoOidc.WebAuth/Pages/Admin/Clients/Add.cshtml
- [ ] T058 [P] [US2] Refactor Admin/Roles/Edit.cshtml: Apply .form-group classes in MrWhoOidc.WebAuth/Pages/Admin/Roles/Edit.cshtml
- [ ] T059 [P] [US2] Refactor Admin/Roles/Add.cshtml: Apply .form-group classes in MrWhoOidc.WebAuth/Pages/Admin/Roles/Add.cshtml

### Platform Admin Pages

- [ ] T060 [P] [US2] Refactor PlatformAdmin/Index.cshtml: Apply .page-header classes in MrWhoOidc.WebAuth/Pages/PlatformAdmin/Index.cshtml
- [ ] T061 [P] [US2] Refactor PlatformAdmin/Tenants/Index.cshtml: Apply .data-table, remove inline styles in MrWhoOidc.WebAuth/Pages/PlatformAdmin/Tenants/Index.cshtml
- [ ] T062 [P] [US2] Refactor PlatformAdmin/Tenants/Edit.cshtml: Replace inline image sizing with classes in MrWhoOidc.WebAuth/Pages/PlatformAdmin/Tenants/Edit.cshtml
- [ ] T063 [P] [US2] Refactor PlatformAdmin/Tenants/Create.cshtml: Apply .form-group classes in MrWhoOidc.WebAuth/Pages/PlatformAdmin/Tenants/Create.cshtml
- [ ] T064 [P] [US2] Refactor PlatformAdmin/Impersonation.cshtml: Apply consistent form styling in MrWhoOidc.WebAuth/Pages/PlatformAdmin/Impersonation.cshtml
- [ ] T065 [P] [US2] Refactor PlatformAdmin/ImpersonationHistory/Index.cshtml: Apply .data-table classes in MrWhoOidc.WebAuth/Pages/PlatformAdmin/ImpersonationHistory/Index.cshtml

### Logout & Auth Workflows

- [ ] T066 [P] [US2] Refactor Logout/Prompt/Index.cshtml: Apply .auth-card classes in MrWhoOidc.WebAuth/Pages/Logout/Prompt/Index.cshtml
- [ ] T067 [P] [US2] Refactor Logout/FederatedSignedOut.cshtml: Apply consistent button styling in MrWhoOidc.WebAuth/Pages/Logout/FederatedSignedOut.cshtml
- [ ] T068 [P] [US2] Refactor Logout/FederatedCallbackError.cshtml: Apply .icon-* and button classes in MrWhoOidc.WebAuth/Pages/Logout/FederatedCallbackError.cshtml
- [ ] T069 [P] [US2] Refactor Auth/External/Error.cshtml: Apply .auth-card and .icon-* classes in MrWhoOidc.WebAuth/Pages/Auth/External/Error.cshtml

### Misc Pages & Partials

- [ ] T070 [P] [US2] Refactor Registrations/Index.cshtml: Replace inline display styles with visibility classes in MrWhoOidc.WebAuth/Pages/Registrations/Index.cshtml
- [ ] T071 [P] [US2] Refactor Password/Index.cshtml: Apply .form-group classes in MrWhoOidc.WebAuth/Pages/Password/Index.cshtml
- [ ] T072 [P] [US2] Refactor Mfa/Index.cshtml: Replace inline image sizing with classes in MrWhoOidc.WebAuth/Pages/Mfa/Index.cshtml
- [ ] T073 [P] [US2] Refactor SwitchTenant.cshtml: Apply consistent styling in MrWhoOidc.WebAuth/Pages/SwitchTenant.cshtml
- [ ] T074 [P] [US2] Refactor StartImpersonation.cshtml: Apply .form-group classes in MrWhoOidc.WebAuth/Pages/StartImpersonation.cshtml
- [ ] T075 [P] [US2] Refactor StopImpersonation.cshtml: Apply .auth-card classes in MrWhoOidc.WebAuth/Pages/StopImpersonation.cshtml
- [ ] T076 [P] [US2] Update _AccountTabs.cshtml: Ensure tab styling uses design system classes in MrWhoOidc.WebAuth/Pages/Account/_AccountTabs.cshtml
- [ ] T077 [P] [US2] Update _UserTabs.cshtml: Ensure tab styling uses design system classes in MrWhoOidc.WebAuth/Pages/Admin/Users/_UserTabs.cshtml
- [ ] T078 [P] [US2] Update _WebAuthnSetup.cshtml: Apply .form-group classes in MrWhoOidc.WebAuth/Pages/Shared/_WebAuthnSetup.cshtml

### Inline Style Audit & Cleanup

- [ ] T079 [US2] Run grep search for all remaining inline styles: `style="` in all .cshtml files under MrWhoOidc.WebAuth/Pages/
- [ ] T080 [US2] Review grep results and identify dynamic vs static inline styles (dynamic = progress bars, database colors; static = all others)
- [ ] T081 [US2] Create CSS classes for any remaining static inline style patterns not covered by existing components
- [ ] T082 [US2] Replace all remaining static inline styles with appropriate CSS classes

### Centralization Verification

- [ ] T083 [US2] Update design-system.css: Add tenant branding CSS variable override example with comments in MrWhoOidc.WebAuth/wwwroot/css/design-system.css
- [ ] T084 [US2] Test global design token update: Change --color-primary in design-system.css and verify all pages reflect new color
- [ ] T085 [US2] Test global spacing update: Change --space-md in design-system.css and verify consistent spacing across pages
- [ ] T086 [US2] Test global border radius update: Change --radius-md in design-system.css and verify all cards/buttons reflect change
- [ ] T087 [US2] Document centralized style management in design-system-guide.md with examples in MrWhoOidc.WebAuth/docs/design-system-guide.md

**Checkpoint**: At this point, 95%+ inline styles eliminated, all design tokens centralized, User Stories 1 AND 2 are complete

---

## Phase 5: User Story 3 - Reusable Component Patterns (Priority: P3)

**Goal**: Ensure all common UI patterns (page headers, tables, forms, alerts, action buttons) are documented, tested across multiple pages, and ready for new feature development.

**Independent Test**: Create a new sample admin page using only documented component classes from quickstart.md without writing any inline styles or custom CSS.

### Component Pattern Verification

- [ ] T088 [P] [US3] Verify page header component: Confirm .page-header classes used on at least 3 pages (Users, Clients, Roles) with identical appearance
- [ ] T089 [P] [US3] Verify data table component: Confirm .data-table classes used on at least 3 pages (Users, Clients, Sessions) with consistent styling
- [ ] T090 [P] [US3] Verify auth card component: Confirm .auth-card classes used on at least 3 pages (Login, Consent, Error) with identical styling
- [ ] T091 [P] [US3] Verify form group component: Confirm .form-group classes used on at least 3 pages with consistent spacing
- [ ] T092 [P] [US3] Verify action button groups: Confirm .action-buttons classes used on at least 3 pages with consistent spacing
- [ ] T093 [P] [US3] Verify icon utilities: Confirm .icon-* size and color classes used consistently across 5+ pages
- [ ] T094 [P] [US3] Verify alert messages: Confirm alert component pattern (with icons) used consistently across pages with TempData messages

### Component Usage Audit

- [ ] T095 [US3] Run grep search to count usage of each component class (.page-header, .data-table, .auth-card, .form-group, .action-buttons) across all .cshtml files
- [ ] T096 [US3] Create component usage report: Document which component classes are used on which pages in design-system-guide.md
- [ ] T097 [US3] Identify component pattern gaps: List any pages not using component classes that should be

### New Page Template Creation

- [ ] T098 [P] [US3] Create admin page template example in design-system-guide.md showing complete page markup using only component classes
- [ ] T099 [P] [US3] Create auth page template example in design-system-guide.md showing login/consent pattern
- [ ] T100 [P] [US3] Create form page template example in design-system-guide.md showing CRUD pattern
- [ ] T101 [US3] Test new page creation: Build a sample admin page using only template patterns and component classes, no custom CSS

### Component Refinement

- [ ] T102 [US3] Review component responsive behavior: Test all components on mobile (375px), tablet (768px), desktop (1200px)
- [ ] T103 [US3] Enhance component accessibility: Verify focus states, keyboard navigation, ARIA labels on all components
- [ ] T104 [US3] Add component variation documentation: Document modifier classes for component variants (e.g., .page-header--compact) if needed
- [ ] T105 [US3] Update quickstart.md with any new component patterns discovered during refactoring

**Checkpoint**: All user stories complete - design system is fully documented and ready for team adoption

---

## Phase 6: Polish & Cross-Cutting Concerns

**Purpose**: Final improvements, documentation, and validation

### Documentation Finalization

- [ ] T106 [P] Complete design-system-guide.md with all component patterns, usage examples, and migration guide in MrWhoOidc.WebAuth/docs/design-system-guide.md
- [ ] T107 [P] Update MrWhoOidc.WebAuth README.md with link to design system guide and quick start instructions
- [ ] T108 [P] Create CHANGELOG entry documenting UI unification changes in docs/CHANGELOG.md

### CSS Organization & Optimization

- [ ] T109 Organize design-system.css into documented sections: Variables, Typography, Buttons, Forms, Cards, Tables, Utilities, Components in MrWhoOidc.WebAuth/wwwroot/css/design-system.css
- [ ] T110 Review site.css: Move any duplicated styles to design-system.css, remove obsolete styles in MrWhoOidc.WebAuth/wwwroot/css/site.css
- [ ] T111 [P] Run CSS validation: Use W3C CSS Validator on design-system.css and site.css
- [ ] T112 [P] Check CSS size: Ensure combined CSS files remain under reasonable size (~150KB uncompressed)

### Accessibility & Performance

- [ ] T113 Run accessibility audit: Test with WAVE or axe DevTools on 10 representative pages
- [ ] T114 Verify color contrast: All text meets WCAG 2.1 AA standards (4.5:1 for normal text, 3:1 for large)
- [ ] T115 Test keyboard navigation: All interactive elements accessible via keyboard on all pages
- [ ] T116 Test screen reader: Navigate through Login, Admin Users, Account Profile with NVDA/JAWS
- [ ] T117 [P] Performance check: Verify no page load time regression, CSS loads efficiently

### Browser & Device Testing

- [ ] T118 [P] Test on Chrome (Windows/Mac): Verify all 10 representative pages
- [ ] T119 [P] Test on Firefox (Windows/Mac): Verify all 10 representative pages
- [ ] T120 [P] Test on Safari (Mac): Verify all 10 representative pages
- [ ] T121 [P] Test on Edge (Windows): Verify all 10 representative pages
- [ ] T122 [P] Test on mobile Safari (iOS): Verify responsive behavior
- [ ] T123 [P] Test on Chrome mobile (Android): Verify responsive behavior

### Final Validation

- [ ] T124 Run final inline style audit: Confirm <5% inline styles remain (only dynamic values)
- [ ] T125 Run final CSS variable audit: Confirm all static colors/sizes reference CSS custom properties
- [ ] T126 Visual regression: Compare final screenshots vs baseline, document any intentional changes
- [ ] T127 Verify tenant branding: Test with custom logo and color on multiple tenants
- [ ] T128 Code review: Have another developer review design-system.css and updated pages for consistency
- [ ] T129 Update spec.md success criteria checklist: Mark all 8 success criteria as achieved
- [ ] T130 Final build: Run dotnet build and confirm zero warnings related to changes

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies - can start immediately
- **Foundational (Phase 2)**: Depends on Setup completion - BLOCKS all user stories
- **User Stories (Phases 3-5)**: All depend on Foundational phase completion
  - User Story 1 (P1): Can start after Foundational - No dependencies on other stories
  - User Story 2 (P2): Can start after Foundational - Depends on US1 for context but independently testable
  - User Story 3 (P3): Can start after US1 and US2 complete (needs components applied to verify patterns)
- **Polish (Phase 6)**: Depends on all user stories being complete

### User Story Dependencies

- **User Story 1 (P1)**: Foundation complete → US1 can proceed independently (MVP!)
- **User Story 2 (P2)**: Foundation complete → US2 can proceed independently (extends US1)
- **User Story 3 (P3)**: US1 + US2 complete → US3 verifies and documents patterns

### Within Each User Story

- **US1**: All page refactoring tasks [P] can run in parallel (different files)
- **US2**: All page refactoring tasks [P] can run in parallel, final audit tasks sequential
- **US3**: Component verification tasks [P] can run in parallel, template creation sequential

### Parallel Opportunities

- **Phase 1**: Tasks T004-T005 can run in parallel with T001-T003
- **Phase 2**: All CSS component creation tasks (T012-T022) can run in parallel after token tasks (T006-T011) complete
- **Phase 3 (US1)**: All auth page tasks (T023-T027) can run in parallel; all admin page tasks (T028-T032) can run in parallel; all error page tasks (T033-T036) can run in parallel
- **Phase 4 (US2)**: All account pages (T045-T053) can run in parallel; all admin config pages (T054-T059) can run in parallel; all platform admin pages (T060-T065) can run in parallel
- **Phase 5 (US3)**: All component verification tasks (T088-T094) can run in parallel; all template creation tasks (T098-T100) can run in parallel
- **Phase 6**: Documentation tasks (T106-T108) can run in parallel; browser testing tasks (T118-T123) can run in parallel

---

## Parallel Example: User Story 1 (Auth Pages)

```bash
# Launch all auth page refactoring tasks together:
Task T023: "Refactor Login.cshtml in MrWhoOidc.WebAuth/Pages/Login.cshtml"
Task T024: "Refactor Consent.cshtml in MrWhoOidc.WebAuth/Pages/Consent.cshtml"
Task T025: "Refactor LoginTotp.cshtml in MrWhoOidc.WebAuth/Pages/LoginTotp.cshtml"
Task T026: "Refactor SelectTenant.cshtml in MrWhoOidc.WebAuth/Pages/SelectTenant.cshtml"
Task T027: "Refactor DiscoverTenant.cshtml in MrWhoOidc.WebAuth/Pages/DiscoverTenant.cshtml"

# These can all execute in parallel - different files, no dependencies
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup (create design-system.css file)
2. Complete Phase 2: Foundational (all CSS tokens and components) - CRITICAL
3. Complete Phase 3: User Story 1 (refactor core pages for visual consistency)
4. **STOP and VALIDATE**: Test 10 representative pages independently
5. Deploy/demo MVP showing consistent visual experience

**MVP Deliverable**: Users see consistent fonts, colors, spacing, and components across login flow and admin dashboards.

### Incremental Delivery

1. Foundation (Setup + Foundational) → Design system ready
2. Add User Story 1 → Test visual consistency → Deploy/Demo (MVP!)
3. Add User Story 2 → Test centralized management → Deploy/Demo (95% inline styles gone)
4. Add User Story 3 → Test component reuse → Deploy/Demo (fully documented design system)
5. Polish → Final validation and cross-browser testing

### Parallel Team Strategy

With multiple developers:

1. Team completes Setup + Foundational together (critical path)
2. Once Foundational is done:
   - Developer A: User Story 1 auth pages (T023-T027)
   - Developer B: User Story 1 admin pages (T028-T032)
   - Developer C: User Story 1 error pages (T033-T036)
3. Then proceed to US2 and US3 in parallel by page category

---

## Task Summary

**Total Tasks**: 130  
**Setup Tasks**: 5  
**Foundational Tasks**: 17  
**User Story 1 Tasks**: 22  
**User Story 2 Tasks**: 43  
**User Story 3 Tasks**: 18  
**Polish Tasks**: 25

**Parallel Opportunities**:
- Phase 2: 15+ tasks can run in parallel
- Phase 3 (US1): 16+ tasks can run in parallel
- Phase 4 (US2): 35+ tasks can run in parallel
- Phase 5 (US3): 12+ tasks can run in parallel
- Phase 6: 10+ tasks can run in parallel

**Independent Test Criteria**:
- US1: Navigate 10 pages, verify visual consistency
- US2: Change CSS variable, verify global update
- US3: Create new page using only documented classes

**Suggested MVP**: Complete through User Story 1 (Tasks T001-T044) for immediate visual consistency improvements.

---

## Notes

- [P] tasks = different files, no dependencies, can run in parallel
- [Story] label maps task to specific user story for traceability
- Each user story is independently completable and testable
- No automated tests - manual visual testing throughout
- Commit after each page refactoring or logical group
- Capture screenshots at checkpoints for comparison
- Verify tenant branding overrides work after refactoring
- Maintain accessibility compliance (WCAG 2.1 AA) throughout
- No C# code changes - pure CSS and Razor markup refactoring
