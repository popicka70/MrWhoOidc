# Feature Specification: WebAuth UI Unification

**Feature Branch**: `005-webauth-ui-unification`  
**Created**: November 11, 2025  
**Status**: Draft  
**Input**: User description: "I want to unify UI in MrWhoOidc.WebAuth project. I want consistent approach to styles, fonts, colors. Find opportunities to improve the design by usage of components. Unify styles by moving the values to project CSS file. Use CSS classes."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Consistent Visual Experience Across All Pages (Priority: P1)

Users navigating through different pages of the WebAuth application (login, consent, admin dashboard, user management, etc.) experience a consistent visual language with unified colors, fonts, spacing, and interactive elements.

**Why this priority**: Visual consistency is fundamental to professional application design and directly impacts user trust, ease of navigation, and brand perception. Without this foundation, all other improvements are compromised.

**Independent Test**: Can be fully tested by navigating through 5-10 representative pages (login, consent, admin users list, client list, settings) and verifying that colors, fonts, button styles, card designs, spacing patterns, and icon usage are identical across all pages.

**Acceptance Scenarios**:

1. **Given** a user navigates from the login page to the consent page, **When** they observe the visual presentation, **Then** font families, sizes, colors, button styles, and spacing are identical
2. **Given** a user browses the admin section (users, clients, roles, settings), **When** they view table headers, action buttons, and form controls, **Then** all elements follow the same visual design patterns
3. **Given** a user views cards and panels across different pages, **When** they compare borders, shadows, and background treatments, **Then** all cards use identical styling properties
4. **Given** a user interacts with buttons across different workflows, **When** they observe primary, secondary, success, danger, and outline button variants, **Then** all buttons have consistent padding, border radius, font weight, and hover states

---

### User Story 2 - Centralized Style Management (Priority: P2)

Developers maintaining the WebAuth application can easily update global styles, colors, and spacing by modifying values in a single centralized CSS file, without hunting through individual page markup.

**Why this priority**: Centralized style management enables rapid design iterations, reduces maintenance burden, prevents style drift, and ensures long-term consistency as new pages are added.

**Independent Test**: Can be fully tested by changing a color variable in the main CSS file (e.g., primary brand color) and verifying that the change is reflected across all pages without modifying individual page files.

**Acceptance Scenarios**:

1. **Given** a developer needs to change the primary brand color, **When** they update the color value in the centralized CSS variables section, **Then** the new color appears on all buttons, links, icons, and branded elements across every page
2. **Given** a developer needs to adjust card border radius globally, **When** they modify the border radius variable in the CSS file, **Then** all cards, buttons, and input fields reflect the new radius without individual page changes
3. **Given** a developer needs to understand spacing conventions, **When** they review the CSS file, **Then** they find clearly documented spacing variables (e.g., margin, padding utilities) with descriptive names
4. **Given** inline style attributes exist in page markup (e.g., `style="max-width: 420px"`), **When** the refactoring is complete, **Then** these are replaced with reusable CSS classes (e.g., `class="auth-container"`)

---

### User Story 3 - Reusable Component Patterns (Priority: P3)

Common UI patterns (page headers with titles and action buttons, data tables with filters, form layouts, alert messages, modal dialogs) are implemented as reusable CSS classes or component patterns that can be consistently applied across pages.

**Why this priority**: Component reuse accelerates development of new features, ensures pixel-perfect consistency, and makes the codebase more maintainable by reducing duplication.

**Independent Test**: Can be fully tested by identifying 3-5 common UI patterns (e.g., page header with icon and action button), extracting them as reusable CSS classes, and applying those classes to 3+ different pages to verify identical appearance.

**Acceptance Scenarios**:

1. **Given** multiple pages display a page header with an icon, title, subtitle, and action button (e.g., Users, Clients, Roles pages), **When** developers apply a standard page header component class, **Then** all headers have identical layout, spacing, and typography
2. **Given** data tables appear on multiple admin pages, **When** developers apply standard table component classes, **Then** all tables have consistent header styling, row hover states, action button groups, and responsive behavior
3. **Given** form pages require consistent input field styling, **When** developers use standardized form classes for labels, inputs, validation messages, and buttons, **Then** all forms have identical visual treatment and spacing
4. **Given** alert messages (success, error, warning, info) appear throughout the application, **When** developers use standardized alert component classes, **Then** all alerts have consistent icons, colors, padding, and dismissal behavior
5. **Given** developers need to create a new admin page, **When** they reference the component pattern documentation or existing pages, **Then** they can compose the page using established CSS classes without writing new inline styles

---

### Edge Cases

- What happens when tenant branding (custom logos, colors) conflicts with the unified design system? (The design system should support tenant branding overrides through CSS variable scoping without breaking core consistency)
- How does the unified design system handle responsive behavior across mobile, tablet, and desktop viewports? (All components must adapt gracefully using responsive CSS classes)
- What happens when inline styles are required for dynamic values (e.g., progress bar width)? (Only truly dynamic values should remain inline; all static styling should move to CSS classes)
- How does the design system handle dark mode or accessibility themes if required in the future? (CSS variables should be structured to support theming through variable overrides)
- What happens when Bootstrap classes conflict with custom CSS classes? (Custom classes should extend Bootstrap thoughtfully, using Bootstrap's utility classes where appropriate and creating custom classes only for application-specific patterns)

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: All pages in the WebAuth project MUST use a consistent set of color variables for primary, secondary, success, danger, warning, info, light, and dark colors
- **FR-002**: All pages MUST use a consistent font family, with defined weights and sizes for headings (h1-h6), body text, labels, and small text
- **FR-003**: All interactive elements (buttons, links, form inputs) MUST have consistent spacing (padding, margin), border radius, and visual states (hover, focus, active, disabled)
- **FR-004**: All card/panel components MUST use consistent border styling, shadow definitions, and background treatments
- **FR-005**: All spacing values (margins, padding) MUST be defined using a consistent spacing scale (e.g., 0.25rem, 0.5rem, 1rem, 1.5rem, 2rem, 3rem, 4rem)
- **FR-006**: Inline style attributes (`style="..."`) MUST be replaced with reusable CSS classes for all static styling values (colors, sizes, spacing, borders)
- **FR-007**: Common UI patterns (page headers, data tables, form groups, alert messages, action button groups) MUST be implemented as reusable CSS classes that can be applied consistently
- **FR-008**: The main project CSS file MUST use CSS custom properties (variables) for all design tokens (colors, spacing, typography, shadows, border radius, transitions)
- **FR-009**: The CSS file MUST be organized into logical sections (variables, typography, buttons, forms, cards, tables, utilities, layout, components) with clear comments
- **FR-010**: Icon usage (Bootstrap Icons) MUST follow consistent sizing patterns and color usage across all pages
- **FR-011**: Responsive behavior MUST be consistent across all pages, with defined breakpoints for mobile, tablet, and desktop layouts
- **FR-012**: Animation and transition effects MUST use consistent timing functions and durations across all interactive elements

### Key Entities

This feature focuses on design consistency and does not introduce new data entities. It refactors presentation layer markup and styles.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: 95% of inline style attributes are replaced with CSS classes (measured by counting `style="..."` occurrences in `.cshtml` files before and after refactoring)
- **SC-002**: All color values in page markup reference centralized CSS variables instead of hardcoded hex/rgb values (measured by grep search for color patterns)
- **SC-003**: Common UI patterns (page headers, data tables, form layouts, alerts) are implemented as reusable CSS classes used on at least 3 different pages each (measured by counting class usage)
- **SC-004**: Developers can change a global design token (primary color, border radius, spacing unit) in one location and see the change reflected across all pages without additional code changes
- **SC-005**: Visual consistency across pages is verified by comparing screenshots of 10 representative pages, showing identical fonts, colors, spacing, and component styling
- **SC-006**: The main CSS file contains clearly documented sections with at least 15 CSS custom properties (variables) for design tokens
- **SC-007**: Code review confirms zero new inline styles are introduced in any page after the unification is complete
- **SC-008**: Developers report 50% faster page creation time when building new admin pages using the unified component classes (measured by time tracking on next feature development)


