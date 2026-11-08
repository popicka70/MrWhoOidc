# Feature Specification: Product/UX Follow-ups from the 2026-11-08 E2E Visual Evaluation

**Feature Branch**: `fix/product-ux-followups` (implementation on `fix/product-ux-followups-impl`)
**Created**: 2026-11-08
**Status**: Implemented — all P1/P2/P3 items (T1–T19) closed; decisions for T9/T10 recorded below. Final verification: full E2E re-run pending on the implementation branch.
**Input**: Final E2E LLM evaluation run `20261108_205730` (103 pages, 103 evaluated, avg 6.9/10, 0 high-severity). All score gates now pass; the items below are the non-gating findings that remain as product/UX debt.

**Related work already merged** (`fix/e2e-suite-parallel-safety`, merged to `master` as of `8b196db4`): realms delete guard, configuration-audit filters + CSV export + pagination binding fix, privacy policy, scopes Add/Edit form polish, select-tenant behavior, E2E parallel-safety infrastructure.

## Problem Tracker

| # | Area | Finding | Priority | Status |
|---|------|---------|----------|--------|
| T1 | `/admin/roles` + `/admin/roles/edit` | Built-in roles deletable, no System badge, editable name | P1 | Done — guards via shared `SystemRoles` (UI + API 403s), confirmation page, System badge |
| T2 | Post-create redirects | Realm create lands on edit page; tenant create lands on public landing | P1 | Done — list redirect + shared success flash; platform-admin no longer auto-switches tenants |
| T3 | `/admin/ProviderMappings` | Title literally "Client ? Provider Mappings" (mangled em-dash in source) | P1 | Done — `↔` restored; empty states carded |
| T4 | `/account/create-tenant` | No slug field/auto-suggest, name-only form | P2 | Done — slug field with `/t/` input-group, JS auto-suggest, field-level duplicate validation |
| T5 | `/admin/clients/add` | Heavy crypto section open by default, duplicate label, no client-type selector | P2 | Done — collapsed crypto section, client-type quick pick, duplicate select removed |
| T6 | `/password` | No complexity rules, no back link, layout stretch | P2 | Done — constrained width, complexity guidance, back link |
| T7 | `/admin/roles/add` + edit | No Display Name field | P2 | Done — `Role.DisplayName` column + migration; forms, list, seeder, API |
| T8 | `/admin/branding` | Missing login title, custom CSS, favicon, ToS/Privacy URL fields | P2 | Done — five tenant columns + migration; applied in both layouts (nonce'd CSS) and login page |
| T9 | `/platform-admin/settings` | Missing General/Security/SMTP/Feature-Flags sections | P2 | Done — decision below: sections + anchor nav + test email |
| T10 | `/admin/users/add` | No password field (invite-only by design?) | P2 | Done — decision below: passwordless by design, explained in the UI |
| T11 | `/platform-admin/tenants` | No search/filter, no Enable/Disable, no Delete; Create vs Add label | P2 | Done — live search + status filter, Suspend/Enable, soft-delete confirmation page, "Add Tenant" label |
| T12 | `/platform-admin/tenants/import` | Import submit button visibility/labeling + overwrite warning | P3 | Done — primary Preview button, labeled destructive mode, live + pre-execute overwrite warnings |
| T13 | `/admin/backchannel` | No bulk Retry/Clear, no Next-retry column, empty-state icon | P3 | Done — bulk Retry Failed / Clear Sent, Next-retry column, guided empty state |
| T14 | `/admin/providers` + `/admin/providers/claim-mappings` | Empty-state guidance, Export action, misaligned form | P3 | Done — empty states, export link, realigned form with validation |
| T15 | `/admin/users/emails` | Raw `admin[System Administrator]` breadcrumb, no empty state | P3 | Done — user context line under the h1 on all four tabs, empty state, CSP-safe delete confirm |
| T16 | `/admin/configuration-audit` | Empty state should explain what populates the log + icon | P3 | Done — explains import/export-only logging |
| T17 | `/platform-admin/support-access` | Start button should be warning-colored; prominent audit alert | P3 | Done — warning Start buttons, prominent audited-sessions banner |
| T18 | `/` home | No quick links to OIDC discovery/JWKS endpoints | P3 | Done — endpoints section with absolute, copyable URLs |
| T19 | Example apps | `Welcome, .` bug, `?? "Unknown"` artifacts, raw Unix timestamps, clipped tokens, raw dumps | P3 | Done — fixed in OidcDemo, RazorClient, React client (masked/copyable tokens, readable dates, em-dash placeholders) |

## Recorded Product Decisions (T9, T10)

**T10 — user creation without a password (decided 2026-11-08): passwordless by design.**
Users are created without a password; the person completes first-time sign-in via the
*Forgot password* flow on the sign-in page (the reset link doubles as account setup), or by
signing in through a linked external provider. Users who already hold a global account are
invited instead of re-created. The admin UI states this flow up front on `/admin/users/add`
and repeats it in the post-create success message. No inline password field was added.

**T9 — platform settings sections (decided 2026-11-08): sections + anchor nav + test email.**
`/platform-admin/settings` is organized into titled, anchor-navigated sections —
**General** (root sign-in behavior, QR login at discovery), **Feature Flags** (token
exchange, dynamic client registration), **Security** (initial access tokens) and
**Email (SMTP)**. SMTP is deployment-configuration-driven, so the section shows the
effective `Mail:` values read-only and adds a functional *Send test email* action to verify
delivery end-to-end. No DB-backed SMTP settings were introduced.

**Not carried forward**: `/admin/scopes/edit` scored 5–6/10 with "missing banner/help/switch" — verified stale: the polished page (banner, form-text, switch) is in `master` and baked into the evaluation image; both evaluations passed their gates. Do not re-implement.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Protect built-in roles from destructive edits (Priority: P1)

A tenant administrator manages roles in `/admin/roles`. Built-in roles (`admin`, `user`, and any seeded role the authorization policies depend on) currently show an enabled **Delete** action and an editable **Name** in the edit form, exactly the safety gap the realms delete guard closed for system realms.

**Why this priority**: deleting or renaming a built-in role breaks tenant administration the same way deleting the `admin` realm would. Guard + visual indication prevents accidental self-inflicted outages.

**Independent Test**: as a tenant admin, attempt to delete a built-in role from the list and via the admin API — both must be refused; the UI must hide/disable Delete for built-in roles and badge them.

**Acceptance Scenarios**:

1. **Given** a tenant with built-in roles, **When** the admin opens `/admin/roles`, **Then** built-in roles show a "System" badge and no Delete action.
2. **Given** a built-in role id, **When** the admin calls `DELETE /admin/api/roles/{id}`, **Then** the API responds 403 with a clear message.
3. **Given** the edit page of a built-in role, **When** the form renders, **Then** the Name field is read-only with an explanatory note.
4. **Given** a custom role, **When** the admin edits or deletes it, **Then** the full edit/delete flow works as before.

---

### User Story 2 - Consistent post-create redirects (Priority: P1)

An admin creates a realm (or a platform admin creates a tenant) and is dropped on an unexpected page — realm creation lands on the edit form, and after tenant creation the app can land on the public landing page — leaving the user disoriented about whether the action succeeded.

**Why this priority**: post-action flow determines whether users trust the operation; landing on an unrelated page reads as failure.

**Independent Test**: create a realm and a tenant through the UI; both must land on the respective list page with a success flash message.

**Acceptance Scenarios**:

1. **Given** the Add Realm form, **When** the admin submits valid data, **Then** the app redirects to the realm list and shows a success message containing the new realm name.
2. **Given** the tenant create flow (platform admin), **When** the tenant is created, **Then** the app redirects to the tenant list with a success message.
3. **Given** a validation error, **When** the form re-renders, **Then** the user stays on the form with field-level errors.

---

### User Story 3 - Fix the mangled provider-mappings title (Priority: P1)

`/admin/ProviderMappings` renders the literal title "Client ? Provider Mappings" — an em-dash (or arrow) character was mangled in the source file — and both the provider-mapping pages have a plain "No mappings." empty state with no guidance.

**Why this priority**: visible encoding corruption in a page title is an immediate credibility issue and a one-line fix.

**Independent Test**: open `/admin/provider-mappings` and `/admin/providers/claim-mappings`; titles render the intended character and empty states explain the next step.

**Acceptance Scenarios**:

1. **Given** `/admin/provider-mappings`, **When** the page loads, **Then** the title renders "Client ↔ Provider Mappings" (or equivalent correct character).
2. **Given** no mappings exist, **When** the page loads, **Then** the empty state shows an icon and guidance (e.g. how to add a first mapping) instead of bare "No mappings.".

---

### User Story 4 - Create-tenant form completeness (Priority: P2)

A self-service user creating a tenant at `/account/create-tenant` sees only a Name field; the tenant slug (which becomes part of their URLs) is generated invisibly with no preview or control.

**Why this priority**: the slug is user-facing in every URL; generating it silently removes user control and creates collisions/surprises.

**Independent Test**: create a tenant via the form; the slug field auto-suggests from the name, shows allowed characters, and the created tenant's URLs use exactly the shown slug.

**Acceptance Scenarios**:

1. **Given** the create-tenant form, **When** the user types a name, **Then** a slug preview auto-suggests from it with character hints.
2. **Given** an already-taken slug, **When** the user submits, **Then** a clear validation error appears on the slug field.
3. **Given** a valid submission, **When** the tenant is created, **Then** the confirmation shows the slug-based URLs the user will use.

---

### User Story 5 - Lighter, clearer admin forms (Priority: P2)

Several admin forms are denser or less guided than their house style: clients-add opens the advanced JWT crypto section by default and duplicates the "ID token signed response alg" label with no explicit client-type selector; the change-password page shows no complexity rules and no way back; roles have no Display Name; branding lacks login-page title, custom CSS, favicon URL, and ToS/Privacy URL fields.

**Why this priority**: these are the forms administrators use daily; friction and unclear defaults there compound across every tenant operation.

**Independent Test**: open each form cold; the primary path is visible without expanding advanced sections, every field is labeled once and distinctly, and guidance (help text, complexity rules) accompanies inputs.

**Acceptance Scenarios**:

1. **Given** `/admin/clients/add`, **When** the form loads, **Then** the advanced crypto section is collapsed, labels are unique, and a client-type choice (confidential/public/m2m) is explicit.
2. **Given** `/password`, **When** the form loads, **Then** complexity rules are listed near the fields, and a back link to the account page exists.
3. **Given** `/admin/roles/add`, **When** the form loads, **Then** it offers a Display Name in addition to the Name.
4. **Given** `/admin/branding`, **When** the form loads, **Then** login-page title, custom CSS, favicon URL, and ToS/Privacy URL fields are present.

---

### User Story 6 - Decide product behavior for users-without-password and platform settings (Priority: P2 — needs decision)

The E2E evaluation flagged two intentional-looking gaps that need a product decision before code: `/admin/users/add` has no password field (users may be created passwordless and set passwords via reset — confirm), and `/platform-admin/settings` lacks General/Security/SMTP/Feature-Flags sections (confirm which belong there).

**Why this priority**: implementing either without a decision risks building the wrong thing; documenting the decision prevents the finding from resurfacing every evaluation.

**Independent Test**: a written decision recorded in this spec's tracker (T9/T10) with the chosen behavior implemented or explicitly marked by-design.

**Acceptance Scenarios**:

1. **Given** the decision is "invite-only", **When** an admin creates a user, **Then** the UI explains the passwordless flow (reset link / first-login setup).
2. **Given** the decision is "inline password", **When** an admin creates a user, **Then** the form includes a password field with complexity hints.
3. **Given** `/platform-admin/settings`, **When** the decision is applied, **Then** the page contains exactly the agreed sections, each functional.

---

### User Story 7 - Tenant-list operations and import clarity (Priority: P2/P3)

`/platform-admin/tenants` has no search, no Enable/Disable, and no Delete action, and the button label "Create Tenant" is inconsistent with the admin area's "Add X" convention; the import page's destructive nature (overwrite behavior) is not flagged before submit.

**Why this priority**: platform admins operate on tenants at scale; missing lifecycle actions push them to the API or DB.

**Independent Test**: search, disable, and re-enable a throwaway tenant from the list; start an import and see an explicit warning before any destructive step.

**Acceptance Scenarios**:

1. **Given** several tenants, **When** the admin types in a search field, **Then** the list filters live.
2. **Given** an active tenant, **When** the admin disables it, **Then** its status badge changes and re-enabling restores it.
3. **Given** the import page with a selected file, **When** conflicts are possible, **Then** an explicit overwrite warning is shown next to the submit button, and the submit button is clearly visible and labeled.

---

### User Story 8 - Consistent empty states and list affordances (Priority: P3)

A pass across list pages for consistent empty-state guidance and missing affordances: providers list (no illustration/guidance, no Export), user alternative emails (no empty state; raw `admin[System Administrator]` breadcrumb markup), configuration audit (empty state should explain that import/export operations populate the log), backchannel outbox (missing bulk Retry Failed / Clear Sent actions, Next-retry and token-type columns), support access (warning-colored start button, prominent audit alert).

**Why this priority**: single consistent sweep; each item is small but together they lift the whole admin surface.

**Independent Test**: visit each page in a state with zero rows; every one shows an icon, a one-line explanation, and the primary next action.

**Acceptance Scenarios**:

1. **Given** zero rows on each affected list, **When** the page loads, **Then** an illustrated empty state with guidance appears.
2. **Given** failed backchannel logouts, **When** the admin views the outbox, **Then** bulk Retry Failed and Clear Sent are available and a Next-retry column shows.
3. **Given** the support-access page, **When** it loads, **Then** the start action is warning-colored and the audit notice is prominent.
4. **Given** the user-emails page, **When** the breadcrumb renders, **Then** it shows proper markup (e.g. "admin (System Administrator)").

---

### User Story 9 - Home and example-app polish (Priority: P3)

The public home page offers no quick links to the OIDC discovery/JWKS endpoints; the example apps (Razor client, OIDC demo, React client) show a `Welcome, .` greeting bug, `?? "Unknown"` template artifacts, raw Unix timestamps, horizontally clipped tokens, and raw token/claim dumps without masking or copy affordances.

**Why this priority**: demos are the first thing evaluators and new developers open; small bugs there set expectations for the platform.

**Independent Test**: open each example app authenticated; all claims render with placeholders for missing values, human-readable dates, masked/copyable tokens, and no template artifacts.

**Acceptance Scenarios**:

1. **Given** the home page, **When** a visitor loads it, **Then** discovery and JWKS endpoint links are visible and copyable.
2. **Given** the Razor client with an incomplete profile, **When** the secure page loads, **Then** missing claims show an em-dash placeholder (not empty) and dates render human-readable.
3. **Given** any example app, **When** tokens are displayed, **Then** they are masked by default with a copy control.

## Edge Cases

- Built-in role detection must match the seeder exactly (share constants, as `SystemRealms` does for realms) — otherwise the guard drifts from what is actually seeded.
- Tenant deletion/Disable must cascade safely (data retention, sub-resources, active sessions) — needs its own review before implementing Delete.
- Slug auto-suggest must handle non-Latin names, collisions, and reserved slugs (`default`, `admin`...).
- Import warnings must reflect the actual conflict-resolution modes the backend supports.
- Configuration-audit empty state must not imply the log is broken — the log only records import/export operations.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: System MUST refuse deletion and renaming of built-in roles via both the admin UI and the admin API (403 with explanatory message), sharing the definition of built-in roles with the seeder.
- **FR-002**: Create flows MUST redirect to the owning list page with a success flash naming the created entity.
- **FR-003**: All user-visible strings MUST render correct typography (no mangled/dropped Unicode), with a check that page titles match their house style.
- **FR-004**: The create-tenant form MUST expose the slug with live auto-suggest, character hints, and duplicate validation.
- **FR-005**: Advanced/optional form sections MUST default to collapsed, and every form label MUST be unique within its form.
- **FR-006**: Password forms MUST display the enforced complexity rules adjacent to the inputs.
- **FR-007**: List pages MUST implement the shared empty-state pattern (icon + guidance + primary action) when zero rows match.
- **FR-008**: The tenant list MUST support search, Enable/Disable, and (after data-retention review) Delete with confirmation.
- **FR-009**: Destructive imports MUST show an explicit overwrite warning before submit.
- **FR-010**: Bulk actions on the backchannel outbox (Retry Failed, Clear Sent) MUST be available to tenant admins.
- **FR-011**: Example apps MUST render claims with placeholders for missing values, human-readable timestamps, and masked tokens with copy affordances.
- **FR-012**: Decisions for T9 (platform settings sections) and T10 (user creation without password) MUST be recorded in this spec before implementation.

### Key Entities

- **Built-in/System entities**: realms (already guarded via `SystemRealms`) and roles — the set the seeder creates that authorization policies depend on; deletable only through an explicit, reviewed process.
- **Tenant slug**: the URL-facing identifier derived from (or chosen with) the tenant name; immutable after creation once in use.
- **Empty state**: the standardized zero-rows presentation (icon, explanation, next action) shared across admin lists.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: E2E suite (103 pages, 3 workers, LLM evaluation) stays fully green with zero high-severity findings.
- **SC-002**: Average LLM visual score across all 103 pages rises from 6.9 to ≥ 8.
- **SC-003**: No page scores below 5 in a full evaluation run.
- **SC-004**: All P1 items (T1–T3) closed with unit tests covering the guards, and the shared-constant pattern (seeder ↔ guard) proven by tests.
- **SC-005**: Every list page renders the standard empty state when empty (verifiable by a scripted zero-row check).
