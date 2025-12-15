# Feature Specification: Sticky Footer Layout

**Feature Branch**: `011-sticky-footer-layout`  
**Created**: 2025-12-15  
**Status**: Draft  
**Input**: User description: "Improve MrWhoOidc.WebAuth UI so the bottom bar (footer) is always visible, never overlaps page content, and works well on mobile; solve consistently across the app (preferably via a grid-based layout)."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Footer is always visible (Priority: P1)

As an admin using the MrWhoOidc.WebAuth UI, I want the bottom bar (footer) to always be visible without needing to scroll, so that I can always access footer actions/links and the UI feels consistent.

**Why this priority**: The footer visibility issue is a recurring usability problem and affects every page.

**Independent Test**: Can be tested by visiting multiple pages with different content lengths and confirming the footer is visible at all times.

**Acceptance Scenarios**:

1. **Given** I open any MrWhoOidc.WebAuth page with a typical desktop viewport, **When** the page finishes loading, **Then** the footer is visible without scrolling.
2. **Given** I navigate between pages within MrWhoOidc.WebAuth, **When** each page renders, **Then** the footer remains visible and consistent in placement.

---

### User Story 2 - Content never hides behind the footer (Priority: P2)

As an admin, I want all page content (including the last row of tables and bottom-most buttons) to remain visible and reachable, so that the footer does not block important actions.

**Why this priority**: Overlapping content can prevent users from seeing or clicking controls, causing errors and frustration.

**Independent Test**: Can be tested on a page with enough content to require scrolling and verifying the last interactive elements are not obscured.

**Acceptance Scenarios**:

1. **Given** a page contains content that extends beyond the viewport height, **When** I scroll to the end of the content, **Then** the final content and any interactive controls are fully visible and usable.
2. **Given** a page contains a table or list, **When** I scroll to the bottom, **Then** the last row is not covered by the footer.

---

### User Story 3 - Works well on mobile (Priority: P3)

As an admin using a mobile device, I want the footer layout to adapt to smaller screens, so that the footer remains visible while page content remains readable and usable.

**Why this priority**: The layout should not regress on small screens; mobile users should have a functional experience.

**Independent Test**: Can be tested by using narrow and short viewports and verifying footer visibility and content reachability.

**Acceptance Scenarios**:

1. **Given** I view a page on a small-screen viewport, **When** the page renders, **Then** the footer remains visible and does not hide essential content.
2. **Given** I use touch/scroll gestures on mobile, **When** I scroll through page content, **Then** the footer remains visible and content is still scrollable.

---

### Edge Cases

- Very short viewport heights (e.g., small laptop screens): footer remains visible and the content area remains usable via scrolling.
- Pages with little/no content: footer still appears at the bottom of the viewport (no awkward mid-page footer).
- Pages with long tables/forms: last interactive elements remain reachable and not blocked.
- Keyboard navigation: tabbing can reach content and footer controls without focus being hidden behind the footer.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The UI MUST present a consistent page layout where the footer is visible at all times during normal page use.
- **FR-002**: The footer MUST NOT overlap or obscure page content; no interactive control may be partially or fully hidden behind the footer.
- **FR-003**: When page content exceeds the available vertical space, users MUST be able to scroll the content while the footer remains visible.
- **FR-004**: The primary layout (header/content/footer) MUST remain usable on narrow screens down to 360px width without causing horizontal scrolling.
- **FR-005**: The change MUST be applied consistently across MrWhoOidc.WebAuth pages (i.e., not via page-by-page special cases).
- **FR-006**: The footer MUST remain usable with keyboard navigation and must not cause focus to be trapped or hidden.

**Assumptions / Constraints**:

- Footer content (links/text) remains functionally the same; this feature focuses on layout and visibility.
- The solution should be centralized so future pages inherit the behavior automatically.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: In a manual audit of all major MrWhoOidc.WebAuth pages at 1024x700 or larger, 0 pages require vertical scrolling solely to reveal the footer.
- **SC-002**: In a manual audit of pages with long content, 0 instances are found where the last interactive control is obscured by the footer.
- **SC-003**: On mobile-sized viewports, users can complete primary admin tasks (e.g., view lists, reach bottom-of-page actions) without layout-related blockers attributable to the footer.
- **SC-004**: Reduce internal reports/bugs related to “footer overlaps content” or “footer not visible” to zero for at least one release cycle after rollout.

