# Research: Sticky Footer Layout

**Date**: 2025-12-15
**Branch**: 011-sticky-footer-layout
**Spec**: C:\Users\rum2c\source\repos\MrWhoOidc\specs\011-sticky-footer-layout\spec.md

## Findings

### Current footer implementation sources

- Footer markup exists in the shared layout:
  - C:\Users\rum2c\source\repos\MrWhoOidc\MrWhoOidc.WebAuth\Pages\Shared\_Layout.cshtml
- Footer styling exists in multiple places:
  - Global styles: C:\Users\rum2c\source\repos\MrWhoOidc\MrWhoOidc.WebAuth\wwwroot\css\site.css
  - Layout-scoped CSS isolation: C:\Users\rum2c\source\repos\MrWhoOidc\MrWhoOidc.WebAuth\Pages\Shared\_Layout.cshtml.css

### Root cause of overlap

**Decision**: The overlap is primarily caused by absolute footer positioning in layout-scoped CSS.

- The layout-scoped CSS sets:
  - `.footer { position: absolute; bottom: 0; width: 100%; ... }`
- This conflicts with a “natural flow” footer design in the global `site.css` and can cause content to render underneath the footer unless extra padding/margins are applied per-page.

### Why the existing workaround doesn’t satisfy the requirement

**Decision**: A global bottom-padding workaround can reduce overlap, but it does not guarantee the footer is always visible.

- The prior documented workaround (“KISS”) relies on body padding so content doesn’t get hidden.
- It does not keep the footer visible while scrolling; the footer remains at the end of the document.

## Decisions

### Decision 1: Use a grid-based page shell with a scrollable content region

**Decision**: Use a 3-row grid layout (header / content / footer) where the content region is the scroll container.

**Rationale**:

- Guarantees footer visibility without needing fixed positioning.
- Avoids overlap because the footer occupies its own grid row.
- Centralizes the behavior in shared layouts (applies to all pages).

**Alternatives considered**:

1. **Fixed footer + padding-bottom**
   - Pros: Simple, footer always visible.
   - Cons: Requires hard-coded footer height/padding; higher risk of overlap with responsive footer heights; can be awkward on mobile browser UI.
2. **Sticky footer (position: sticky; bottom: 0)**
   - Pros: Minimal changes.
   - Cons: Not reliable in all scroll container setups; depends on parent overflow behavior; can fail with complex layouts.
3. **Remove absolute positioning only**
   - Pros: Low risk.
   - Cons: Does not satisfy “always visible without scrolling”.

### Decision 2: Prefer modern viewport units for mobile

**Decision**: Prefer `100dvh` (dynamic viewport height) for the page shell minimum height where appropriate.

**Rationale**:

- Improves behavior on mobile browsers where the visible viewport changes as the address bar shows/hides.

## Open Questions

None remaining for planning. Implementation should confirm:

- Sidebar and main content height assumptions (e.g., existing `h-100` usage) still behave when the content region becomes the scroll container.
