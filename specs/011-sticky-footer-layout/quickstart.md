# Quickstart: Sticky Footer Layout

**Goal**: Verify the footer is always visible and never overlaps content.

## Prerequisites

- Run the app as you normally do for MrWhoOidc.WebAuth.
- Use a modern browser with responsive device emulation.

## Verification Checklist

### Desktop (primary)

1. Set viewport to at least 1024x700.
2. Navigate to multiple pages with varying content:
   - A long list/table page (e.g., Admin lists)
   - A form/edit page with bottom buttons
   - A short page with minimal content
3. Confirm:
   - Footer is visible immediately on load (no scroll required to reveal it).
   - Scrolling occurs in the content area (not required to reach the footer).
   - The last row/button is fully visible and clickable (no overlap).

### Mobile

1. Set viewport to 360x800 (or similar).
2. Repeat navigation and confirm:
   - Footer remains visible.
   - Content remains scrollable.
   - No horizontal scrolling is required for the primary layout.

### Accessibility / Keyboard

1. Use keyboard Tab navigation to reach:
   - Bottom-of-page controls
   - Footer links
2. Confirm focus is never hidden behind the footer.

## Expected Outcome

- Footer is always visible.
- No overlap or “hidden behind footer” controls.
- Works consistently across MrWhoOidc.WebAuth pages.
